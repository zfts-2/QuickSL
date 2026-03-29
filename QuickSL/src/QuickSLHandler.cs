using Godot;
using System;

namespace QuickSL;

/// <summary>
/// 常驻节点，监听 SceneTree.NodeAdded：
///   · NMainMenu 出现 → 自动 Continue（单人）或 StartLoad（多人房主）
///   · NTopBarMapButton 出现 → 注入 SL 按钮（非房主不注入）
/// </summary>
public partial class QuickSLHandler : Node
{
    Control? _slButton;

    public override void _Ready() => GetTree().NodeAdded += OnNodeAdded;

    public override void _ExitTree()
    {
        if (IsInsideTree()) GetTree().NodeAdded -= OnNodeAdded;
    }

    void OnNodeAdded(Node node)
    {
        var t = node.GetType();

        // ── 主菜单出现 → 根据模式自动重载 ──
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
            GetTree().CreateTimer(0.2).Timeout += () => InjectButton((Control)node);
    }

    void InjectButton(Control mapBtn)
    {
        if (!GodotObject.IsInstanceValid(mapBtn) || !mapBtn.IsInsideTree()) return;
        if (_slButton != null && GodotObject.IsInstanceValid(_slButton) && _slButton.IsInsideTree()) return;

        var parent = mapBtn.GetParent();
        if (parent == null) return;

        // 多人非房主 → 不注入
        bool isMP = QuickSLMod.IsMultiplayer();
        if (isMP && !QuickSLMod.IsHost())
        {
            GD.Print("[QuickSL] 多人非房主，隐藏 SL 按钮");
            return;
        }

        try
        {
            // 容器 (改回 80x80 大小)
            var container = new Control
            {
                Name = "QuickSLButton",
                CustomMinimumSize = new Vector2(80, 80),
                SizeFlagsHorizontal = mapBtn.SizeFlagsHorizontal,
                SizeFlagsVertical = mapBtn.SizeFlagsVertical
            };

            // 背景 Shader
            var material = new ShaderMaterial { Shader = new Shader { Code = QuickSLStyles.Pseudo3DBackgroundShader } };
            material.SetShaderParameter("hover_weight", 0.0f);

            var bg = new ColorRect { Material = material };
            bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            container.AddChild(bg);

            // 文字
            var labelMat = new ShaderMaterial { Shader = new Shader { Code = QuickSLStyles.Pseudo3DTextShader } };
            labelMat.SetShaderParameter("hover_weight", 0.0f);

            var label = new Label
            {
                Text = "SL",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Material = labelMat
            };
            label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            label.AddThemeFontSizeOverride("font_size", 30);
            label.AddThemeConstantOverride("outline_size", 6);
            label.AddThemeConstantOverride("shadow_offset_x", 0);
            label.AddThemeConstantOverride("shadow_offset_y", 4);
            label.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 1f));
            label.AddThemeColorOverride("font_outline_color", new Color(0.15f, 0.05f, 0.20f, 1f)); // 深邃且富含质感的暗紫暗红描边
            label.AddThemeColorOverride("font_shadow_color", new Color(0.0f, 0.0f, 0.0f, 0.9f));
            container.AddChild(label);

            // 点击区域
            var btn = new Button
            {
                Flat = true, Text = "",
                TooltipText = isMP ? "Quick Save/Load (多人房主 - 将重开房间)" : "Quick Save/Load",
                FocusMode = Control.FocusModeEnum.None
            };
            btn.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            foreach (var s in new[] { "hover", "pressed", "focus" })
                btn.AddThemeStyleboxOverride(s, new StyleBoxEmpty());

            // Hover / Press 动画
            Tween? tween = null;
            float hw = 0f;

            void Animate(float target, float dur)
            {
                if (!GodotObject.IsInstanceValid(btn)) return;
                if (tween is { } t && t.IsValid()) t.Kill();
                tween = btn.CreateTween();
                tween.TweenMethod(Callable.From<float>(v => {
                    hw = v;
                    if (GodotObject.IsInstanceValid(bg) && bg.Material != null)
                        bg.Material.Set("shader_parameter/hover_weight", v);
                    if (GodotObject.IsInstanceValid(label) && label.Material != null)
                        label.Material.Set("shader_parameter/hover_weight", v);
                }), hw, target, dur).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
            }

            btn.MouseEntered += () => Animate(1.0f, 0.2f);
            btn.MouseExited  += () => Animate(0.0f, 0.4f);
            btn.ButtonDown   += () => Animate(1.2f, 0.1f);
            btn.ButtonUp     += () => Animate(btn.IsHovered() ? 1.0f : 0.0f, 0.2f);
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
}
