using Godot;
using System;

namespace QuickSL;

/// <summary>
/// 常驻节点，监听 SceneTree.NodeAdded：
///   · NMainMenu → 自动 Continue / StartLoad
///   · NTopBarMapButton → 注入 SL 按钮
///   · NGameOverScreen → 注入"回档"按钮（单人死亡时）
/// </summary>
public partial class QuickSLHandler : Node
{
    Control? _slButton;
    Control? _rewindButton;

    public override void _Ready() => GetTree().NodeAdded += OnNodeAdded;

    public override void _ExitTree()
    {
        if (IsInsideTree()) GetTree().NodeAdded -= OnNodeAdded;
    }

    void OnNodeAdded(Node node)
    {
        var t = node.GetType();

        // ── 主菜单出现 → 自动重载 ──
        if (QuickSLMod.AutoContinuePending && QuickSLMod.NMainMenuType?.IsAssignableFrom(t) == true)
        {
            QuickSLMod.AutoContinuePending = false;
            var mp = QuickSLMod.IsMultiplayerSL;
            QuickSLMod.IsMultiplayerSL = false;

            var delay = mp ? 0.3 : 0.1;
            GD.Print($"[QuickSL] 主菜单出现，{delay}s 后{(mp ? "多人重载" : "Continue")}...");

            GetTree().CreateTimer(delay).Timeout += () =>
            {
                if (!GodotObject.IsInstanceValid(node)) return;
                if (mp) QuickSLMod.AutoContinueMultiplayer(node);
                else    QuickSLMod.AutoContinue(node);
            };
        }

        // ── Map 按钮出现 → 注入 SL 按钮 ──
        if (QuickSLMod.NMapButtonType?.IsAssignableFrom(t) == true)
            GetTree().CreateTimer(0.2).Timeout += () => InjectSLButton((Control)node);

        // ── Game Over 画面出现 → 注入"回档"按钮（仅单人、存档已保留时） ──
        if (QuickSLMod.SavePreserved
            && QuickSLMod.NGameOverScreenType?.IsAssignableFrom(t) == true)
        {
            GD.Print("[QuickSL] Game Over 画面出现，注入回档按钮...");
            var screen = node;

            // 等待 Game Over 动画播放一段时间后再显示回档按钮
            GetTree().CreateTimer(1.5).Timeout += () =>
            {
                if (GodotObject.IsInstanceValid(screen))
                    InjectRewindButton(screen);
            };

            // 当 Game Over 画面退出时（用户选了"继续"），清理被保留的存档
            screen.TreeExiting += () =>
            {
                if (QuickSLMod.SavePreserved)
                {
                    GD.Print("[QuickSL] Game Over 正常退出，清理保留存档...");
                    QuickSLMod.CleanupPreservedSave();
                }
            };
        }
    }

    // ── SL 按钮注入 ──────────────────────────────────────────

    void InjectSLButton(Control mapBtn)
    {
        if (!GodotObject.IsInstanceValid(mapBtn) || !mapBtn.IsInsideTree()) return;
        if (_slButton != null && GodotObject.IsInstanceValid(_slButton) && _slButton.IsInsideTree()) return;

        var parent = mapBtn.GetParent();
        if (parent == null) return;

        bool isMP = QuickSLMod.IsMultiplayer();
        if (isMP && !QuickSLMod.IsHost())
        {
            GD.Print("[QuickSL] 多人非房主，隐藏 SL 按钮");
            return;
        }

        try
        {
            var container = new Control
            {
                Name = "QuickSLButton",
                CustomMinimumSize = mapBtn.CustomMinimumSize
            };

            var material = new ShaderMaterial { Shader = new Shader { Code = QuickSLStyles.Pseudo3DBackgroundShader } };
            material.SetShaderParameter("hover_weight", 0.0f);

            var bg = new ColorRect { Material = material };
            bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            container.AddChild(bg);

            var label = new Label
            {
                Text = "SL",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            label.AddThemeFontSizeOverride("font_size", 30);
            label.AddThemeConstantOverride("outline_size", 8);
            label.AddThemeConstantOverride("shadow_offset_x", 0);
            label.AddThemeConstantOverride("shadow_offset_y", 2);
            label.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 1f));
            label.AddThemeColorOverride("font_outline_color", new Color(0.25f, 0.05f, 0.4f, 0.9f));
            label.AddThemeColorOverride("font_shadow_color", new Color(0.1f, 0.0f, 0.2f, 0.8f));
            var labelMat = new ShaderMaterial { Shader = new Shader { Code = QuickSLStyles.Pseudo3DTextShader } };
            labelMat.SetShaderParameter("hover_weight", 0.0f);
            label.Material = labelMat;
            container.AddChild(label);

            var btn = new Button
            {
                Flat = true, Text = "",
                TooltipText = isMP ? "Quick Save/Load (多人房主 - 将重开房间)" : "Quick Save/Load",
                FocusMode = Control.FocusModeEnum.None
            };
            btn.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            foreach (var s in new[] { "hover", "pressed", "focus" })
                btn.AddThemeStyleboxOverride(s, new StyleBoxEmpty());

            Tween? tween = null;
            float hw = 0f;

            void Animate(float target, float dur)
            {
                if (!GodotObject.IsInstanceValid(btn)) return;
                if (tween is { } tw && tw.IsValid()) tw.Kill();
                tween = btn.CreateTween();
                tween.TweenMethod(Callable.From<float>(v => {
                    hw = v;
                    if (GodotObject.IsInstanceValid(bg) && bg.Material != null)
                        bg.Material.Set("shader_parameter/hover_weight", v);
                    if (GodotObject.IsInstanceValid(label) && label.Material != null)
                        label.Material.Set("shader_parameter/hover_weight", v);
                }), hw, target, dur).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
            }

            btn.MouseEntered += () => { label.AddThemeFontSizeOverride("font_size", 34); Animate(1.0f, 0.3f); };
            btn.MouseExited  += () => { label.AddThemeFontSizeOverride("font_size", 30); Animate(0.0f, 0.5f); };
            btn.ButtonDown   += () => { label.AddThemeFontSizeOverride("font_size", 32); Animate(1.2f, 0.15f); };
            btn.ButtonUp     += () => {
                bool h = btn.IsHovered();
                label.AddThemeFontSizeOverride("font_size", h ? 34 : 30);
                Animate(h ? 1.0f : 0.0f, 0.3f);
            };
            btn.Pressed += QuickSLMod.QuickSaveLoad;

            container.AddChild(btn);
            parent.AddChild(container);
            parent.MoveChild(container, mapBtn.GetIndex());

            _slButton = container;
            GD.Print($"[QuickSL] ✅ SL 按钮已注入 (多人={isMP})");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[QuickSL] 按钮注入失败: {ex.Message}");
        }
    }

    void InjectRewindButton(Node gameOverScreen)
    {
        if (!GodotObject.IsInstanceValid(gameOverScreen)) return;
        if (_rewindButton != null && GodotObject.IsInstanceValid(_rewindButton)) return;

        try
        {
            // 通过反射找到游戏的 _continueButton
            var continueField = gameOverScreen.GetType().GetField("_continueButton",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var continueBtn = continueField?.GetValue(gameOverScreen) as Control;

            if (continueBtn == null || !GodotObject.IsInstanceValid(continueBtn))
            {
                GD.PrintErr("[QuickSL] 未找到 _continueButton");
                return;
            }

            var parent = continueBtn.GetParent();
            if (parent == null) return;

            // 复制 continue 按钮作为外观模板
            var rewindNode = continueBtn.Duplicate() as Control;
            if (rewindNode == null) return;
            rewindNode.Name = "QuickSLRewindButton";

            // 插入到 continue 按钮前面
            parent.AddChild(rewindNode);
            parent.MoveChild(rewindNode, continueBtn.GetIndex());

            // 修改所有文字为 "superSL"
            SetAllText(rewindNode, "superSL");

            // 在复制的按钮上叠加一个透明按钮来捕获点击
            // Duplicate 的游戏按钮内部 OnPress 不会触发（信号未连接），
            // 所以我们用一个覆盖全区域的 Button 来拦截输入
            var overlay = new Button
            {
                Flat = true,
                Text = "",
                FocusMode = Control.FocusModeEnum.None,
                MouseFilter = Control.MouseFilterEnum.Stop, // 拦截鼠标事件
            };
            overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            foreach (var s in new[] { "normal", "hover", "pressed", "focus" })
                overlay.AddThemeStyleboxOverride(s, new StyleBoxEmpty());

            var screen = gameOverScreen;
            overlay.Pressed += () =>
            {
                GD.Print("[QuickSL] 用户选择 superSL");
                QuickSLMod.TriggerDeathRewind(screen);
            };

            rewindNode.AddChild(overlay);

            _rewindButton = rewindNode;
            GD.Print("[QuickSL] ✅ superSL 按钮已注入");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[QuickSL] 回档按钮注入失败: {ex.Message}");
        }
    }

    /// <summary>递归设置节点树中所有文本属性为指定内容</summary>
    static void SetAllText(Node node, string text)
    {
        // 尝试通过反射设置 Text 属性（覆盖 Label, Button, MegaLabel 等）
        var prop = node.GetType().GetProperty("Text",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (prop?.CanWrite == true && prop.PropertyType == typeof(string))
        {
            try { prop.SetValue(node, text); } catch { }
        }

        foreach (var child in node.GetChildren())
            SetAllText(child, text);
    }
}

