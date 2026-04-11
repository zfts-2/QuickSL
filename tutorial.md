# 杀戮尖塔2 QuickSL Mod 开发教程 (v2.0 直接注入架构)

> 从零开始，理解一个完整的高级 C# 游戏 Mod（直接 DLL 注入机制）是如何开发的。

---

## 目录

1. [项目全貌](#1-项目全貌)
2. [前置知识](#2-前置知识)
3. [为什么放弃官方 Mod 机制？（痛点解析）](#3-为什么放弃官方-mod-机制痛点解析)
4. [核心技术：直接 IL 注入 (Mono.Cecil)](#4-核心技术直接-il-注入-monocecil)
5. [核心技术：Godot 场景树与信号](#5-核心技术godot-场景树与信号)
6. [核心技术：反射 (Reflection)](#6-核心技术反射-reflection)
7. [功能实现：Quick Save/Load 流程](#7-功能实现quick-saveload-流程)
8. [功能实现：死亡回档拦截机制](#8-功能实现死亡回档拦截机制)
9. [安装器设计理念](#9-安装器设计理念)
10. [调试与逆向技巧](#10-调试与逆向技巧)
11. [总结](#11-总结)

---

## 1. 项目全貌

本项目经历了从传统的官方 Mod 加载模式（v1.x）到**直接注入模式（v2.0）**的架构演变。
目前的结构如下：

```text
sts2sl/
├── QuickSL/                    ← Mod 本体（C# DLL）
│   ├── QuickSL.csproj          ← 构建配置（自动检测游戏路径、部署DLL到data目录）
│   └── src/
│       ├── QuickSLMod.cs       ← 初始化入口、Harmony 拦截、SL 业务逻辑
│       ├── QuickSLHandler.cs   ← 常驻在游戏场景树的节点，负责动态注入 UI 和监听生命周期
│       └── QuickSLStyles.cs    ← UI 样式和特效 Shader 代码
├── QuickSL.Installer/          ← 安装器（独立跨平台 CLI/TUI 工具）
│   ├── QuickSL.Installer.csproj
│   └── Program.cs              ← 负责备份原版 dll、注入 IL 到游戏，以及安装状态检查
└── .agents/workflows/          ← 自动化构建工作流脚本
```

**两个项目各自独立构建**，Installer 编译时会将 Mod 的 DLL 嵌入自身资源中，玩家只需下载一个独立的 `QuickSL_Setup.exe` 即可完成全部安装。

---

## 2. 前置知识

| 概念 | 在本项目中的角色 |
|------|-----------------|
| **C# 9.0 / .NET 9** | 作为开发语言。游戏本身使用 Godot 4 + .NET 9 运行。 |
| **Mono.Cecil** | 静态织入工具。用于直接修改游戏的 `sts2.dll` 字节码，在游戏启动入口插入我们的代码。 |
| **Harmony** | 动态拦截库。游戏运行时，拦截（Hook）游戏的保存、删除存档函数。 |
| **反射 (Reflection)** | 破解访问限制。动态调用游戏私有/内置的方法和属性。 |
| **Godot 引擎** | 控制 UI 生命周期的框架。我们与场景树 (SceneTree) 和节点 (Node) 交互。 |

---

## 3. 为什么放弃官方 Mod 机制？（痛点解析）

在 v1.x 版本中，本 Mod 采用游戏官方推荐的 `mod_manifest.json` + `[ModInitializer]` 进行加载。但我们遭遇了一个致命痛点：

**官方系统会将启用了 Mod 的存档隔离到 `modded/profile1/saves` 目录下，导致无法使用 Steam Cloud 同步进度！** 

我们曾经尝试引入文件监听（FileWatcher）将 modded 下的文件实时强行复制到原版目录中，不仅引发了大量竞态条件导致存档损坏，也无法让游戏内部干净地支持原路读档。

**破局方案：直接 DLL 注入**。
通过分析游戏的 `UserDataPathProvider.GetProfileDir()` 我们发现：
- 游戏根据加载的 `Mods.Count > 0` 来将变量 `IsRunningModded` 设为 true，从而在存档路径里添加 `modded/`。
- 如果我们**绕过官方的 Mod 加载器**，使用底层的方法强行调用 `QuickSL.Initialize()`，`Mods.Count` 就还是 0。
- 这样，我们的 Mod 可以随心所欲执行逻辑，同时**完美欺骗游戏使用原生路径，享受 Steam Cloud 自然同步**！

---

## 4. 核心技术：直接 IL 注入 (Mono.Cecil)

如何绕过官方的加载机制？答案是修改游戏的核心程序集。

我们的安装器 (`Installer/Program.cs`) 使用 `Mono.Cecil` 读取了游戏的 `sts2.dll`。

**1. 找到合适的注入点**：
我们定位到 `MegaCrit.Sts2.Core.Nodes.NGame` 类的 `_Ready()` 方法。这是游戏主节点初始化的必经之路。

**2. IL 注入操作**：
```csharp
// 导入我们的 QuickSLMod.Initialize() 方法引用
var importedMethod = module.ImportReference(initMethod);
var il = readyMethod.Body.GetILProcessor();
var firstInstruction = readyMethod.Body.Instructions[0];

// 在原本的第一条代码前面，插入一句 Call QuickSL.QuickSLMod.Initialize()
il.InsertBefore(firstInstruction, il.Create(OpCodes.Call, importedMethod));
```

**3. 处理依赖树 (`sts2.deps.json`)**：
因为这是 .NET Core (而非旧时代 Framework)，游戏运行目录通过 `sts2.deps.json` 文件读取依赖树。就算你把 `QuickSL.dll` 扔到根目录，底层也不认它。
所以安装器还负责动态修改 `deps.json` 文件：
- 在 `targets` 中增加 `QuickSL/0.0.0.0` 作为运行时依赖。
- 在 `sts2/0.1.0` 的依赖列表中显式添加 `QuickSL`。

这相当于硬核的“外科手术”，成功实现了零感知植入。

---

## 5. 核心技术：Godot 场景树与信号

杀戮尖塔2 基于 Godot 并大量使用了控制节点。
我们不需要通过 Godot 编辑器操作，纯代码可以完成所有动态注入。

### 信号机制 (Signals)
我们不使用任何轮询（Update()中每过1帧查一次），那是非常低效的。
我们使用：
```csharp
// C# 中 Godot 信号是以事件 (event) 形式暴露的
GetTree().NodeAdded += OnNodeAdded;
```
每当场景树出现新节点时，就会进入我们的回调中。通过判断 `node.Name` 或 `node.GetType().Name`，精准捕获：
- `NTopBarMapButton` 出现：游戏进入战斗了，我们在它旁边**注入 SL 按钮**。
- `NMainMenu` 出现：我们在 SL 后重载主菜单到了，准备触发“ **自动继续 (Continue)**”。
- `NGameOverContinueButton` 出现：玩家死了，我们在旁边注入 **superSL 后悔药按钮**。

### 延迟调用 (Defer)
修改 UI 结构（AddChild / RemoveChild）如果发生在帧处理中期，极易引发底层 C++ 报错。
必须使用：
```csharp
targetNode.CallDeferred("add_child", mySuperButton);
```

---

## 6. 核心技术：反射 (Reflection)

因为 `sts2.dll` 中的系统模块大多不是 `public`，我们需要暴力调用它们。
通常分为四步：缓存类型 -> 获取属性/方法 -> Invoke -> 容错。

```csharp
// 查找类型
NGameType ??= asm.GetType("MegaCrit.Sts2.Core.Nodes.NGame");

// 获取实例 (单例往往挂在静态属性 Instance 上)
var nGame = NGameType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

// 提取私有方法并执行
var method = NGameType?.GetMethod("ReturnToMainMenu", BindingFlags.Public | BindingFlags.Instance);
method.Invoke(nGame, null);
```

---

## 7. 功能实现：Quick Save/Load 流程

Quick SL 并非真正去干涉游戏底层的序列化写入逻辑。
它本质是一个宏 (Macro)，自动组合了官方现存的两个稳定功能：

1. **自动保存并退出**：模拟触发 `ReturnToMainMenu()`。游戏底层十分强壮，会自动处理好这一帧的战斗状态录入磁盘。
2. **状态传递**：我们内部设置 `AutoContinuePending = true`。
3. **自动继续**：当监听到 `NMainMenu` 加载进内存时，调用私有方法 `OnContinueButtonPressed()`，读取当前的最新的 `current_run.save`，并清理 pending 状态。

由于这是基于官方自身的安全逻辑，从根本上杜绝了脏读和状态不同步。

---

## 8. 功能实现：死亡回档拦截机制

除了普通的 SL，我们还做了一个高级功能："超级SL" (死亡回档)。

当血量归零，游戏常规逻辑会立即调用 `DeleteCurrentRun` / `DeleteCurrentMultiplayerRun` 强行删档。
我们使用 **Harmony** 进行 AOP (面向切面编程) 拦截：

```csharp
[HarmonyPrefix]
static bool BackupBeforeDelete(object __instance)
{
    if (AllowDelete) return true; // 如果是玩家手动放弃，放行

    if (IsVictory()) return true; // 赢了正常结算，放行
    
    // 如果是死亡导致，拦截删除指令！存档被扣留保护。
    SavedRunSaveManager = __instance; 
    return false; // 返回 false 阻止原函数继续执行
}
```

随后，在 Game Over 画面注入我们的 `superSL` 按钮。玩家点击后：
1. 拦截了删档指令，但直接呼唤主控：`ReturnToMainMenu()`。
2. 因为主控回到了主菜单，此时存档文件安然无恙，游戏判定你还保留着“意外断电前”的存档。
3. 接着自动触发 Continue 流程。死而复生完成。

如果不点击超级SL按钮，而是点击官方的“继续（放弃）”，我们将 `AllowDelete` 置为 `true` 并重新触发删除。

---

## 9. 安装器设计理念

一个 Mod 如果仅仅是由开发者爽，那么它是失败的。

我们的安装器采用了非常清晰的单文件可执行部署架构（Single-File Self-Contained）。
玩家不需要装 .NET 开发环境，拿到的是一个 20M 的独立 `exe`。

**自动寻找游戏位置**
通过读取 Windows 侧各种 Steam 相关的注册表键：
- `SOFTWARE\Valve\Steam\InstallPath`
- 以及备选的 `libraryfolders.vdf` 找出 Steam 跨盘符的所有子库。
自动定位到杀戮尖塔2 的安装位置，极大地平滑了小白用户的操作。

**自我修复与无缝卸载**
安装时不仅执行 IL 注入和 `deps.json` 修补，还在同目录下产生 `.bak` 文件。当玩家选择“卸载”时，将原封不动地替换回去。即使安装器意外删除了，玩家使用 Steam 官方界面校验文件完整性，也会自然将其重置清除，不会造成灾难性的永久损坏。

---

## 10. 调试与逆向技巧

- **逆向分析工具**：本教程伴随开发中编写过多次 `DllScanner` 工具进行分析探索。直接加载目标通过 IL 迭代，比各种反编译器定位字符串更快。
- **神级日志节点**：游戏本身在 `%AppData%\SlayTheSpire2\logs\godot.log` 不断产生日志，我们的 `GD.Print` 也混迹其中。过滤关键的 Exception 或是自身的标记 (`[QuickSL]`) 是重中之重。
- **保护场景树**：当你在注入 UI 报错但看似合理时，牢记 Godot 对于节点树的保护机制，善用 `CallDeferred` 与短暂的 `CreateTimer`。

---

## 11. 总结

从最初用 `FileWatcher` 死磕强行同步存档被竞态条件虐得体无完肤，到如今理解 `UserDataPathProvider`，通过 Mono.Cecil 进行 IL 外科手术，我们得到了一套**纯原生理发般顺滑、自然支持云同步的高级直接注入方案**。

这不仅仅是一个 QuickSL 功能的实现，更是探索 `.NET` 和 Godot 底层的良好范本。
希望你的下一个 Mod 开发更出彩。
