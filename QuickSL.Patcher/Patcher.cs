using Mono.Cecil;
using Mono.Cecil.Cil;

namespace QuickSL.Patcher;

/// <summary>
/// 使用 Mono.Cecil 将 QuickSL 初始化代码注入 sts2.dll 的 NGame._Ready() 方法。
/// 注入后游戏启动时自动加载 QuickSL，无需经过 Mod 系统。
/// 存档走原版路径，Steam Cloud 完美同步。
/// </summary>
public static class Patcher
{
    public const string QuickSLTypeName = "QuickSL.QuickSLMod";
    public const string QuickSLMethodName = "Initialize";
    public const string QuickSLAssemblyName = "QuickSL";
    public const string NGameTypeName = "MegaCrit.Sts2.Core.Nodes.NGame";
    public const string InjectTargetMethod = "_Ready";

    /// <summary>
    /// 检查 sts2.dll 是否已经被注入过
    /// </summary>
    public static bool IsPatched(string dataDir)
    {
        var dllPath = Path.Combine(dataDir, "sts2.dll");
        if (!File.Exists(dllPath)) return false;

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(dataDir);

        using var assembly = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters
        {
            ReadSymbols = false,
            AssemblyResolver = resolver
        });

        var nGameType = assembly.MainModule.Types.FirstOrDefault(t => t.FullName == NGameTypeName);
        var readyMethod = nGameType?.Methods.FirstOrDefault(m => m.Name == InjectTargetMethod);
        if (readyMethod?.HasBody != true) return false;

        // 检查是否已有对 QuickSL 的引用
        return readyMethod.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Call &&
            i.Operand is MethodReference mr &&
            mr.DeclaringType.Name == "QuickSLMod" &&
            mr.Name == QuickSLMethodName);
    }

    /// <summary>
    /// 注入 QuickSL.QuickSLMod.Initialize() 调用到 NGame._Ready() 的开头。
    /// 
    /// 注入前:
    ///   _Ready() {
    ///     _window = GetTree().Root;
    ///     ...
    ///   }
    /// 
    /// 注入后:
    ///   _Ready() {
    ///     QuickSLMod.Initialize();    ← 新增
    ///     _window = GetTree().Root;
    ///     ...
    ///   }
    /// </summary>
    public static PatchResult Patch(string dataDir)
    {
        var dllPath = Path.Combine(dataDir, "sts2.dll");
        var backupPath = Path.Combine(dataDir, "sts2.dll.bak");
        var quickslDll = Path.Combine(dataDir, "QuickSL.dll");

        if (!File.Exists(dllPath))
            return PatchResult.Fail("未找到 sts2.dll");

        if (!File.Exists(quickslDll))
            return PatchResult.Fail("未找到 QuickSL.dll，请先将 QuickSL.dll 放到 data 目录");

        // 备份原文件
        if (!File.Exists(backupPath))
        {
            File.Copy(dllPath, backupPath, false);
        }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(dataDir);

        using var assembly = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters
        {
            ReadSymbols = false,
            ReadWrite = true,
            AssemblyResolver = resolver
        });

        var module = assembly.MainModule;

        // 1. 找到 NGame 类
        var nGameType = module.Types.FirstOrDefault(t => t.FullName == NGameTypeName);
        if (nGameType == null)
            return PatchResult.Fail($"未找到类型 {NGameTypeName}");

        // 2. 找到 _Ready 方法
        var readyMethod = nGameType.Methods.FirstOrDefault(m => m.Name == InjectTargetMethod);
        if (readyMethod?.HasBody != true)
            return PatchResult.Fail($"未找到方法 {NGameTypeName}.{InjectTargetMethod}");

        // 3. 检查是否已注入
        if (readyMethod.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Call &&
            i.Operand is MethodReference mr &&
            mr.DeclaringType.Name == "QuickSLMod"))
        {
            return PatchResult.Ok("已经注入过，跳过");
        }

        // 4. 加载 QuickSL 程序集获取目标方法引用
        using var quickslAssembly = AssemblyDefinition.ReadAssembly(quickslDll, new ReaderParameters
        {
            ReadSymbols = false,
            AssemblyResolver = resolver
        });

        var quickslType = quickslAssembly.MainModule.Types.FirstOrDefault(t => t.FullName == QuickSLTypeName);
        if (quickslType == null)
            return PatchResult.Fail($"QuickSL.dll 中未找到 {QuickSLTypeName}");

        var initMethod = quickslType.Methods.FirstOrDefault(m => m.Name == QuickSLMethodName && m.IsStatic);
        if (initMethod == null)
            return PatchResult.Fail($"QuickSL.dll 中未找到 {QuickSLTypeName}.{QuickSLMethodName}()");

        // 5. 导入方法引用到目标模块
        var importedMethod = module.ImportReference(initMethod);

        // 6. 在 _Ready 的最前面注入 Call 指令
        var il = readyMethod.Body.GetILProcessor();
        var firstInstruction = readyMethod.Body.Instructions[0];
        il.InsertBefore(firstInstruction, il.Create(OpCodes.Call, importedMethod));

        // 7. 写回
        assembly.Write();

        return PatchResult.Ok("注入成功");
    }

    /// <summary>
    /// 恢复原始 sts2.dll
    /// </summary>
    public static PatchResult Unpatch(string dataDir)
    {
        var dllPath = Path.Combine(dataDir, "sts2.dll");
        var backupPath = Path.Combine(dataDir, "sts2.dll.bak");

        if (!File.Exists(backupPath))
            return PatchResult.Fail("未找到备份 sts2.dll.bak，无法恢复");

        File.Copy(backupPath, dllPath, true);
        File.Delete(backupPath);

        return PatchResult.Ok("已恢复原始 sts2.dll");
    }
}

public record PatchResult(bool Success, string Message)
{
    public static PatchResult Ok(string msg) => new(true, msg);
    public static PatchResult Fail(string msg) => new(false, msg);
}
