using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace desktop.script.UX;

/// <summary>
/// 桌宠**气泡窗口**（P9 重做）：独立无边框透明窗口 —— **按内容自适应**、跟随桌宠、头顶弹出、鼠标穿透。
/// <para>
/// 为什么独立成窗：气泡原先挂在桌宠窗口里的一块**固定 512×128 的 RichTextLabel**（字号 32）上 ——
/// 桌宠窗口只有角色那么大（缩放 × 512），于是文字稍长就被窗口裁掉、字号相对巨大、位置钉死在左上角。
/// 独立窗口才能同时做到：按内容定尺寸 / 屏幕内夹取 / 跟随拖拽与贴边 / 字号与风格可控。
/// </para>
/// <para>
/// 形态：圆角半透明面板（Mica 浅色风，可切深色底）+ 打字机效果；**鼠标穿透**（点气泡 = 点到底下的东西，不抢焦点）。
/// 位置：桌宠头顶居中对齐（上方放不下自动翻到脚下），四边夹在屏幕可用区内；显示期间**每帧跟随**。
/// 配置：`config/bubble.json`（时长 + 字号 / 最大宽度 / 内边距 / 圆角 / 不透明度 / 深色底 / 波浪 / 距头顶 / 打字速度）。
/// </para>
/// <para>线程：只允许在主线程调用（`Dialogue` 的三个入口都用 `CallDeferred` marshal 过来）。</para>
/// 约定：标识符英文，注释中文（见 AIPet-Agent.md §8）。
/// </summary>
public partial class BubbleWindow : Window
{
    // ===== 外观与节律（config/bubble.json，缺省内置）=====
    private static int 字号 = 15;
    /// <summary>最大宽度 = **气泡本体**（不含投影留白）的最大宽度；到了就折行、向上长高。</summary>
    private static int 最大宽度 = 360;
    private static int 内边距 = 11;
    private static int 圆角 = 12;
    private static float 不透明度 = 0.93f;
    private static bool 深色底 = false;
    private static bool 波浪效果 = false;
    private static int 距头顶像素 = 14;
    private static float 打字速度 = 24f;          // 字/秒（0 = 不打字机，直接整段出现）

    private const int 阴影留白 = 6;                 // 窗口四周留给投影的空隙（面板向内缩这么多）
    private const int 行距 = 4;

    private static BubbleWindow _单例;

    private MarginContainer _边距框;
    private RichTextLabel _文本;
    private Tween _打字;
    private double _剩余秒;
    private string _原文本 = "";
    private bool _已二遍;

    // ================= 对外状态 =================

    public static bool 存在 => _单例 != null && GodotObject.IsInstanceValid(_单例);
    public static bool 可见中 => 存在 && _单例.Visible;

    /// <summary>气泡显示时长（秒）——`config/bubble.json` 的 `气泡显示秒`（默认 4）。**所有气泡共用**。</summary>
    public static float 气泡显示秒 { get; private set; } = 4f;

    public static string 探针_当前文本 => 存在 ? _单例._原文本 : "";
    public static string 探针_解析文本 => 存在 ? _单例._文本.GetParsedText() : "";
    public static Vector2I 探针_当前尺寸 => 存在 ? _单例.Size : Vector2I.Zero;
    public static double 探针_剩余秒 => 存在 ? _单例._剩余秒 : 0;
    public static bool 探针_鼠标穿透 => 存在 && _单例.MousePassthrough;
    public static int 探针_字号 => 存在 ? _单例._文本.GetThemeFontSize("normal_font_size") : 0;
    public static int 探针_配置字号 => 字号;
    public static int 探针_配置最大宽度 => 最大宽度;

    /// <summary>探针：窗口允许的最大宽度 = 气泡本体上限 + 投影留白 + 2（防圆角被裁）。</summary>
    public static int 探针_最大窗宽 => 最大宽度 + 阴影留白 * 2 + 2;

    /// <summary>探针：气泡窗当前位置（位置断言用；headless 下没有真实屏幕信息）。</summary>
    public static Vector2I 探针_位置 => 存在 ? _单例.Position : Vector2I.Zero;

    /// <summary>探针（**非 headless 才有意义**）：把气泡窗自己的渲染结果抓成图片（含 alpha）。</summary>
    public static Image 探针_截图()
    {
        if (!存在) return null;
        var 图 = _单例.GetTexture()?.GetImage();
        if (图 == null) return null;
        if (图.IsCompressed()) 图.Decompress();
        return 图;
    }

    /// <summary>探针：直接推进气泡计时（不等真实时间）——验证「到点自动收」。</summary>
    public static void 探针_推进时间(double 秒)
    {
        if (!存在 || !_单例.Visible || _单例._剩余秒 <= 0) return;
        _单例._剩余秒 -= 秒;
        if (_单例._剩余秒 <= 0)
        {
            _单例._剩余秒 = 0;
            _单例.隐藏_立即();
        }
    }

    /// <summary>探针：直接设时长（验证「到点自动收」而不等 4 秒）。</summary>
    public static void 探针_设气泡秒(float 秒) => 气泡显示秒 = Math.Clamp(秒, 0.2f, 600f);

    // ================= 配置 =================

    /// <summary>读配置：`config/bubble.json`（气泡专档，时长 + 外观）→ 老位置 `behavior.json` 兜底。</summary>
    public static void 载入配置()
    {
        foreach (var 路径 in Util.ConfigFile.候选("bubble.json").Concat(Util.ConfigFile.候选("config/bubble.json")))
        {
            try
            {
                if (!File.Exists(路径)) continue;
                using var 文档 = JsonDocument.Parse(File.ReadAllText(路径));
                var 根 = 文档.RootElement;
                if (根.TryGetProperty("气泡显示秒", out var v0) && v0.TryGetSingle(out var s) && s > 0) 气泡显示秒 = Math.Clamp(s, 0.5f, 60f);
                if (根.TryGetProperty("字号", out var v1) && v1.TryGetInt32(out var a)) 字号 = Math.Clamp(a, 9, 40);
                if (根.TryGetProperty("最大宽度像素", out var v2) && v2.TryGetInt32(out var b)) 最大宽度 = Math.Clamp(b, 120, 1200);
                if (根.TryGetProperty("内边距像素", out var v3) && v3.TryGetInt32(out var c)) 内边距 = Math.Clamp(c, 2, 40);
                if (根.TryGetProperty("圆角像素", out var v4) && v4.TryGetInt32(out var d)) 圆角 = Math.Clamp(d, 0, 40);
                if (根.TryGetProperty("不透明度", out var v5) && v5.TryGetSingle(out var e)) 不透明度 = Math.Clamp(e, 0.3f, 1f);
                if (根.TryGetProperty("深色底", out var v6) && v6.ValueKind is JsonValueKind.True or JsonValueKind.False) 深色底 = v6.GetBoolean();
                if (根.TryGetProperty("波浪效果", out var v7) && v7.ValueKind is JsonValueKind.True or JsonValueKind.False) 波浪效果 = v7.GetBoolean();
                if (根.TryGetProperty("距头顶像素", out var v8) && v8.TryGetInt32(out var f)) 距头顶像素 = Math.Clamp(f, -40, 300);
                if (根.TryGetProperty("打字速度字每秒", out var v9) && v9.TryGetSingle(out var g) && g >= 0) 打字速度 = Math.Clamp(g, 0f, 200f);
                GD.Print($"[Bubble] 配置: 字号={字号} 最大宽度={最大宽度} 时长={气泡显示秒:0.#}s（{路径}）");
                return;
            }
            catch (Exception ex) { GD.PrintErr($"[Bubble] 读 {路径} 失败: {ex.Message}"); }
        }

        // 老位置兜底（老配置里时长写在 behavior.json）—— 读到就照用，方便平滑迁移
        foreach (var 路径 in Util.ConfigFile.候选("behavior.json").Concat(Util.ConfigFile.候选("config/behavior.json")))
        {
            try
            {
                if (!File.Exists(路径)) continue;
                using var 文档 = JsonDocument.Parse(File.ReadAllText(路径));
                if (文档.RootElement.TryGetProperty("气泡显示秒", out var v) && v.TryGetSingle(out var s) && s > 0)
                {
                    气泡显示秒 = Math.Clamp(s, 0.5f, 60f);
                    GD.Print($"[Bubble] 时长 {气泡显示秒:0.#}s 读自 behavior.json（新家是 config/bubble.json）");
                }
                break;
            }
            catch { /* 老位置读不到就算了：用缺省 4 秒 */ }
        }
    }

    // ================= 对外入口 =================

    /// <summary>显示气泡。`显示秒 > 0` = 到点自动收；`≤ 0` = 常驻（等 `隐藏()`）。</summary>
    public static void 显示(string 文本, float 显示秒 = 0f)
    {
        if (string.IsNullOrEmpty(文本)) return;
        确保创建();
        if (!存在) return;
        _单例.显示_立即(文本, 显示秒);
    }

    /// <summary>流式追加（Agent 回复逐块到达）：不清空、不重放打字机 —— 直接整段显示并重算尺寸。</summary>
    public static void 追加(string 块)
    {
        if (string.IsNullOrEmpty(块)) return;
        确保创建();
        if (!存在) return;
        _单例.追加_立即(块);
    }

    public static void 隐藏()
    {
        if (!存在) return;
        _单例.隐藏_立即();
    }

    private static void 确保创建()
    {
        if (存在) return;
        try
        {
            var 窗 = new BubbleWindow { Name = "BubbleWindow" };
            ((SceneTree)Engine.GetMainLoop()).Root.AddChild(窗);
        }
        catch (Exception e) { GD.PrintErr($"[Bubble] 创建失败: {e.Message}"); }
    }

    // ================= 生命周期 =================

    public override void _Ready()
    {
        _单例 = this;
        Title = "气泡";
        Visible = false;
        Borderless = true;
        Transparent = true;
        AlwaysOnTop = true;
        Unresizable = true;
        Unfocusable = true;                 // 是「看」的，不抢焦点
        MousePassthrough = true;            // ★ 鼠标穿透：点气泡 = 点到底下的东西（Windows 支持）
        MinSize = new Vector2I(24, 24);
        Size = new Vector2I(180, 40);
        CloseRequested += 隐藏_立即;
        构建界面();
        GD.Print("[Bubble] 气泡窗口就绪（独立窗 / 自适应 / 跟随 / 鼠标穿透）");
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        跟随桌宠();
        if (_剩余秒 <= 0) return;
        _剩余秒 -= delta;
        if (_剩余秒 <= 0)
        {
            _剩余秒 = 0;
            隐藏_立即();
        }
    }

    // ================= 显示 / 隐藏 =================

    private void 显示_立即(string 文本, float 显示秒)
    {
        _原文本 = 文本;
        _文本.Text = 装饰后文本(文本);
        var 字符数 = _文本.GetTotalCharacterCount();
        重算尺寸();
        打字机(字符数);
        _剩余秒 = 显示秒 > 0 ? 显示秒 : 0;
        跟随桌宠();
        Show();
    }

    private void 追加_立即(string 块)
    {
        _原文本 += 块;
        _文本.Text = 装饰后文本(_原文本);
        _打字?.Kill();                      // 流式不需要打字机：整段直接可见
        _打字 = null;
        _文本.VisibleCharacters = -1;
        重算尺寸();
        _剩余秒 = 0;                        // 流式中不自动收（收尾由结束流式统一定时）
        跟随桌宠();
        Show();
    }

    private void 隐藏_立即()
    {
        _打字?.Kill();
        _打字 = null;
        _剩余秒 = 0;
        Hide();
    }

    /// <summary>文本 → RichTextLabel 富文本：**先转义**（气泡文本一律当纯文本，见 ChatBox 同款约定），再按需套波浪。</summary>
    private static string 装饰后文本(string 文本)
    {
        var 转义 = (文本 ?? "").Replace("[", "[lb]");
        return 波浪效果 ? $"[wave]{转义}[/wave]" : 转义;
    }

    /// <summary>打字机：**必须先 Kill 上一条 tween** —— 连发气泡时两条 tween 会同时改 visible_characters（老代码实测的坑）。</summary>
    private void 打字机(int 字符数)
    {
        _打字?.Kill();
        _打字 = null;
        if (字符数 <= 0) { _文本.VisibleCharacters = -1; return; }
        if (打字速度 <= 0f) { _文本.VisibleCharacters = -1; return; }
        _文本.VisibleCharacters = 0;
        var 时长 = 字符数 / 打字速度;
        _打字 = CreateTween();
        _打字.TweenProperty(_文本, "visible_characters", 字符数, 时长).SetTrans(Tween.TransitionType.Linear);
    }

    // ================= 自适应大小 =================

    /// <summary>
    /// 按内容重算窗口尺寸：短文本用自然宽（气泡就是小的），长文本按「最大宽度」折行、向上长高。
    /// 用字体实测（`Font.GetMultilineStringSize`）而不是猜行数 —— 中英混排在 CJK 断行下也算得准。
    /// </summary>
    public void 重算尺寸()
    {
        if (_文本 == null || _边距框 == null) return;
        var 纯文本 = _文本.GetParsedText();
        var 字体 = _文本.GetThemeFont("normal_font");
        var 字号px = _文本.GetThemeFontSize("normal_font_size");
        if (字体 == null || 字号px <= 0 || string.IsNullOrEmpty(纯文本)) return;

        var 文本上限 = Math.Max(60, 最大宽度 - 内边距 * 2);
        var 自然 = 字体.GetMultilineStringSize(纯文本, HorizontalAlignment.Left, -1, 字号px);
        var 文本宽 = Mathf.Clamp(Mathf.Ceil(自然.X), 24, 文本上限);

        var 单行高 = 字体.GetHeight(字号px);
        var 包裹 = 字体.GetMultilineStringSize(纯文本, HorizontalAlignment.Left, 文本宽, 字号px);
        var 行数 = Math.Max(1, Mathf.RoundToInt(包裹.Y / Mathf.Max(1f, 单行高)));
        var 文本高 = Mathf.Ceil(包裹.Y) + Math.Max(0, 行数 - 1) * 行距;

        _文本.CustomMinimumSize = new Vector2(文本宽, 文本高);

        var 内容 = _边距框.GetCombinedMinimumSize();
        if (内容.X <= 0 || 内容.Y <= 0) return;
        var 目标 = new Vector2I(Mathf.CeilToInt(内容.X) + 2, Mathf.CeilToInt(内容.Y) + 2);   // +2 防圆角/描边被裁
        MinSize = new Vector2I(24, 24);
        Size = 目标;

        // **二遍校正**：同一帧里 RichTextLabel 仍按「旧宽度」算自身最小高度（折行数偏多 → 气泡虚高），
        // 下一帧它才是按最终宽度算的。所以隔一帧再量一次（只校正一次，不来回振荡）。
        if (_已二遍) return;
        _已二遍 = true;
        CallDeferred(nameof(二遍校正));
    }

    private void 二遍校正()
    {
        try { if (Visible) 重算尺寸(); }
        finally { _已二遍 = false; }
    }

    // ================= 位置：头顶 + 跟随 + 夹屏 =================

    /// <summary>
    /// 摆在桌宠头顶：水平居中于桌宠窗口、上边距 `距头顶像素`；上方放不下就翻到脚下；四边夹进屏幕可用区。
    /// 主窗口（无参 DisplayServer 读的）就是桌宠窗口 —— 拖拽 / 走动 / 贴边隐藏时每帧跟一次。
    /// </summary>
    public void 跟随桌宠()
    {
        var 屏 = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
        if (屏.Size.X <= 0 || 屏.Size.Y <= 0) return;          // headless / 无屏幕信息：别瞎动
        var 宠位 = DisplayServer.WindowGetPosition();
        var 宠尺 = DisplayServer.WindowGetSize();
        if (宠尺.X <= 0 || 宠尺.Y <= 0) return;

        var 视觉高 = Math.Max(8, Size.Y - 阴影留白 * 2);        // 去掉投影留白后的「气泡本体」高度
        var x = 宠位.X + (宠尺.X - Size.X) / 2;
        var y = 宠位.Y - 视觉高 - 距头顶像素 + 阴影留白;          // 本体底部离头顶 `距头顶像素`
        if (y < 屏.Position.Y) y = 宠位.Y + 宠尺.Y + 距头顶像素 - 阴影留白;   // 上面放不下 → 翻到脚下

        x = Math.Clamp(x, 屏.Position.X, Math.Max(屏.Position.X, 屏.End.X - Size.X));
        y = Math.Clamp(y, 屏.Position.Y, Math.Max(屏.Position.Y, 屏.End.Y - Size.Y));
        var 目标 = new Vector2I(x, y);
        if (Position != 目标) Position = 目标;
    }

    // ================= UI =================

    private void 构建界面()
    {
        _边距框 = new MarginContainer { Name = "Margin" };
        _边距框.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        foreach (var 名 in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            _边距框.AddThemeConstantOverride(名, 阴影留白);
        AddChild(_边距框);

        var 面板 = new PanelContainer { Name = "Panel" };
        面板.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = 底色(),
            BorderColor = 深色底 ? new Color(1f, 1f, 1f, 0.10f) : new Color(1f, 1f, 1f, 0.85f),
            BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            CornerRadiusTopLeft = 圆角, CornerRadiusTopRight = 圆角,
            CornerRadiusBottomLeft = 圆角, CornerRadiusBottomRight = 圆角,
            ContentMarginLeft = 内边距, ContentMarginRight = 内边距,
            ContentMarginTop = Math.Max(2, 内边距 - 4), ContentMarginBottom = Math.Max(2, 内边距 - 4),
            ShadowColor = new Color(0.10f, 0.18f, 0.34f, 深色底 ? 0.30f : 0.20f),
            ShadowSize = 阴影留白, ShadowOffset = new Vector2(0, 2),
        });
        _边距框.AddChild(面板);

        _文本 = new RichTextLabel
        {
            Name = "Text",
            BbcodeEnabled = true,                      // 只为装饰用（波浪/转义）
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,   // 中文按字断行，英文按词
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        _文本.AddThemeColorOverride("default_color", 深色底 ? new Color(0.96f, 0.97f, 1f) : MicaTheme.主文字);
        _文本.AddThemeFontSizeOverride("normal_font_size", 字号);
        _文本.AddThemeConstantOverride("line_separation", 行距);
        面板.AddChild(_文本);
    }

    private static Color 底色()
    {
        var 基 = 深色底 ? new Color(0.09f, 0.11f, 0.16f) : new Color(0.97f, 0.98f, 1f);
        return new Color(基.R, 基.G, 基.B, 不透明度);
    }
}
