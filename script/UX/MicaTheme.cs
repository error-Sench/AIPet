using Godot;

namespace desktop.script.UX;

/// <summary>
/// 「云母」(Mica) 视觉规范：浅色半透明磨砂，统一圆角/间距/配色。
/// Godot 默认主题是深色，浅色面板上必须显式覆盖各控件的 StyleBox，故集中在此。
/// 约定：标识符英文，注释中文（见 AIPet-Agent.md §8）。
/// </summary>
public static class MicaTheme
{
    // —— 配色 ——
    public static readonly Color 主文字 = new(0.11f, 0.14f, 0.22f);
    public static readonly Color 次文字 = new(0.39f, 0.44f, 0.55f);
    public static readonly Color 强调 = new(0.24f, 0.45f, 0.88f);
    public static readonly Color 你说色 = new(0.16f, 0.38f, 0.78f);
    public static readonly Color 桌宠说色 = new(0.62f, 0.31f, 0.67f);
    /// <summary>危险色：只给「退出桌宠」这类终结动作（红底按钮 / 退出确认）。</summary>
    public static readonly Color 危险 = new(0.84f, 0.28f, 0.28f);

    private static readonly Color 底 = new(0.94f, 0.96f, 1f, 0.94f);
    private static readonly Color 描边 = new(1f, 1f, 1f, 0.90f);
    private static readonly Color 控件底 = new(1f, 1f, 1f, 0.78f);
    private static readonly Color 控件悬停 = new(1f, 1f, 1f, 0.98f);
    private static readonly Color 控件按下 = new(0.87f, 0.91f, 1f, 1f);
    private static readonly Color 柔和强调 = new(0.84f, 0.90f, 1f, 0.72f);

    /// <summary>主面板：浅色磨砂 + 圆角 + 柔和投影。</summary>
    public static StyleBoxFlat 面板(float 圆角 = 18)
    {
        return new StyleBoxFlat
        {
            BgColor = 底,
            BorderColor = 描边,
            BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            CornerRadiusTopLeft = (int)圆角, CornerRadiusTopRight = (int)圆角,
            CornerRadiusBottomLeft = (int)圆角, CornerRadiusBottomRight = (int)圆角,
            ContentMarginLeft = 14, ContentMarginRight = 14,
            ContentMarginTop = 13, ContentMarginBottom = 13,
            ShadowColor = new Color(0.10f, 0.18f, 0.34f, 0.20f),
            ShadowSize = 14, ShadowOffset = new Vector2(0, 6),
        };
    }

    public static StyleBoxFlat 控件(bool 聚焦 = false)
    {
        return new StyleBoxFlat
        {
            BgColor = 聚焦 ? 控件悬停 : 控件底,
            BorderColor = 聚焦 ? 强调 : new Color(0f, 0f, 0f, 0.10f),
            BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            CornerRadiusTopLeft = 9, CornerRadiusTopRight = 9,
            CornerRadiusBottomLeft = 9, CornerRadiusBottomRight = 9,
            ContentMarginLeft = 8, ContentMarginRight = 8,
            ContentMarginTop = 4, ContentMarginBottom = 4,
        };
    }

    public static StyleBoxFlat 圆角按钮(Color 底色)
    {
        return new StyleBoxFlat
        {
            BgColor = 底色,
            CornerRadiusTopLeft = 9, CornerRadiusTopRight = 9,
            CornerRadiusBottomLeft = 9, CornerRadiusBottomRight = 9,
            ContentMarginLeft = 8, ContentMarginRight = 8,
            ContentMarginTop = 4, ContentMarginBottom = 4,
        };
    }

    /// <summary>轻量分区背景：用在标题与状态等信息容器，避免整窗只有一块平面。</summary>
    public static StyleBoxFlat 信息底()
    {
        return new StyleBoxFlat
        {
            BgColor = 柔和强调,
            CornerRadiusTopLeft = 11, CornerRadiusTopRight = 11,
            CornerRadiusBottomLeft = 11, CornerRadiusBottomRight = 11,
            ContentMarginLeft = 9, ContentMarginRight = 9,
            ContentMarginTop = 5, ContentMarginBottom = 5,
        };
    }

    /// <summary>细分隔线，颜色比默认主题更适合浅色磨砂面板。</summary>
    public static StyleBoxFlat 分隔线() => new()
    {
        BgColor = new Color(0.22f, 0.31f, 0.48f, 0.10f),
        ContentMarginTop = 1,
    };

    /// <summary>把云母风格套到下拉框（OptionButton）上。</summary>
    public static void 应用(OptionButton o, int 字号 = 12)
    {
        if (o == null) return;
        o.AddThemeStyleboxOverride("normal", 控件());
        o.AddThemeStyleboxOverride("hover", 控件(聚焦: true));
        o.AddThemeStyleboxOverride("pressed", 控件(聚焦: true));
        o.AddThemeStyleboxOverride("focus", 控件(聚焦: true));
        o.AddThemeStyleboxOverride("disabled", 控件());
        o.AddThemeColorOverride("font_color", 主文字);
        o.AddThemeColorOverride("font_hover_color", 主文字);
        o.AddThemeColorOverride("font_pressed_color", 主文字);
        o.AddThemeColorOverride("font_focus_color", 主文字);
        o.AddThemeFontSizeOverride("font_size", 字号);
        o.AddThemeConstantOverride("arrow_margin", 8);
    }

    /// <summary>下拉框弹出的菜单也是窗口，Godot 默认深色底与浅色面板割裂，需一并覆盖。</summary>
    public static void 应用弹窗(PopupMenu p, int 字号 = 12)
    {
        if (p == null) return;
        p.AddThemeStyleboxOverride("panel", 弹窗底());
        p.AddThemeStyleboxOverride("hover", 圆角按钮(柔和强调));
        p.AddThemeStyleboxOverride("separator", 分隔线());
        p.AddThemeColorOverride("font_color", 主文字);
        p.AddThemeColorOverride("font_hover_color", 主文字);
        p.AddThemeColorOverride("font_disabled_color", 次文字);
        p.AddThemeFontSizeOverride("font_size", 字号);
        p.AddThemeConstantOverride("item_start_padding", 10);
        p.AddThemeConstantOverride("item_end_padding", 10);
        p.AddThemeConstantOverride("v_separation", 4);
    }

    /// <summary>弹出菜单底色：比主面板更实，保证浮在桌面上时文字清晰。</summary>
    public static StyleBoxFlat 弹窗底()
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(0.97f, 0.98f, 1f, 0.99f),
            BorderColor = new Color(0.22f, 0.31f, 0.48f, 0.16f),
            BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            ContentMarginLeft = 6, ContentMarginRight = 6,
            ContentMarginTop = 6, ContentMarginBottom = 6,
            ShadowColor = new Color(0.10f, 0.18f, 0.34f, 0.22f),
            ShadowSize = 12, ShadowOffset = new Vector2(0, 4),
        };
    }

    /// <summary>把云母风格套到页签容器（TabContainer）上：浅色选中态 + 透明页签条。</summary>
    public static void 应用(TabContainer t, int 字号 = 12)
    {
        if (t == null) return;
        var 页体 = new StyleBoxFlat
        {
            BgColor = new Color(1f, 1f, 1f, 0.35f),
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            ContentMarginLeft = 12, ContentMarginRight = 12,
            ContentMarginTop = 10, ContentMarginBottom = 10,
        };
        t.AddThemeStyleboxOverride("panel", 页体);
        t.AddThemeStyleboxOverride("tab_selected", 圆角按钮(控件悬停));
        t.AddThemeStyleboxOverride("tab_unselected", 圆角按钮(new Color(0, 0, 0, 0)));
        t.AddThemeStyleboxOverride("tab_hovered", 圆角按钮(柔和强调));
        t.AddThemeStyleboxOverride("tab_disabled", 圆角按钮(new Color(0, 0, 0, 0)));
        t.AddThemeColorOverride("font_selected_color", 主文字);
        t.AddThemeColorOverride("font_unselected_color", 次文字);
        t.AddThemeColorOverride("font_hovered_color", 强调);
        t.AddThemeColorOverride("font_disabled_color", 次文字);
        t.AddThemeFontSizeOverride("font_size", 字号);
        t.AddThemeConstantOverride("side_margin", 4);
        t.AddThemeConstantOverride("tab_separation", 2);
    }

    /// <summary>开关按钮（ToggleMode）：开＝强调底色（白字），关＝普通控件底。配置窗用。</summary>
    public static void 应用开关(Button b, bool 开)
    {
        if (b == null) return;
        b.Text = 开 ? "开" : "关";
        if (开)
        {
            b.AddThemeStyleboxOverride("normal", 圆角按钮(强调));
            b.AddThemeStyleboxOverride("hover", 圆角按钮(new Color(0.31f, 0.52f, 0.96f)));
            b.AddThemeStyleboxOverride("pressed", 圆角按钮(强调));
            b.AddThemeColorOverride("font_color", Colors.White);
            b.AddThemeColorOverride("font_hover_color", Colors.White);
            b.AddThemeColorOverride("font_pressed_color", Colors.White);
        }
        else
        {
            b.AddThemeStyleboxOverride("normal", 圆角按钮(控件底));
            b.AddThemeStyleboxOverride("hover", 圆角按钮(控件悬停));
            b.AddThemeStyleboxOverride("pressed", 圆角按钮(控件按下));
            b.AddThemeColorOverride("font_color", 主文字);
            b.AddThemeColorOverride("font_hover_color", 强调);
            b.AddThemeColorOverride("font_pressed_color", 主文字);
        }
        b.AddThemeFontSizeOverride("font_size", 12);
    }

    /// <summary>把云母风格套到输入框上。</summary>
    public static void 应用(LineEdit e, int 字号 = 13)
    {
        if (e == null) return;
        e.AddThemeStyleboxOverride("normal", 控件());
        e.AddThemeStyleboxOverride("focus", 控件(聚焦: true));
        e.AddThemeColorOverride("font_color", 主文字);
        e.AddThemeColorOverride("font_placeholder_color", 次文字);
        e.AddThemeColorOverride("caret_color", 强调);
        e.AddThemeFontSizeOverride("font_size", 字号);
    }

    /// <summary>把云母风格套到按钮上（flat 按钮用透明底）。</summary>
    /// <param name="对齐">是否把图标+文字靠左对齐（工具栏按钮用，保证图标纵向对齐）。</param>
    public static void 应用(Button b, int 字号 = 12, bool 扁平 = false, bool 对齐 = false)
    {
        if (b == null) return;
        if (扁平)
        {
            b.AddThemeStyleboxOverride("normal", 圆角按钮(new Color(0, 0, 0, 0)));
            b.AddThemeStyleboxOverride("hover", 圆角按钮(new Color(0f, 0f, 0f, 0.06f)));
            b.AddThemeStyleboxOverride("pressed", 圆角按钮(new Color(0f, 0f, 0f, 0.10f)));
        }
        else
        {
            b.AddThemeStyleboxOverride("normal", 圆角按钮(控件底));
            b.AddThemeStyleboxOverride("hover", 圆角按钮(控件悬停));
            b.AddThemeStyleboxOverride("pressed", 圆角按钮(控件按下));
        }
        b.AddThemeColorOverride("font_color", 主文字);
        b.AddThemeColorOverride("font_hover_color", 强调);
        b.AddThemeColorOverride("font_pressed_color", 主文字);
        b.AddThemeFontSizeOverride("font_size", 字号);

        // 图标染色：原素材是白色 PNG，浅色面板上必须调成深色才可见
        b.AddThemeColorOverride("icon_normal_color", 主文字);
        b.AddThemeColorOverride("icon_hover_color", 强调);
        b.AddThemeColorOverride("icon_pressed_color", 主文字);
        b.AddThemeColorOverride("icon_disabled_color", 次文字);

        if (对齐)
        {
            b.Alignment = HorizontalAlignment.Left;
            b.IconAlignment = HorizontalAlignment.Left;
            b.ExpandIcon = false;
            b.AddThemeConstantOverride("h_separation", 5);
        }
    }

    /// <summary>强调按钮：只用于每个面板的主要动作，避免界面处处抢眼。</summary>
    public static void 应用强调按钮(Button b, int 字号 = 12)
    {
        if (b == null) return;
        b.AddThemeStyleboxOverride("normal", 圆角按钮(强调));
        b.AddThemeStyleboxOverride("hover", 圆角按钮(new Color(0.31f, 0.52f, 0.96f)));
        b.AddThemeStyleboxOverride("pressed", 圆角按钮(new Color(0.18f, 0.36f, 0.75f)));
        b.AddThemeColorOverride("font_color", Colors.White);
        b.AddThemeColorOverride("font_hover_color", Colors.White);
        b.AddThemeColorOverride("font_pressed_color", Colors.White);
        b.AddThemeFontSizeOverride("font_size", 字号);
    }

    /// <summary>危险按钮：红底白字白图标（只用于「退出桌宠」这类终结动作）。</summary>
    public static void 应用危险按钮(Button b, int 字号 = 11)
    {
        if (b == null) return;
        b.AddThemeStyleboxOverride("normal", 圆角按钮(危险));
        b.AddThemeStyleboxOverride("hover", 圆角按钮(new Color(0.92f, 0.38f, 0.38f)));
        b.AddThemeStyleboxOverride("pressed", 圆角按钮(new Color(0.70f, 0.20f, 0.20f)));
        b.AddThemeColorOverride("font_color", Colors.White);
        b.AddThemeColorOverride("font_hover_color", Colors.White);
        b.AddThemeColorOverride("font_pressed_color", Colors.White);
        b.AddThemeFontSizeOverride("font_size", 字号);
        // 图标与文字同色（素材是白色 PNG，红底上保持白色；不许沿用「染成主文字色」的浅色底规则）
        b.AddThemeColorOverride("icon_normal_color", Colors.White);
        b.AddThemeColorOverride("icon_hover_color", Colors.White);
        b.AddThemeColorOverride("icon_pressed_color", Colors.White);
    }

    /// <summary>命令按钮：图标 + 文字，统一尺寸与对齐。</summary>
    public static void 应用命令按钮(Button b, int 字号 = 11)
    {
        应用(b, 字号, 扁平: true, 对齐: true);
        if (b == null) return;
        b.AddThemeConstantOverride("icon_max_width", 15);
    }

    public static void 应用(Label l, int 字号 = 12, bool 次要 = false)
    {
        if (l == null) return;
        l.AddThemeColorOverride("font_color", 次要 ? 次文字 : 主文字);
        l.AddThemeFontSizeOverride("font_size", 字号);
    }
}
