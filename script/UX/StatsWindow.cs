using System.Collections.Generic;
using desktop.script.Soul;
using Godot;

namespace desktop.script.UX;

/// <summary>
/// 状态窗（**独立窗口**，与配置窗同型）：显示桌宠此刻的「感受」——心情 / 精力 / 亲密（P5 数值层）。
/// <para>
/// 入口：聊天面板底部命令栏的「状态」按钮（主人指定：可视化按钮放菜单栏）。
/// 数值与人格分离（AGENTS.md §2）：这里**只显示数值**，不改人格、不改说话方式。
/// 无边框窗口的关闭沿用面板范式（自绘 × + CloseRequested + 系统标题栏）。
/// </para>
/// 约定：标识符英文，注释中文（见 AGENTS.md §8）。
/// </summary>
public partial class StatsWindow : Window
{
    private const int 面板宽 = 360;
    private const int 面板高 = 188; // 视觉复核：206 时底部留白偏大、重心偏上

    private static StatsWindow _单例;

    private readonly List<(ProgressBar 条, Label 数, System.Func<float> 取值)> _行 = new();
    private Label _概览;

    public static bool 存在 => _单例 != null;

    /// <summary>状态窗位于鼠标下方时优先接管指针，避免底层桌宠误入拖拽（同 ChatBox / ToolBar / 配置窗）。</summary>
    public static bool 正在接管指针
    {
        get
        {
            if (_单例 is not { Visible: true }) return false;
            var mouse = DisplayServer.MouseGetPosition();
            var pos = _单例.Position;
            var size = _单例.Size;
            return mouse.X >= pos.X && mouse.X < pos.X + size.X &&
                   mouse.Y >= pos.Y && mouse.Y < pos.Y + size.Y;
        }
    }

    public override void _Ready()
    {
        _单例 = this;
        Title = "状态";
        Visible = false;
        Borderless = true;
        Transparent = true;
        Unresizable = true;
        MinSize = new Vector2I(320, 180);
        Size = new Vector2I(面板宽, 面板高);
        CloseRequested += 隐藏;
        BuildUi();
        GD.Print("[StatsWindow] 就绪");
    }

    public static void 显示()
    {
        if (_单例 == null) return;
        _单例.Size = new Vector2I(面板宽, 面板高);
        _单例.刷新();
        _单例.Show();
    }

    public static void 隐藏()
    {
        if (_单例 == null) return;
        _单例.Hide();
    }

    // ================= UI =================

    private void BuildUi()
    {
        var 根面板 = new PanelContainer { Name = "Root" };
        根面板.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        根面板.AddThemeStyleboxOverride("panel", MicaTheme.面板(14));
        AddChild(根面板);

        var 列 = new VBoxContainer();
        列.AddThemeConstantOverride("separation", 8);
        根面板.AddChild(列);

        // —— 标题行（无边框窗口没有系统标题栏，关闭按钮自绘） ——
        var 标题底 = new PanelContainer();
        标题底.AddThemeStyleboxOverride("panel", MicaTheme.信息底());
        列.AddChild(标题底);
        var 标题行 = new HBoxContainer();
        标题行.AddThemeConstantOverride("separation", 8);
        标题底.AddChild(标题行);
        var 标题 = new Label { Text = "小萝的状态", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        MicaTheme.应用(标题, 15);
        标题行.AddChild(标题);
        var 关闭 = new Button { Text = "×", TooltipText = "关闭", CustomMinimumSize = new Vector2(26, 24) };
        MicaTheme.应用(关闭, 16, 扁平: true);
        关闭.Pressed += 隐藏;
        标题行.AddChild(关闭);

        var 分隔 = new HSeparator();
        分隔.AddThemeStyleboxOverride("separator", MicaTheme.分隔线());
        列.AddChild(分隔);

        // —— 三行数值 ——
        数值行(列, "心情", 100f, MicaTheme.桌宠说色, () => StatsTable.当前心情);
        数值行(列, "精力", 100f, MicaTheme.强调, () => StatsTable.当前精力);
        数值行(列, "亲密", 999f, MicaTheme.亲密色, () => StatsTable.当前亲密);

        _概览 = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        MicaTheme.应用(_概览, 11, 次要: true);
        列.AddChild(_概览);
    }

    private void 数值行(VBoxContainer 列, string 标签, float 上限, Color 色, System.Func<float> 取值)
    {
        var 行 = new HBoxContainer();
        行.AddThemeConstantOverride("separation", 8);

        var 标签节点 = new Label
        {
            Text = 标签,
            CustomMinimumSize = new Vector2(48, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        MicaTheme.应用(标签节点, 12, 次要: true);
        行.AddChild(标签节点);

        var 条 = new ProgressBar
        {
            MaxValue = 上限,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 16),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        条.AddThemeStyleboxOverride("background", MicaTheme.控件());
        条.AddThemeStyleboxOverride("fill", MicaTheme.数值填充(色));
        行.AddChild(条);

        var 数 = new Label
        {
            // 视觉复核：字号调到 13 且用主文字色，让数值成为视觉重心；固定最小宽度防「999 / 999」顶到边界
            CustomMinimumSize = new Vector2(76, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        MicaTheme.应用(数, 13);
        行.AddChild(数);

        列.AddChild(行);
        _行.Add((条, 数, 取值));
    }

    /// <summary>把当前数值刷进控件（显示窗打开时每帧刷新，漂移也看得见）。</summary>
    private void 刷新()
    {
        foreach (var (条, 数, 取值) in _行)
        {
            var 值 = 取值();
            条.Value = 值;
            数.Text = $"{值:0} / {条.MaxValue:0}";
        }
        if (_概览 != null) _概览.Text = 概览文本();
    }

    /// <summary>一句话状态（只是描述数值，不替代人格说话）。</summary>
    private static string 概览文本()
    {
        if (StatsTable.精力不济) return "困了，可能随时打瞌睡…";
        if (StatsTable.心情低落) return "有点蔫，想安静一会儿…";
        if (StatsTable.当前心情 >= 75f) return "心情不错，精神头很足~";
        return "数值随时间与互动变化：摸摸、聊天都会让它开心；睡觉回精力。";
    }

    public override void _Process(double delta)
    {
        if (Visible) 刷新();
    }

    // ================= 探针专用 =================

    public static bool 可见_探针 => _单例 is { Visible: true };
    public static Vector2I 探针_窗口位置 => _单例?.Position ?? Vector2I.Zero;
    public static Vector2I 探针_窗口尺寸 => _单例?.Size ?? Vector2I.Zero;
    public static string 探针_概览文本 => _单例?._概览?.Text ?? "";

    /// <summary>探针：窗口里三行数值的显示文本（用于断言「显示值 == StatsTable 值」）。</summary>
    public static string 探针_数值文本 =>
        _单例 == null ? "" : string.Join(" | ", _单例._行.ConvertAll(r => r.数.Text));

    /// <summary>探针：把窗口区域截图存到 user://（供视觉复核）。</summary>
    public static void 探针_截图(string 文件名)
    {
        if (_单例 == null) return;
        var 路径 = ProjectSettings.GlobalizePath($"user://{文件名}");
        var 图 = _单例.GetTexture()?.GetImage();
        if (图 == null) { GD.PrintErr("[StatsWindow] 截图失败：纹理为空"); return; }
        图.SavePng(路径);
        GD.Print($"[StatsWindow] 截图: {路径} ({图.GetWidth()}x{图.GetHeight()})");
    }
}