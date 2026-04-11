using Godot;
using HarmonyLib;
using System;
using System.Reflection;
using MegaCrit.Sts2.Core.Runs;

namespace QuickSL;

/// <summary>
/// QuickSL 核心入口。
/// 直接注入模式：由 Patcher 在 NGame._Ready() 中插入 Initialize() 调用。
/// 不经过游戏 Mod 系统，存档走原版路径，Steam Cloud 完美同步。
/// </summary>
public static class QuickSLMod
{
    public const string ModId = "com.typnosis.quicksl";
    public const string Version = "2.0.0";

    // ── SL 状态 ──
    public static bool AutoContinuePending { get; set; }
    public static bool IsMultiplayerSL { get; set; }

    // ── 死亡回档状态 ──
    /// <summary>备份的存档路径（非空表示死亡回档可用）</summary>
    public static string? SaveBackupPath { get; set; }

    // ── 反射类型缓存 ──
    internal static Type? NGameType;
    internal static Type? NMainMenuType;
    internal static Type? NMapButtonType;
    internal static Type? NMultiplayerSubmenuType;
    internal static Type? INetGameServiceType;
    internal static Type? NetGameTypeEnum;
    internal static Type? RunSaveManagerType;
    internal static Type? NGameOverScreenType;

    // ── 防重入标志 ──
    private static bool _initialized = false;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        GD.Print($"[QuickSL] v{Version} 初始化（直接注入模式）...");
        try
        {
            var harmony = new Harmony(ModId);
            harmony.PatchAll(typeof(QuickSLMod).Assembly);
            ResolveTypes();
            PatchSaveAndManagers(harmony);

            if (Engine.GetMainLoop() is SceneTree tree)
                tree.Root.CallDeferred("add_child", new QuickSLHandler { Name = "QuickSLHandler" });

            GD.Print("[QuickSL] ✅ 初始化完成（直接注入模式，存档走原版路径）");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[QuickSL] ❌ 初始化失败: {ex.Message}");
        }
    }

    static void ResolveTypes()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            NGameType              ??= asm.GetType("MegaCrit.Sts2.Core.Nodes.NGame");
            NMainMenuType          ??= asm.GetType("MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu");
            NMapButtonType         ??= asm.GetType("MegaCrit.Sts2.Core.Nodes.TopBar.NTopBarMapButton");
            NMultiplayerSubmenuType ??= asm.GetType("MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerSubmenu");
            INetGameServiceType    ??= asm.GetType("MegaCrit.Sts2.Core.Multiplayer.Game.INetGameService");
            NetGameTypeEnum        ??= asm.GetType("MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType");
            RunSaveManagerType     ??= asm.GetType("MegaCrit.Sts2.Core.Saves.Managers.RunSaveManager");
            NGameOverScreenType    ??= asm.GetType("MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverScreen");

            if (NGameType != null && NMainMenuType != null && NMapButtonType != null
                && NMultiplayerSubmenuType != null && INetGameServiceType != null
                && NetGameTypeEnum != null && RunSaveManagerType != null
                && NGameOverScreenType != null)
                break;
        }
    }

    // ── Harmony: 在删除前备份存档 ───────────────────────────────

    static void PatchSaveAndManagers(Harmony harmony)
    {
        // Hook 删除存档
        if (RunSaveManagerType != null)
        {
            var prefixDel = typeof(QuickSLMod).GetMethod(nameof(BackupBeforeDelete), BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var name in new[] { "DeleteCurrentRun", "DeleteCurrentMultiplayerRun" })
            {
                var target = RunSaveManagerType.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (target != null && prefixDel != null)
                    harmony.Patch(target, prefix: new HarmonyMethod(prefixDel));
            }
        }
    }

    /// <summary>
    /// Harmony Prefix: 在游戏删除存档之前，备份一份。
    /// 让游戏正常删除（return true），这样正常死亡流程不受影响。
    /// 只有用户点 superSL 时才恢复备份。
    /// </summary>
    public static bool AllowDelete = false;
    public static object? SavedRunSaveManager = null;

    static bool BackupBeforeDelete(object __instance)
    {
        try
        {
            if (AllowDelete) return true; // 允许手动触发的删除

            var rm = RunManager.Instance;
            if (rm == null) return true;

            if (IsMultiplayer()) return true;
            if (IsVictory()) return true;

            var abandoned = rm.GetType().GetProperty("IsAbandoned",
                BindingFlags.Public | BindingFlags.Instance)?.GetValue(rm);
            if (abandoned is true) return true;

            // 存下 RunSaveManager 实例，方便我们后续如果接受死亡再调用
            SavedRunSaveManager = __instance;

            GD.Print("[QuickSL] 🛡️ 拦截 DeleteCurrentRun，保留存档以便由于 superSL 返回！");
            
            // 返回 false 拦截原生删除，这样存档和 profile 都不会被标记为死亡
            return false;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[QuickSL] BackupBeforeDelete 异常: {ex.Message}");
            return true;
        }
    }

    // ── 死亡回档 ────────────────────────────────────────────────

    /// <summary>用户在 Game Over 画面点击 superSL 时调用</summary>
    internal static void TriggerDeathRewind(Node gameOverScreen)
    {
        if (SavedRunSaveManager == null)
        {
            GD.PrintErr("[QuickSL] 未找到绑定的 RunSaveManager，回档可能会失败！");
        }

        AutoContinuePending = true;
        IsMultiplayerSL = false;

        GD.Print("[QuickSL] 💀→🔄 拦截了删除，触发回档");

        if (gameOverScreen is Control ctrl)
            ctrl.Visible = false;

        var nGame = NGameType?
            .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?
            .GetValue(null);
        var method = NGameType?.GetMethod("ReturnToMainMenu",
            BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

        if (nGame != null && method != null)
        {
            method.Invoke(nGame, null);
        }
        else
        {
            GD.PrintErr("[QuickSL] NGame 不可用，回档失败");
            ResetState();
        }
    }

    // ── 多人/通关状态判断 ─────────────────────────────────────────

    public static bool IsVictory(object? gameOverScreen = null)
    {
        try
        {
            // 检查 RunManager 的通关标志
            var rm = RunManager.Instance;
            if (rm != null)
            {
                foreach (var prop in rm.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if ((prop.Name.Contains("Victory") || prop.Name.Contains("Won")) && prop.PropertyType == typeof(bool))
                    {
                        if ((bool)prop.GetValue(rm)! == true) return true;
                    }
                }
                foreach (var field in rm.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if ((field.Name.Contains("Victory") || field.Name.Contains("Won")) && field.FieldType == typeof(bool))
                    {
                        if ((bool)field.GetValue(rm)! == true) return true;
                    }
                }
            }

            // 检查 GameOverScreen 上的通关标志
            if (gameOverScreen != null)
            {
                var type = gameOverScreen.GetType();
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if ((prop.Name.Contains("Victory") || prop.Name.Contains("Won")) && prop.PropertyType == typeof(bool))
                    {
                        if ((bool)prop.GetValue(gameOverScreen)! == true) return true;
                    }
                }
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if ((field.Name.Contains("Victory") || field.Name.Contains("Won")) && field.FieldType == typeof(bool))
                    {
                        if ((bool)field.GetValue(gameOverScreen)! == true) return true;
                    }
                }
            }
        }
        catch { }
        return false;
    }

    public static bool IsMultiplayer()
    {
        try
        {
            var rm = RunManager.Instance;
            if (rm == null) return false;
            var prop = rm.GetType().GetProperty("IsSinglePlayerOrFakeMultiplayer",
                BindingFlags.Public | BindingFlags.Instance);
            return prop != null && !(bool)prop.GetValue(rm)!;
        }
        catch { return false; }
    }

    public static bool IsHost()
    {
        try
        {
            if (INetGameServiceType == null || NetGameTypeEnum == null) return false;
            var rm = RunManager.Instance;
            if (rm == null) return false;

            var netService = rm.GetType()
                .GetProperty("NetService", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(rm);
            if (netService == null) return false;

            var netType = INetGameServiceType.GetProperty("Type")?.GetValue(netService);
            return netType != null && Convert.ToInt32(netType) == 2;
        }
        catch { return false; }
    }

    public static bool CanQuickSL() => !IsMultiplayer() || IsHost();

    // ── 核心 SL 逻辑 ─────────────────────────────────────────

    public static void QuickSaveLoad()
    {
        try
        {
            if (RunManager.Instance is not { IsInProgress: true })
            {
                GD.Print("[QuickSL] 当前无进行中的游戏");
                return;
            }

            if (!CanQuickSL())
            {
                GD.Print("[QuickSL] ⚠ 多人非房主，跳过");
                return;
            }

            IsMultiplayerSL = IsMultiplayer();
            AutoContinuePending = true;

            GD.Print($"[QuickSL] 模式: {(IsMultiplayerSL ? "多人(房主)" : "单人")}");

            var nGame = NGameType?
                .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?
                .GetValue(null);
            var method = NGameType?.GetMethod("ReturnToMainMenu",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

            if (nGame != null && method != null)
            {
                GD.Print("[QuickSL] ReturnToMainMenu...");
                method.Invoke(nGame, null);
            }
            else
            {
                GD.PrintErr("[QuickSL] NGame 不可用");
                ResetState();
            }
        }
        catch (Exception ex)
        {
            ResetState();
            GD.PrintErr($"[QuickSL] QuickSaveLoad 失败: {ex.Message}");
        }
    }

    // ── 自动继续 ─────────────────────────────────────────────

    internal static void AutoContinue(Node mainMenu)
    {
        if (NMainMenuType == null) return;
        try
        {
            NMainMenuType
                .GetMethod("OnContinueButtonPressed", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(mainMenu, new object?[] { null });
            GD.Print("[QuickSL] ✅ 单人 Continue");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[QuickSL] Continue 异常: {Unwrap(ex)}");
        }
    }

    internal static void AutoContinueMultiplayer(Node mainMenu)
    {
        if (NMainMenuType == null || NMultiplayerSubmenuType == null) return;
        try
        {
            var openMethod = NMainMenuType.GetMethod("OpenMultiplayerSubmenu",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            var submenu = openMethod?.Invoke(mainMenu, null);
            if (submenu == null)
            {
                GD.PrintErr("[QuickSL] OpenMultiplayerSubmenu 失败");
                return;
            }

            GD.Print("[QuickSL] 多人子菜单已打开，延迟触发 StartLoad...");

            if (Engine.GetMainLoop() is not SceneTree tree) return;
            tree.CreateTimer(0.3).Timeout += () =>
            {
                try
                {
                    NMultiplayerSubmenuType
                        .GetMethod("StartLoad", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                        ?.Invoke(submenu, new object?[] { null });
                    GD.Print("[QuickSL] ✅ 多人 StartLoad（进入 Host 等待房间）");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[QuickSL] StartLoad 异常: {Unwrap(ex)}");
                }
            };
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[QuickSL] 多人重载异常: {Unwrap(ex)}");
        }
    }

    // ── 工具方法 ──────────────────────────────────────────────

    internal static void ResetState()
    {
        AutoContinuePending = false;
        IsMultiplayerSL = false;
        SavedRunSaveManager = null;
        AllowDelete = false;
    }

    internal static string Unwrap(Exception ex)
        => ex is TargetInvocationException tie
            ? tie.InnerException?.Message ?? ex.Message
            : ex.Message;
}
