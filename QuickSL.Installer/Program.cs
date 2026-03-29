using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace QuickSL.Installer;

enum MigrateDirection { VanillaToModded, ModdedToVanilla }

class Program
{
    const string GameName = "Slay the Spire 2";
    const string ModName = "QuickSL";
    const string DisplayName = "Quick Save/Load (快速SL)";
    const string Version = "1.1.0";

    static readonly string[] ModFiles = { "QuickSL.dll", "mod_manifest.json" };

    static string? _gamePath;
    static string? _modDir;

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.CursorVisible = false;
        Console.Title = $"{DisplayName} v{Version}";

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

        _modDir = Path.Combine(_gamePath, "mods", ModName);
        bool installed = Directory.Exists(_modDir) && File.Exists(Path.Combine(_modDir, "QuickSL.dll"));
        PrintSuccess($"  游戏目录: {_gamePath}");
        PrintInfo(installed ? "  Mod 状态: ✅ 已安装" : "  Mod 状态: ❌ 未安装");
        Console.WriteLine();

        // 主菜单
        var choice = ArrowMenu("请选择操作:", new[]
        {
            "⚡ 安装 / 更新 Mod",
            "⚡ 安装 / 更新 Mod（含存档迁移）",
            "📦 迁移存档（原版 → Mod 版）",
            "📦 迁移存档（Mod 版 → 原版）",
            "🗑️  卸载 Mod",
            "退出",
        });
        Console.WriteLine();

        switch (choice)
        {
            case 0: Install(migrate: false); break;
            case 1: Install(migrate: true); break;
            case 2: DoMigrate(MigrateDirection.VanillaToModded); break;
            case 3: DoMigrate(MigrateDirection.ModdedToVanilla); break;
            case 4: Uninstall(); break;
            case 5: return;
        }

        Console.WriteLine();
        Console.CursorVisible = true;
        PrintInfo("按任意键退出...");
        Console.ReadKey(true);
    }

    // ── 安装 / 卸载 ─────────────────────────────────────────

    static void Install(bool migrate)
    {
        PrintHeader("安装 Mod");
        Directory.CreateDirectory(_modDir!);
        var asm = Assembly.GetExecutingAssembly();

        foreach (var file in ModFiles)
        {
            using var stream = asm.GetManifestResourceStream(file);
            if (stream == null) { PrintWarning($"  ⚠ 跳过 {file}"); continue; }
            using var fs = File.Create(Path.Combine(_modDir!, file));
            stream.CopyTo(fs);
            PrintSuccess($"  ✅ {file} ({stream.Length:N0} bytes)");
        }
        PrintSuccess($"  已安装到: {_modDir}");

        if (migrate)
        {
            Console.WriteLine();
            Migrate(MigrateDirection.VanillaToModded);
        }

        Console.WriteLine();
        PrintSuccess("  ════════════════════════════════════════");
        PrintSuccess("  安装完成！启动游戏即可使用");
        PrintSuccess("  在游戏房间顶栏会出现 [SL] 按钮");
        PrintSuccess("  ════════════════════════════════════════");
    }

    static void Uninstall()
    {
        PrintHeader("卸载 Mod");
        if (!Directory.Exists(_modDir!) || !File.Exists(Path.Combine(_modDir!, "QuickSL.dll")))
        {
            PrintInfo("  Mod 未安装，无需卸载。");
            return;
        }

        try
        {
            foreach (var file in ModFiles)
            {
                var path = Path.Combine(_modDir!, file);
                if (File.Exists(path)) { File.Delete(path); PrintSuccess($"  ✅ 已删除 {file}"); }
            }
            if (Directory.Exists(_modDir!) && Directory.GetFileSystemEntries(_modDir!).Length == 0)
            {
                Directory.Delete(_modDir!);
                PrintSuccess("  ✅ 已删除目录");
            }
            PrintSuccess("  卸载完成！");
            PrintInfo("  注: 存档数据保留在 AppData 中，不受影响。");
        }
        catch (Exception ex)
        {
            PrintError($"  卸载失败: {ex.Message}");
            PrintInfo("  请确认游戏未在运行中。");
        }
    }

    // ── 存档迁移 ─────────────────────────────────────────────

    static void DoMigrate(MigrateDirection dir)
    {
        var label = dir == MigrateDirection.VanillaToModded ? "原版 → Mod 版" : "Mod 版 → 原版";
        PrintHeader($"迁移存档（{label}）");
        PrintWarning("  ⚠ 注意: 其他 Mod 若修改了存档结构，迁移后的存档可能无法使用。");
        PrintWarning("  ⚠ 建议迁移前确认目标环境的 Mod 兼容性。");
        Console.WriteLine();
        Migrate(dir);
    }

    static void Migrate(MigrateDirection dir)
    {
        var savesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SlayTheSpire2", "steam");

        if (!Directory.Exists(savesRoot)) { PrintInfo("  未找到存档目录，跳过。"); return; }
        var userDirs = Directory.GetDirectories(savesRoot);
        if (userDirs.Length == 0) { PrintInfo("  未找到用户存档，跳过。"); return; }

        var arrow = dir == MigrateDirection.VanillaToModded ? "原版→Mod" : "Mod→原版";

        foreach (var userDir in userDirs)
        {
            var userId = Path.GetFileName(userDir);
            var (src, dst) = dir == MigrateDirection.VanillaToModded
                ? (Path.Combine(userDir, "profile1", "saves"),
                   Path.Combine(userDir, "modded", "profile1", "saves"))
                : (Path.Combine(userDir, "modded", "profile1", "saves"),
                   Path.Combine(userDir, "profile1", "saves"));

            if (!Directory.Exists(src)) { PrintInfo($"  用户 {userId}: 无源存档，跳过。"); continue; }
            var srcProgress = Path.Combine(src, "progress.save");
            if (!File.Exists(srcProgress)) { PrintInfo($"  用户 {userId}: 无进度文件，跳过。"); continue; }

            // 检查是否已迁移
            var dstProgress = Path.Combine(dst, "progress.save");
            if (File.Exists(dstProgress))
            {
                if (new FileInfo(dstProgress).Length >= new FileInfo(srcProgress).Length * 0.9)
                { PrintInfo($"  用户 {userId}: 已迁移，跳过。"); continue; }
            }

            PrintInfo($"  用户 {userId}: {arrow} 迁移中...");
            Directory.CreateDirectory(dst);
            var dstHistory = Path.Combine(dst, "history");
            Directory.CreateDirectory(dstHistory);

            // 备份
            if (File.Exists(dstProgress))
            {
                var bk = Path.Combine(dst, $"_backup_{DateTime.Now:yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(bk);
                foreach (var f in Directory.GetFiles(dst, "*.save*"))
                    File.Copy(f, Path.Combine(bk, Path.GetFileName(f)), true);
                PrintInfo($"    📋 已备份现有存档");
            }

            // 复制 progress / prefs
            int n = 0;
            foreach (var name in new[] { "progress.save", "progress.save.backup", "prefs.save", "prefs.save.backup" })
            {
                var s = Path.Combine(src, name);
                if (File.Exists(s)) { File.Copy(s, Path.Combine(dst, name), true); n++; }
            }
            PrintSuccess($"    ✅ {n} 个存档文件");

            // 增量复制历史
            var srcHistory = Path.Combine(src, "history");
            if (Directory.Exists(srcHistory))
            {
                var existing = new HashSet<string>(
                    Directory.Exists(dstHistory)
                        ? Directory.GetFiles(dstHistory).Select(Path.GetFileName)!
                        : Array.Empty<string>());
                int r = 0;
                foreach (var f in Directory.GetFiles(srcHistory))
                {
                    var fname = Path.GetFileName(f);
                    if (!existing.Contains(fname)) { File.Copy(f, Path.Combine(dstHistory, fname)); r++; }
                }
                PrintSuccess($"    ✅ {r} 条历史战绩");
            }
        }
        PrintSuccess("  迁移完成！");
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

        foreach (var root in new[] {
            @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam",
            @"D:\Steam", @"E:\Steam", @"F:\Steam",
            @"D:\SteamLibrary", @"E:\SteamLibrary", @"F:\SteamLibrary" })
        {
            var p = Path.Combine(root, "steamapps", "common", GameName);
            if (IsGameDir(p)) return p;
        }
        return null;
    }

    static bool IsGameDir(string path) =>
        Directory.Exists(path) && (
            File.Exists(Path.Combine(path, "data_sts2_windows_x86_64", "sts2.dll")) ||
            File.Exists(Path.Combine(path, "SlayTheSpire2.exe")));

    static string? GetSteamPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam")
                          ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
              if (k?.GetValue("InstallPath") is string p && Directory.Exists(p)) return p; } catch { }
        try { using var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
              if (k?.GetValue("SteamPath") is string p && Directory.Exists(p)) return p; } catch { }
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
            Console.Write(new string(' ', Math.Max(0, Console.WindowWidth - Console.CursorLeft - 1)));
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
        Console.WriteLine($"  ║      v{Version} 安装管理器                     ║");
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
