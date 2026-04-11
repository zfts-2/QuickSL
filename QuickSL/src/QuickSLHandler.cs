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

    public override void _Ready() {
        GetTree().NodeAdded += OnNodeAdded;
    }


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

            var delay = mp ? 0.3 : 0.5; // 死亡回档后需要更长延迟等文件就位
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

        // ── Game Over 画面出现 ──
        if (QuickSLMod.NGameOverScreenType?.IsAssignableFrom(t) == true)
        {
            // 隐藏 SL 按钮
            if (_slButton != null && GodotObject.IsInstanceValid(_slButton))
            {
                _slButton.Visible = false;
                GD.Print("[QuickSL] SL 按钮已隐藏");
            }

            // 有拦截到删除操作时提前注入并潜伏 superSL
            if (QuickSLMod.SavedRunSaveManager != null)
            {
                GD.Print("[QuickSL] Game Over 画面出现，潜伏侦测 continue 按钮的诞生...");
                var screen = node;
                var attempts = 0;

                void TryPrepare()
                {
                    attempts++;
                    if (!GodotObject.IsInstanceValid(screen)) return;

                    var field = screen.GetType().GetField("_continueButton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var btn = field?.GetValue(screen) as Control;
                    
                    // 只要它在内存里被 new 出来了，我们就立刻给它做手术，绝对不等它 Visible，否则就错过了起跳时机！
                    if (btn != null)
                    {
                        GD.Print($"[QuickSL] continue 按钮已实例化，立刻执行坐标重写注入手术 (第{attempts}次)");
                        PrepareRewindButton(screen);
                        return;
                    }

                    if (attempts < 20)
                        GetTree()?.CreateTimer(0.1)?.Connect("timeout", Godot.Callable.From(TryPrepare));
                    else
                        GD.PrintErr("[QuickSL] ⚠ continue 按钮始终未生成，放弃注入");
                }
                
                // 延迟一帧开始
                Godot.Callable.From(TryPrepare).CallDeferred();
            }
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
                TooltipText = isMP 
                    ? "快速存读档\n保存当前进度，并立即重新载入当前房间。\n注意：联机模式下将强行重载整个多人房间！\n(F5)" 
                    : "快速存读档\n保存当前的进度，并立即重新载入当前房间。\n(F5)",
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

    // 放弃有延迟且会导致重复加载的轮询，改为直接对原始组件进行提前设置，然后在原生显示时准同步克隆体的动画
    void PrepareRewindButton(Node gameOverScreen)
    {
        if (!GodotObject.IsInstanceValid(gameOverScreen)) return;
        if (_rewindButton != null && GodotObject.IsInstanceValid(_rewindButton)) return;
        
        // 游戏通关时不显示死亡回档按钮
        if (QuickSLMod.IsVictory(gameOverScreen))
        {
            GD.Print("[QuickSL] 当前为通关状态，不注入 superSL 按钮");
            return;
        }

        try
        {
            var continueField = gameOverScreen.GetType().GetField("_continueButton",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var continueBtn = continueField?.GetValue(gameOverScreen) as Control;

            var mainMenuField = gameOverScreen.GetType().GetField("_mainMenuButton",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var mainMenuBtn = mainMenuField?.GetValue(gameOverScreen) as Control;

            if (continueBtn == null || !GodotObject.IsInstanceValid(continueBtn))
            {
                GD.PrintErr("[QuickSL] 未找到 _continueButton");
                return;
            }

            var parent = continueBtn.GetParent();
            if (parent == null) return;

            // 监听原生的 Continue 和 MainMenu 按钮，玩家接受死亡时我们手动删除存档
            Action interceptDelete = () =>
            {
                if (QuickSLMod.AllowDelete) return; // 已经执行过了
                GD.Print("[QuickSL] 玩家放弃治疗，正常执行 DeleteCurrentRun");
                QuickSLMod.AllowDelete = true;
                if (QuickSLMod.SavedRunSaveManager != null)
                {
                    var deleteMethod = QuickSLMod.SavedRunSaveManager.GetType().GetMethod("DeleteCurrentRun",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    deleteMethod?.Invoke(QuickSLMod.SavedRunSaveManager, null);
                }
            };

            // 非破坏性监听原生按钮：改为严格模仿 Godot Button 的 Click 触发机制（鼠标抬起 + 位于框内）
            void HookNativeButton(Control nativeBtn)
            {
                if (nativeBtn == null) return;
                var callable = Godot.Callable.From<InputEvent>(ev => 
                {
                    if (ev is InputEventMouseButton mb && !mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                    {
                        if (nativeBtn.GetGlobalRect().HasPoint(mb.GlobalPosition))
                        {
                            interceptDelete();
                            // 玩家点原生按钮继续了，给 superSL 一个优雅的消失退场动画！
                            if (_rewindButton != null && GodotObject.IsInstanceValid(_rewindButton))
                            {
                                var t = _rewindButton.GetTree().CreateTween();
                                t.TweenProperty(_rewindButton, "modulate:a", 0f, 0.2f);
                            }
                        }
                    }
                });
                
                if (!nativeBtn.IsConnected("gui_input", callable))
                {
                    nativeBtn.Connect("gui_input", callable);
                }
            }

            HookNativeButton(continueBtn);
            if (mainMenuBtn != null) HookNativeButton(mainMenuBtn);

            // 阶段 1：在 continueBtn 刚刚在内存创建但尚未进行 _Ready 初始化和子节点构建前，
            // 抢先修改其目标弹出坐标！
            var cPos = continueBtn.Position;
            var cSize = continueBtn.Size;
            var centerX = cPos.X + cSize.X / 2;
            var gap = 15f;
            var newCloneX = centerX - gap - cSize.X;
            var newContX = centerX + gap;

            ShiftInternalX(continueBtn, cPos.X, newContX);
            continueBtn.Position = new Vector2(newContX, cPos.Y);

            // 阶段 2：开辟一条独立时间轴，死蹲原版 continueBtn 动画启动。
            // 当动画一发生，意味着它的 _Ready 已经彻底跑完，其名下的高亮框、背景图等各种子节点必定全部生成完毕。
            // 此时再使用 Godot Duplicate() 进行克隆，才能万分保证克隆体“五脏俱全”（不再是一片空白）！
            var initialY = cPos.Y;
            var startAlpha = continueBtn.Modulate.A;
            bool isCloned = false;
            int awaitTicks = 0;

            void AwaitAndClone()
            {
                if (isCloned || !GodotObject.IsInstanceValid(continueBtn) || !GodotObject.IsInstanceValid(parent)) return;

                awaitTicks++;

                // 判断标准：被设了透明度，开始位移，或者是等待强制超时（50帧约0.8秒后强行克隆，防止死锁不展现）
                if (Math.Abs(continueBtn.Position.Y - initialY) > 0.5f || Math.Abs(continueBtn.Modulate.A - startAlpha) > 0.05f || awaitTicks > 50)
                {
                    isCloned = true;
                    GD.Print($"[QuickSL] 原版 continue 起跳或已超时 ({awaitTicks})！子节点发育成熟，开始收网克隆。");

                    var clone = continueBtn.Duplicate() as Control;
                    if (clone == null) return;
                    
                    clone.Name = "superSL";

                    // 深度切断材质共享
                    void MakeMaterialsUnique(Node node)
                    {
                        if (node is CanvasItem ci && ci.Material != null) ci.Material = ci.Material.Duplicate() as Material;
                        foreach (var child in node.GetChildren()) MakeMaterialsUnique(child);
                    }
                    MakeMaterialsUnique(clone);

                    // 【核心修复】Godot Duplicate 浅拷贝会导致 C# 引用错乱，必须递归重新帮 clone 指定其自身的子节点！
                    void FixDuplicateFields(Node original, Node cloned)
                    {
                        foreach (var f in original.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        {
                            var oldVal = f.GetValue(original);
                            if (oldVal is Node oldNode && original.IsAncestorOf(oldNode))
                            {
                                var path = original.GetPathTo(oldNode);
                                if (cloned.HasNode(path)) f.SetValue(cloned, cloned.GetNode(path));
                            }
                            else if (oldVal is Material oldMat && cloned is CanvasItem ci && ci.Material != null)
                            {
                                if (ci.Material.ResourceName == oldMat.ResourceName || oldMat == ci.Material) f.SetValue(cloned, ci.Material);
                            }
                        }
                        for (int i = 0; i < original.GetChildCount(); i++)
                        {
                            if (i < cloned.GetChildCount()) FixDuplicateFields(original.GetChild(i), cloned.GetChild(i));
                        }
                    }
                    FixDuplicateFields(continueBtn, clone);

                    // 换皮
                    ForceSetText(clone);

                    // 挂载并立即可见
                    clone.Visible = true;
                    parent.AddChild(clone);
                    
                    // 挂靠本模组的重载功能 (模拟原生 Click 逻辑，鼠标抬起且在范围内)
                    var cloneCallable = Godot.Callable.From<InputEvent>(ev => 
                    {
                        if (ev is InputEventMouseButton mb && !mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                        {
                            if (clone.GetGlobalRect().HasPoint(mb.GlobalPosition))
                            {
                                GD.Print("[QuickSL] 成功精准触发 Death Rewind 按钮释放逻辑！");
                                if (GodotObject.IsInstanceValid(clone))
                                    clone.GetTree().CreateTween().TweenProperty(clone, "modulate:a", 0f, 0.2f);
                                if (GodotObject.IsInstanceValid(continueBtn))
                                    continueBtn.GetTree().CreateTween().TweenProperty(continueBtn, "modulate:a", 0f, 0.2f);
                                QuickSLMod.TriggerDeathRewind(gameOverScreen);
                            }
                        }
                    });
                    if (!clone.IsConnected("gui_input", cloneCallable)) clone.Connect("gui_input", cloneCallable);

                    _rewindButton = clone;

                    // 废弃对 clone.OnEnable 的风险调用，改用最高维度的帧锁死！
                    // 把 clone 的灵魂彻底与 continue 绑定，每一帧强制复制原生按钮的三维、坐标与显隐
                    void ForceSlaveLoop()
                    {
                        if (!GodotObject.IsInstanceValid(clone) || !GodotObject.IsInstanceValid(continueBtn)) return;
                        
                        // 强制锁定，彻底跟随原生的进场、退场和隐现
                        clone.Position = new Vector2(newCloneX, continueBtn.Position.Y);
                        clone.Size = continueBtn.Size;
                        clone.Modulate = continueBtn.Modulate;
                        clone.SelfModulate = continueBtn.SelfModulate;
                        clone.Visible = continueBtn.Visible;
                        
                        clone.GetTree()?.CreateTimer(0.016)?.Connect("timeout", Godot.Callable.From(ForceSlaveLoop));
                    }
                    ForceSlaveLoop();

                    GD.Print($"[QuickSL] ✅ superSL 克隆部署完成！彻底锁死奴役坐标。");
                    return;
                }

                continueBtn.GetTree()?.CreateTimer(0.016)?.Connect("timeout", Godot.Callable.From(AwaitAndClone));
            }
            AwaitAndClone();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[QuickSL] 回档按钮注入失败: {ex.Message}");
        }
    }

    // 弃用 _Process，直接在初始化时硬改原生组件内部缓存的 _showPosition / _hidePosition 坐标
    // 这样游戏原生的 Tween 就会自动且无比平滑地把它滑到正确的位置，彻底解决卡顿问题！
    static void ShiftInternalX(object obj, float oldOldX, float newX)
    {
        var type = obj.GetType();
        foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
        {
            if (field.FieldType == typeof(Vector2))
            {
                var val = (Vector2)field.GetValue(obj)!;
                if (Mathf.Abs(val.X - oldOldX) < 2f) 
                {
                    val.X = newX;
                    field.SetValue(obj, val);
                }
            }
        }
    }

    /// <summary>强力破解替换所有文本（清空本地化并且延迟设定）</summary>
    static void ForceSetText(Node root)
    {
        void WipeKeyAndSetText(Node node)
        {
            // [修复] 原生的 Godot Button 是我们在透明点击层或者劫持层注入的，里面如果有内容会变成全白字体！
            if (node is Button || node is TextureButton) return; 

            var type = node.GetType();
            
            // 清空本地化 Key（MegaRichTextLabel 等常见属性名）防止语言包抢占
            foreach (var keyName in new[] { "localization_key", "LocKey", "localizationKey", "key", "_textKey" })
            {
                var field = type.GetField(keyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field?.FieldType == typeof(string)) try { field.SetValue(node, ""); } catch { }
                
                var propK = type.GetProperty(keyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (propK?.CanWrite == true && propK.PropertyType == typeof(string)) try { propK.SetValue(node, ""); } catch { }
            }

            // 暴力覆盖文本
            var prop = type.GetProperty("Text", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (prop?.CanWrite == true && prop.PropertyType == typeof(string))
            {
                try { prop.SetValue(node, "superSL"); } catch { }
            }

            foreach (var child in node.GetChildren())
                WipeKeyAndSetText(child);
        }

        // 立即执行一次
        WipeKeyAndSetText(root);

        // 为了防止游戏逻辑在数帧后再次覆写，在接下来的一小段时间内偶尔补刀
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            tree.CreateTimer(0.1).Timeout += () => { if (GodotObject.IsInstanceValid(root)) WipeKeyAndSetText(root); };
            tree.CreateTimer(0.3).Timeout += () => { if (GodotObject.IsInstanceValid(root)) WipeKeyAndSetText(root); };
            tree.CreateTimer(0.8).Timeout += () => { if (GodotObject.IsInstanceValid(root)) WipeKeyAndSetText(root); };
            tree.CreateTimer(1.5).Timeout += () => { if (GodotObject.IsInstanceValid(root)) WipeKeyAndSetText(root); };
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            if (keyEvent.Keycode == Key.F5)
            {
                // 仅当常规 SL 按钮在场且可见时（战斗或事件进行中），允许按 F5 触发快速读档
                if (GodotObject.IsInstanceValid(_slButton) && _slButton.IsInsideTree() && _slButton.Visible)
                {
                    GD.Print("[QuickSL] 热键 F5 触发！");
                    QuickSLMod.QuickSaveLoad();
                    GetViewport()?.SetInputAsHandled();
                }
            }
        }
    }
}

