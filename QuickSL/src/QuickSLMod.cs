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
    public const string ModId = "com.darkgemini.quicksl";
    public const string Version = "1.1.0";

    public static bool AutoContinuePending { get; set; }
    public static bool IsMultiplayerSL { get; set; }

    internal static Type? NGameType;
    internal static Type? NMainMenuType;
    internal static Type? NMapButtonType;
    internal static Type? NMultiplayerSubmenuType;
    internal static Type? INetGameServiceType;
    internal static Type? NetGameTypeEnum;

    public static void Initialize()
    {
        GD.Print($"[QuickSL] v{Version} 初始化...");
        try
        {
            new Harmony(ModId).PatchAll(typeof(QuickSLMod).Assembly);
            ResolveTypes();

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

            if (NGameType != null && NMainMenuType != null && NMapButtonType != null
                && NMultiplayerSubmenuType != null && INetGameServiceType != null
                && NetGameTypeEnum != null)
                break;
        }
    }

    // ── 多人模式判断 ─────────────────────────────────────────

    /// <summary>RunManager.IsSinglePlayerOrFakeMultiplayer == false</summary>
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

    /// <summary>RunManager.NetService.Type == NetGameType.Host (2)</summary>
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

    /// <summary>单人 → true, 多人 → 仅房主 true</summary>
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

    // ── 自动继续（信号触发后调用）──────────────────────────────

    /// <summary>单人：OnContinueButtonPressed(null)</summary>
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

    /// <summary>多人：OpenMultiplayerSubmenu() → StartLoad(null) → Host 等待房间</summary>
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

    static void ResetState()
    {
        AutoContinuePending = false;
        IsMultiplayerSL = false;
    }

    static string Unwrap(Exception ex)
        => ex is TargetInvocationException tie
            ? tie.InnerException?.Message ?? ex.Message
            : ex.Message;
}
