using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace QuickSL.Installer;

class Program
{
    const string GameName = "Slay the Spire 2";
    const string DisplayName = "Quick Save/Load (快速SL)";
    const string Version = "2.1.0";

    // 注入目标常量
    const string NGameTypeName = "MegaCrit.Sts2.Core.Nodes.NGame";
    const string InjectTargetMethod = "_Ready";
    const string QuickSLTypeName = "QuickSL.QuickSLMod";
    const string QuickSLInitMethod = "Initialize";

    static string? _gamePath;
    static string? _dataDir;

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.CursorVisible = false;
        try { Console.Title = $"{DisplayName} v{Version}"; } catch { /* macOS terminal may not support */ }

        PrintBanner();

        // 定位游戏
        var mode = ArrowMenu("请选择游戏路径获取方式:", new[]
        {
            "🔍 Steam 模式（自动检测游戏路径）",
            "📂 本文件已放在游戏安装目录下",
        });
        Console.WriteLine();

        if (mode == 0)
        {
            _gamePath = FindGamePathSteam();
            if (_gamePath == null)
            {
                PrintError("❌ 未找到《杀戮尖塔2》安装目录！");
                PrintInfo("   请确认游戏已通过 Steam 安装，或将本程序放到游戏目录后重试。");
                WaitAndExit(1); return;
            }
        }
        else
        {
            var cwd = Environment.CurrentDirectory;
            if (!IsGameDir(cwd))
            {
                PrintError("❌ 当前目录不是《杀戮尖塔2》安装目录！");
                PrintInfo($"   当前目录: {cwd}");
                WaitAndExit(1); return;
            }
            _gamePath = cwd;
        }

        _dataDir = DetectDataDir(_gamePath);
        if (_dataDir == null)
        {
            PrintError("❌ 未找到游戏 data 目录（data_sts2_*）！");
            WaitAndExit(1); return;
        }
        Console.WriteLine();

        // 主菜单
        var choice = ArrowMenu("请选择操作:", new[]
        {
            "⚡ 安装 / 更新 QuickSL",
            "🗑️  卸载 QuickSL",
            "🔍 检查安装状态",
            "退出",
        });
        Console.WriteLine();

        switch (choice)
        {
            case 0: Install(); break;
            case 1: Uninstall(); break;
            case 2: CheckStatus(); break;
            default: return;
        }

        Console.WriteLine();
        Console.CursorVisible = true;
        PrintInfo("按任意键退出...");
        Console.ReadKey(true);
    }

    // ── 安装 ─────────────────────────────────────────────────

    static void Install()
    {
        PrintHeader("安装 QuickSL（直接注入模式）");

        // 0. 自动清理旧版 Mod
        var oldModDir = Path.Combine(_gamePath!, "mods", "QuickSL");
        if (Directory.Exists(oldModDir))
        {
            try
            {
                Directory.Delete(oldModDir, true);
                PrintSuccess("  ✅ 已自动清理旧版 Mod 模式安装");
                var modsDir = Path.Combine(_gamePath!, "mods");
                if (Directory.Exists(modsDir) && Directory.GetFileSystemEntries(modsDir).Length == 0)
                    Directory.Delete(modsDir);
            }
            catch { }
        }
        
        // 1. 释放 QuickSL.dll
        PrintInfo("  步骤 1/3：释放 QuickSL.dll...");
        var asm = Assembly.GetExecutingAssembly();
        using (var stream = asm.GetManifestResourceStream("QuickSL.dll"))
        {
            if (stream == null)
            {
                PrintError("  ❌ 安装包中未找到 QuickSL.dll");
                return;
            }
            var destPath = Path.Combine(_dataDir!, "QuickSL.dll");
            using var fs = File.Create(destPath);
            stream.CopyTo(fs);
            PrintSuccess($"  ✅ QuickSL.dll ({stream.Length:N0} bytes)");
        }

        // 2. 备份 sts2.dll
        PrintInfo("  步骤 2/3：备份 sts2.dll...");
        var sts2Path = Path.Combine(_dataDir!, "sts2.dll");
        var backupPath = Path.Combine(_dataDir!, "sts2.dll.bak");
        if (!File.Exists(backupPath))
        {
            File.Copy(sts2Path, backupPath, false);
            PrintSuccess("  ✅ 已备份 sts2.dll → sts2.dll.bak");
        }
        else
        {
            PrintInfo("  ✓ 备份已存在，跳过");
        }

        // 3. 注入 sts2.dll
        PrintInfo("  步骤 3/4：注入 QuickSL 到 sts2.dll...");
        try
        {
            var result = PatchSts2();
            if (result.Success)
                PrintSuccess($"  ✅ {result.Message}");
            else
            {
                PrintError($"  ❌ {result.Message}");
                return;
            }
        }
        catch (Exception ex)
        {
            PrintError($"  ❌ 注入失败: {ex.Message}");
            PrintInfo("  请确认游戏未在运行中。");
            return;
        }

        // 4. 注册到 deps.json
        PrintInfo("  步骤 4/4：注册 QuickSL 到 sts2.deps.json...");
        try
        {
            var depsResult = PatchDepsJson();
            if (depsResult.Success)
                PrintSuccess($"  ✅ {depsResult.Message}");
            else
                PrintWarning($"  ⚠ {depsResult.Message}");
        }
        catch (Exception ex)
        {
            PrintWarning($"  ⚠ deps.json 修补失败: {ex.Message}（可能不影响运行）");
        }

        Console.WriteLine();
        PrintSuccess("  ════════════════════════════════════════");
        PrintSuccess("  安装完成！启动游戏即可使用");
        PrintSuccess("  在游戏中顶栏会出现 [SL] 按钮");
        PrintSuccess("  ════════════════════════════════════════");
    }

    // ── 注入逻辑 ─────────────────────────────────────────────

    static (bool Success, string Message) PatchSts2()
    {
        var sts2Path = Path.Combine(_dataDir!, "sts2.dll");
        var quickslPath = Path.Combine(_dataDir!, "QuickSL.dll");

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(_dataDir!);

        using var assembly = AssemblyDefinition.ReadAssembly(sts2Path, new ReaderParameters
        {
            ReadSymbols = false,
            ReadWrite = true,
            AssemblyResolver = resolver
        });

        var module = assembly.MainModule;

        // 找到 NGame._Ready()
        var nGameType = module.Types.FirstOrDefault(t => t.FullName == NGameTypeName);
        if (nGameType == null)
            return (false, $"未找到 {NGameTypeName}");

        var readyMethod = nGameType.Methods.FirstOrDefault(m => m.Name == InjectTargetMethod);
        if (readyMethod?.HasBody != true)
            return (false, $"未找到 {NGameTypeName}.{InjectTargetMethod}");

        // 检查是否已注入
        if (readyMethod.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Call &&
            i.Operand is MethodReference mr &&
            mr.DeclaringType.Name == "QuickSLMod" &&
            mr.Name == QuickSLInitMethod))
        {
            return (true, "已经注入过，跳过注入步骤（DLL 已更新）");
        }

        // 加载 QuickSL.dll 获取方法引用
        using var quickslAssembly = AssemblyDefinition.ReadAssembly(quickslPath, new ReaderParameters
        {
            ReadSymbols = false,
            AssemblyResolver = resolver
        });

        var quickslType = quickslAssembly.MainModule.Types.FirstOrDefault(t => t.FullName == QuickSLTypeName);
        if (quickslType == null)
            return (false, $"QuickSL.dll 中未找到 {QuickSLTypeName}");

        var initMethod = quickslType.Methods.FirstOrDefault(m => m.Name == QuickSLInitMethod && m.IsStatic);
        if (initMethod == null)
            return (false, $"QuickSL.dll 中未找到 {QuickSLInitMethod}()");

        // 导入并注入
        var importedMethod = module.ImportReference(initMethod);
        var il = readyMethod.Body.GetILProcessor();
        var firstInstruction = readyMethod.Body.Instructions[0];
        il.InsertBefore(firstInstruction, il.Create(OpCodes.Call, importedMethod));

        assembly.Write();
        return (true, "注入成功！QuickSL.Initialize() 已植入 NGame._Ready()");
    }

    static (bool Success, string Message) PatchDepsJson()
    {
        var depsPath = Path.Combine(_dataDir!, "sts2.deps.json");
        var depsBackupPath = Path.Combine(_dataDir!, "sts2.deps.json.bak");

        if (!File.Exists(depsPath))
            return (false, "未找到 sts2.deps.json");

        // 备份
        if (!File.Exists(depsBackupPath))
            File.Copy(depsPath, depsBackupPath);

        var json = JsonNode.Parse(File.ReadAllText(depsPath))!;
        // 自动检测 target key（macOS 可能有多个 target，需找包含 sts2 条目的那个）
        var targetsObj = json["targets"]!.AsObject();
        var targetKey = targetsObj.Select(kv => kv.Key)
            .FirstOrDefault(k => k.Contains(".NETCoreApp") && targetsObj[k]?["sts2/0.1.0"] != null)
            ?? targetsObj.Select(kv => kv.Key)
            .FirstOrDefault(k => k.Contains(".NETCoreApp"));
        if (targetKey == null)
            return (false, "deps.json 中未找到 .NETCoreApp target");
        var targets = targetsObj[targetKey]!;

        // 添加 QuickSL 作为 sts2 的依赖
        var sts2Entry = targets["sts2/0.1.0"];
        if (sts2Entry == null)
            return (false, $"在 target '{targetKey}' 中未找到 sts2/0.1.0 条目");

        var deps = sts2Entry["dependencies"]!.AsObject();
        bool changed = false;

        if (!deps.ContainsKey("QuickSL"))
        {
            deps.Add("QuickSL", "0.0.0.0");
            changed = true;
        }

        // 添加 QuickSL 运行时条目
        if (targets["QuickSL/0.0.0.0"] == null)
        {
            targets.AsObject().Add("QuickSL/0.0.0.0", new JsonObject
            {
                ["runtime"] = new JsonObject
                {
                    ["QuickSL.dll"] = new JsonObject()
                }
            });
            changed = true;
        }

        // 添加到 libraries
        var libraries = json["libraries"]?.AsObject();
        if (libraries != null && !libraries.ContainsKey("QuickSL/0.0.0.0"))
        {
            libraries.Add("QuickSL/0.0.0.0", new JsonObject
            {
                ["type"] = "reference",
                ["serviceable"] = false,
                ["sha512"] = ""
            });
            changed = true;
        }

        if (!changed)
            return (true, "deps.json 已包含 QuickSL，跳过");

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(depsPath, json.ToJsonString(options));
        return (true, "QuickSL 已注册到 sts2.deps.json");
    }

    static bool IsPatched()
    {
        try
        {
            var sts2Path = Path.Combine(_dataDir!, "sts2.dll");
            if (!File.Exists(sts2Path)) return false;

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(_dataDir!);

            using var assembly = AssemblyDefinition.ReadAssembly(sts2Path, new ReaderParameters
            {
                ReadSymbols = false,
                AssemblyResolver = resolver
            });

            var nGameType = assembly.MainModule.Types.FirstOrDefault(t => t.FullName == NGameTypeName);
            var readyMethod = nGameType?.Methods.FirstOrDefault(m => m.Name == InjectTargetMethod);
            if (readyMethod?.HasBody != true) return false;

            return readyMethod.Body.Instructions.Any(i =>
                i.OpCode == OpCodes.Call &&
                i.Operand is MethodReference mr &&
                mr.DeclaringType.Name == "QuickSLMod");
        }
        catch { return false; }
    }

    // ── 卸载 ─────────────────────────────────────────────────

    static void Uninstall()
    {
        PrintHeader("卸载 QuickSL");

        // 1. 恢复 sts2.dll
        var backupPath = Path.Combine(_dataDir!, "sts2.dll.bak");
        if (File.Exists(backupPath))
        {
            try
            {
                File.Copy(backupPath, Path.Combine(_dataDir!, "sts2.dll"), true);
                File.Delete(backupPath);
                PrintSuccess("  ✅ 已恢复原始 sts2.dll");
            }
            catch (Exception ex)
            {
                PrintError($"  ❌ 恢复 sts2.dll 失败: {ex.Message}");
                PrintInfo("  请确认游戏未在运行中。");
                return;
            }
        }
        else
        {
            PrintWarning("  ⚠ 未找到 sts2.dll.bak，可能需要通过 Steam 验证游戏文件完整性来恢复");
        }

        // 2. 删除 QuickSL.dll
        var quickslPath = Path.Combine(_dataDir!, "QuickSL.dll");
        if (File.Exists(quickslPath))
        {
            try
            {
                File.Delete(quickslPath);
                PrintSuccess("  ✅ 已删除 QuickSL.dll");
            }
            catch (Exception ex)
            {
                PrintError($"  ❌ 删除 QuickSL.dll 失败: {ex.Message}");
            }
        }

        // 3. 恢复 deps.json
        var depsBackup = Path.Combine(_dataDir!, "sts2.deps.json.bak");
        if (File.Exists(depsBackup))
        {
            try
            {
                File.Copy(depsBackup, Path.Combine(_dataDir!, "sts2.deps.json"), true);
                File.Delete(depsBackup);
                PrintSuccess("  ✅ 已恢复原始 sts2.deps.json");
            }
            catch { }
        }

        PrintSuccess("  卸载完成！");
        PrintInfo("  提示: 也可以通过 Steam 验证游戏文件完整性来恢复原始状态。");
    }

    // ── 检查状态 ──────────────────────────────────────────────

    static void CheckStatus()
    {
        PrintHeader("安装状态检查");
        Console.WriteLine();

        PrintInfo($"  游戏目录:   {_gamePath}");
        PrintInfo($"  数据目录:   {_dataDir}");
        Console.WriteLine();

        bool patched = IsPatched();
        bool hasDll = File.Exists(Path.Combine(_dataDir!, "QuickSL.dll"));
        bool hasBackup = File.Exists(Path.Combine(_dataDir!, "sts2.dll.bak"));
        bool hasDepsBackup = File.Exists(Path.Combine(_dataDir!, "sts2.deps.json.bak"));
        bool hasOldMod = Directory.Exists(Path.Combine(_gamePath!, "mods", "QuickSL"));

        // 综合状态
        if (patched && hasDll)
            PrintSuccess("  综合状态:  ✅ 已安装（直接注入模式）");
        else if (hasDll)
            PrintWarning("  综合状态:  ⚠ QuickSL.dll 存在但未注入");
        else
            PrintError("  综合状态:  ❌ 未安装");

        Console.WriteLine();

        // 详细
        PrintCheck(hasDll,         "QuickSL.dll");
        PrintCheck(patched,        "sts2.dll 注入");
        PrintCheck(hasDepsBackup,  "deps.json 注册");
        PrintCheck(hasBackup,      "sts2.dll.bak 备份");

        if (hasOldMod)
        {
            Console.WriteLine();
            PrintWarning("  ⚠ 检测到旧版 Mod 模式安装（mods/QuickSL/）");
            PrintInfo("    重新安装时会自动清理");
        }
    }

    static void PrintCheck(bool ok, string label)
    {
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write(ok ? "  ✅ " : "  ❌ ");
        Console.ResetColor();
        Console.WriteLine(label);
    }

    // ── 查找游戏 ─────────────────────────────────────────────

    static string? FindGamePathSteam()
    {
        var steam = GetSteamPath();
        if (steam != null)
        {
            var p = Path.Combine(steam, "steamapps", "common", GameName);
            if (IsGameDir(p)) return p;
            foreach (var lib in GetSteamLibraries(steam))
            {
                p = Path.Combine(lib, "steamapps", "common", GameName);
                if (IsGameDir(p)) return p;
            }
        }

        // 平台特定的备选路径
        var fallbacks = OperatingSystem.IsMacOS()
            ? new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "Steam") }
            : new[] {
                @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam",
                @"D:\Steam", @"E:\Steam", @"F:\Steam",
                @"D:\SteamLibrary", @"E:\SteamLibrary", @"F:\SteamLibrary" };

        foreach (var root in fallbacks)
        {
            var p = Path.Combine(root, "steamapps", "common", GameName);
            if (IsGameDir(p)) return p;
            // 也搜索 Steam Library 子目录
            foreach (var lib in GetSteamLibraries(root))
            {
                p = Path.Combine(lib, "steamapps", "common", GameName);
                if (IsGameDir(p)) return p;
            }
        }
        return null;
    }

    static bool IsGameDir(string path)
    {
        if (!Directory.Exists(path)) return false;
        // 检查任意 data_sts2_* 目录下是否有 sts2.dll
        if (DetectDataDir(path) != null) return true;
        // Windows 可执行文件
        if (File.Exists(Path.Combine(path, "SlayTheSpire2.exe"))) return true;
        // macOS .app bundle
        if (Directory.Exists(Path.Combine(path, "Slay the Spire 2.app"))) return true;
        return false;
    }

    /// <summary>自动检测 data_sts2_* 目录（Windows 平铺 / macOS .app bundle）</summary>
    static string? DetectDataDir(string gamePath)
    {
        if (!Directory.Exists(gamePath)) return null;
        try
        {
            // 搜索路径列表：游戏根目录 + macOS .app bundle 内部
            var searchRoots = new List<string> { gamePath };

            // macOS: SlayTheSpire2.app/Contents/Resources/
            foreach (var app in Directory.GetDirectories(gamePath, "*.app"))
            {
                var resources = Path.Combine(app, "Contents", "Resources");
                if (Directory.Exists(resources))
                    searchRoots.Add(resources);
            }

            // 优先 arm64（Apple Silicon），然后其他
            string? fallback = null;
            foreach (var root in searchRoots)
            {
                foreach (var dir in Directory.GetDirectories(root, "data_sts2_*"))
                {
                    if (File.Exists(Path.Combine(dir, "sts2.dll")))
                    {
                        if (dir.Contains("arm64"))
                            return dir; // 优先 ARM64
                        fallback ??= dir;
                    }
                }
            }
            return fallback;
        }
        catch { }
        return null;
    }

    static string? GetSteamPath()
    {
        // macOS
        if (OperatingSystem.IsMacOS())
        {
            var macSteam = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "Steam");
            if (Directory.Exists(macSteam)) return macSteam;
            return null;
        }

        // Windows: 通过注册表查找
        if (OperatingSystem.IsWindows())
        {
            try { return GetSteamPathFromRegistry(); } catch { }
        }
        return null;
    }

    /// <summary>Windows 注册表查找 Steam 路径（仅 Windows 编译时可用）</summary>
    static string? GetSteamPathFromRegistry()
    {
#if WINDOWS
        try { using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam")
                          ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
              if (k?.GetValue("InstallPath") is string p && Directory.Exists(p)) return p; } catch { }
        try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
              if (k?.GetValue("SteamPath") is string p && Directory.Exists(p)) return p; } catch { }
#endif
        return null;
    }

    static List<string> GetSteamLibraries(string steamPath)
    {
        var result = new List<string>();
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return result;
        try
        {
            foreach (var line in File.ReadAllLines(vdf))
            {
                var t = line.Trim();
                if (!t.StartsWith("\"path\"")) continue;
                var parts = t.Split('"');
                if (parts.Length >= 4)
                {
                    var lp = parts[3].Replace(@"\\", @"\");
                    if (Directory.Exists(lp)) result.Add(lp);
                }
            }
        }
        catch { }
        return result;
    }

    // ── TUI 组件 ─────────────────────────────────────────────

    static int ArrowMenu(string title, string[] options)
    {
        PrintHeader(title);
        Console.WriteLine();
        int sel = 0, top = Console.CursorTop;
        Draw(options, sel, top);

        while (true)
        {
            switch (Console.ReadKey(true).Key)
            {
                case ConsoleKey.UpArrow:   sel = (sel - 1 + options.Length) % options.Length; Draw(options, sel, top); break;
                case ConsoleKey.DownArrow: sel = (sel + 1) % options.Length; Draw(options, sel, top); break;
                case ConsoleKey.Enter:     Draw(options, sel, top, true); return sel;
            }
        }
    }

    static void Draw(string[] options, int sel, int top, bool confirmed = false)
    {
        Console.SetCursorPosition(0, top);
        for (int i = 0; i < options.Length; i++)
        {
            if (i == sel)
            {
                Console.ForegroundColor = confirmed ? ConsoleColor.Green : ConsoleColor.Black;
                Console.BackgroundColor = confirmed ? ConsoleColor.DarkGreen : ConsoleColor.Cyan;
                Console.Write($"  ▸ {options[i]}  ");
            }
            else
            {
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"    {options[i]}");
            }
            Console.ResetColor();
            try { Console.Write(new string(' ', Math.Max(0, Console.WindowWidth - Console.CursorLeft - 1))); } catch { }
            Console.WriteLine();
        }
        Console.ResetColor();
    }

    // ── 输出工具 ─────────────────────────────────────────────

    static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n  ╔══════════════════════════════════════════════╗");
        Console.WriteLine($"  ║   ⚡ {DisplayName,-36}   ║");
        Console.WriteLine($"  ║      v{Version} 安装器   by typnosis   ║");
        Console.WriteLine($"  ╚══════════════════════════════════════════════╝\n");
        Console.ResetColor();
    }

    static void PrintHeader(string t)  { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"  ▶ {t}"); Console.ResetColor(); }
    static void PrintSuccess(string t) { Console.ForegroundColor = ConsoleColor.Green;  Console.WriteLine(t); Console.ResetColor(); }
    static void PrintInfo(string t)    { Console.ForegroundColor = ConsoleColor.Gray;   Console.WriteLine(t); Console.ResetColor(); }
    static void PrintWarning(string t) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(t); Console.ResetColor(); }
    static void PrintError(string t)   { Console.ForegroundColor = ConsoleColor.Red;    Console.WriteLine(t); Console.ResetColor(); }

    static void WaitAndExit(int code)
    {
        Console.CursorVisible = true;
        Console.WriteLine();
        PrintInfo("按任意键退出...");
        Console.ReadKey(true);
        Environment.Exit(code);
    }
}
