namespace QuickSL;

/// <summary>SL 按钮的卡通化 + 魔法光环 Shader</summary>
public static class QuickSLStyles
{
    public const string Pseudo3DBackgroundShader = @"
shader_type canvas_item;
uniform float hover_weight = 0.0;

// 伪随机数，用于产生一点微弱的材质噪点
float noise(vec2 uv) {
    return fract(sin(dot(uv, vec2(12.9898, 78.233))) * 43758.5453);
}

void fragment() {
    // 居中映射，适应 80x80 控制框
    // 稍微留出一丝供厚度和阴影使用的边缘
    vec2 uv = (UV - 0.5) * 2.0;
    float r = length(uv);
    
    // 按钮的真实视觉半径
    float btn_radius = 0.86;
    
    // 1. 基础颜色：紫红到紫蓝的斜向暗沉渐变
    // uv.x + uv.y 在左上角是负，右下角是正；利用这个特性做线性渐变
    float grad_factor = clamp((uv.x + uv.y + btn_radius) / (2.0 * btn_radius), 0.0, 1.0);
    vec3 col_rd = vec3(0.24, 0.12, 0.28); // 偏暗的紫红
    vec3 col_bl = vec3(0.12, 0.14, 0.30); // 偏暗的紫蓝
    vec3 base_color = mix(col_rd, col_bl, grad_factor);
    
    // 添加极少量的噪点增加“石质/磨砂”质感，避免纯色过于塑料
    float n = noise(uv * 10.0);
    base_color += (n - 0.5) * 0.03;
    
    // 2. 伪3D立体感 (Bevel 倒角与打光)
    float inner_r = btn_radius - 0.16;
    float bevel = smoothstep(inner_r, btn_radius, r);
    
    // 模拟光照：假设主光源在左上方
    float angle = atan(uv.y, uv.x);
    float light_angle = -2.35; // 约 -135度，即左上方
    float directional_light = cos(angle - light_angle);
    
    // 迎光面亮，背光面暗
    float edge_light = directional_light * bevel;
    vec3 color = base_color;
    
    // 左上提取假高光
    color += vec3(0.4, 0.35, 0.45) * max(edge_light, 0.0) * 1.5;
    // 右下增加假阴影
    color -= vec3(0.8) * max(-edge_light, 0.0) * 0.8;
    
    // 在倒角的内侧增加一条微弱的高光线（提升模型的锐利金属/石刻边缘质感）
    float inner_rim = smoothstep(inner_r - 0.04, inner_r, r) * smoothstep(btn_radius - 0.05, inner_r + 0.05, r);
    color += vec3(0.25, 0.2, 0.35) * inner_rim;
    
    // 中央凸起表面的微弱高光反射
    vec2 highlight_uv = uv - vec2(-0.2, -0.2);
    float specular = smoothstep(0.5, 0.0, length(highlight_uv));
    color += vec3(0.15, 0.12, 0.2) * pow(specular, 2.0);
    
    // 3. 悬停交互 (魔法亮光透出)
    float center_glow = 1.0 - smoothstep(0.0, btn_radius, r);
    float pulse = sin(TIME * 4.0) * 0.5 + 0.5;
    // 使用偏青/天蓝的色调来呼应“紫红-紫蓝”的冷色基底
    vec3 hover_glow_color = vec3(0.3, 0.7, 0.8);
    float hover_glow_intensity = center_glow * (0.3 + pulse * 0.4) * hover_weight;
    
    color += hover_glow_color * hover_glow_intensity;
    color += vec3(0.1, 0.12, 0.15) * hover_weight; // 整体微亮
    color += vec3(0.3, 0.25, 0.4) * max(edge_light, 0.0) * hover_weight; // 强化高光
    
    // 4. 伪3D底部投影 (Drop Shadow)
    vec2 shadow_uv = uv - vec2(0.06, 0.08); // 偏移向右下方
    float shadow_mask = 1.0 - smoothstep(btn_radius - 0.06, btn_radius + 0.06, length(shadow_uv));
    
    // 5. 混合输出
    float alpha_btn = 1.0 - smoothstep(btn_radius - 0.02, btn_radius + 0.02, r);
    // 整体 Alpha = 按钮本体层 + 阴影层
    float alpha = max(alpha_btn, shadow_mask * 0.75); 
    
    // 在按钮本体外的单纯展示阴影
    vec3 final_color = mix(vec3(0.0, 0.0, 0.0), color, alpha_btn);
    
    COLOR = vec4(final_color, alpha);
}
";

    public const string Pseudo3DTextShader = @"
shader_type canvas_item;
uniform float hover_weight = 0.0;

void fragment() {
    vec3 base_color = COLOR.rgb;
    
    // 通过亮度 (luma) 来区分白色的文字本体，还是深紫色的外描边/黑色阴影
    float luma = dot(base_color, vec3(0.299, 0.587, 0.114));
    // text_mask 接近 1 时代表文字本体区域；接近 0 代表描边/阴影
    float text_mask = smoothstep(0.4, 0.8, luma);
    
    // 1. 给文字本体增加“秘银/金属质感”渐变 (上亮下暗)
    // 利用 UV.y 在整个 Label 范围内的分布构建立体渐变
    vec3 metallic_top = vec3(0.95, 0.96, 1.0); // 亮银
    vec3 metallic_bottom = vec3(0.5, 0.52, 0.58); // 暗银
    vec3 text_grad = mix(metallic_top, metallic_bottom, smoothstep(0.2, 0.8, UV.y));
    
    // 2. 伪造内阴影/高光 (塑造金属雕刻倒角)
    // 顶部边缘极大提亮：模仿朝上的受光面
    float top_highlight = smoothstep(0.4, 0.25, UV.y) * 0.5;
    text_grad += vec3(top_highlight);
    // 底部少量反光：模仿下方环境反弹光
    float bottom_bounce = smoothstep(0.7, 0.9, UV.y) * 0.15;
    text_grad += vec3(bottom_bounce);
    
    // 3. 悬停状态：动态光晕与扫描线
    float sweep = smoothstep(0.4, 0.5, fract(UV.x * 2.5 - UV.y - TIME * 1.5)) 
                * smoothstep(0.6, 0.5, fract(UV.x * 2.5 - UV.y - TIME * 1.5));
                
    // 悬停时光扫呈现一种灵能青绿色
    vec3 glow_color = vec3(0.5, 1.0, 0.8) * sweep * 1.5 * hover_weight;
    
    // 悬停时文字本体整体泛起的微光
    vec3 hover_boost = vec3(0.1, 0.25, 0.25) * hover_weight;
    
    // 将光效叠加到金属字体材质上
    vec3 final_text = text_grad + glow_color + hover_boost;
    
    // 4. 合成输出
    // 描边/阴影部分保持原先颜色的基础，在悬停时略微让阴影/描边带一点紫红色发光
    vec3 outline_color = base_color + vec3(0.15, 0.05, 0.2) * hover_weight * 0.5;
    
    // 利用 mask 合并金属内芯与深色边框
    COLOR.rgb = mix(outline_color, final_text, text_mask);
}
";
}
