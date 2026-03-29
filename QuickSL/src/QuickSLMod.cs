using Godot;
using HarmonyLib;
using System;
using System.Reflection;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;

namespace QuickSL;

[ModInitializer("Initialize")]
public static class QuickSLMod
{
    public const string ModId = "com.typnosis.quicksl";
    public const string Version = "1.3.0";

    // ── SL 状态 ──
    public static bool AutoContinuePending { get; set; }
    public static bool IsMultiplayerSL { get; set; }

    // ── 死亡回档状态 ──
    /// <summary>存档是否被我们拦截保留（需要清理或回档）</summary>
    public static bool SavePreserved { get; set; }

    // ── 反射类型缓存 ──
    internal static Type? NGameType;
    internal static Type? NMainMenuType;
    internal static Type? NMapButtonType;
    internal static Type? NMultiplayerSubmenuType;
    internal static Type? INetGameServiceType;
    internal static Type? NetGameTypeEnum;
    internal static Type? RunSaveManagerType;
    internal static Type? NGameOverScreenType;

    public static void Initialize()
    {
        GD.Print($"[QuickSL] v{Version} 初始化...");
        try
        {
            var harmony = new Harmony(ModId);
            harmony.PatchAll(typeof(QuickSLMod).Assembly);
            ResolveTypes();
            PatchDeleteCurrentRun(harmony);

            if (Engine.GetMainLoop() is SceneTree tree)
                tree.Root.CallDeferred("add_child", new QuickSLHandler { Name = "QuickSLHandler" });

            GD.Print("[QuickSL] ✅ 初始化完成");
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

    // ── Harmony: 拦截 DeleteCurrentRun ──────────────────────────

    static void PatchDeleteCurrentRun(Harmony harmony)
    {
        if (RunSaveManagerType == null)
        {
            GD.PrintErr("[QuickSL] RunSaveManagerType 未找到，死亡回档不可用");
            return;
        }

        var prefix = typeof(QuickSLMod).GetMethod(nameof(DeleteCurrentRunPrefix),
            BindingFlags.NonPublic | BindingFlags.Static);

        foreach (var name in new[] { "DeleteCurrentRun", "DeleteCurrentMultiplayerRun" })
        {
            var target = RunSaveManagerType.GetMethod(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (target != null)
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                GD.Print($"[QuickSL] ✅ Hooked {name}");
            }
        }
    }

    /// <summary>
    /// Harmony Prefix: 单人死亡时阻止存档删除，让用户在 Game Over 画面选择回档。
    /// 多人模式 / 主动放弃 / 非 GameOver → 正常删除。
    /// </summary>
    static bool DeleteCurrentRunPrefix()
    {
        try
        {
            var rm = RunManager.Instance;
            if (rm == null) return true;

            // 多人 → 不拦截
            if (IsMultiplayer()) return true;

            // 主动放弃 → 不拦截
            var abandoned = rm.GetType().GetProperty("IsAbandoned",
                BindingFlags.Public | BindingFlags.Instance)?.GetValue(rm);
            if (abandoned is true) return true;

            // 非 GameOver → 不拦截
            var gameOver = rm.GetType().GetProperty("IsGameOver",
                BindingFlags.Public | BindingFlags.Instance)?.GetValue(rm);
            if (gameOver is not true) return true;

            // ── 单人死亡：保留存档 ──
            SavePreserved = true;
            GD.Print("[QuickSL] 🛡️ 拦截存档删除（死亡回档可用）");
            return false;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[QuickSL] DeleteCurrentRunPrefix 异常: {ex.Message}");
            return true;
        }
    }

    // ── 死亡回档 ────────────────────────────────────────────────

    /// <summary>用户在 Game Over 画面点击"回档"时调用</summary>
    internal static void TriggerDeathRewind(Node gameOverScreen)
    {
        SavePreserved = false;
        AutoContinuePending = true;
        IsMultiplayerSL = false; // 死亡回档仅单人

        GD.Print("[QuickSL] 💀→🔄 死亡回档触发");

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

    /// <summary>
    /// 清理被保留的存档（用户选择了"继续"而非"回档"时）。
    /// 手动调用原本被跳过的 DeleteCurrentRun。
    /// </summary>
    internal static void CleanupPreservedSave()
    {
        if (!SavePreserved) return;
        SavePreserved = false;

        try
        {
            var rm = RunManager.Instance;
            if (rm == null || RunSaveManagerType == null) return;

            var saveMgrProp = rm.GetType().GetProperty("RunSaveManager",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var saveMgr = saveMgrProp?.GetValue(rm);
            if (saveMgr == null) return;

            // 直接删文件（绕过我们自己的 hook），用反射拿路径然后 File.Delete
            var pathProp = RunSaveManagerType.GetProperty("CurrentRunSavePath",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var path = pathProp?.GetValue(saveMgr) as string;
            if (path != null && System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
                GD.Print($"[QuickSL] 🗑️ 已清理保留的存档: {System.IO.Path.GetFileName(path)}");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[QuickSL] 存档清理异常: {ex.Message}");
        }
    }

    // ── 多人模式判断 ─────────────────────────────────────────

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
        SavePreserved = false;
    }

    internal static string Unwrap(Exception ex)
        => ex is TargetInvocationException tie
            ? tie.InnerException?.Message ?? ex.Message
            : ex.Message;
}
