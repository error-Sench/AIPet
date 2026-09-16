using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using Godot;

namespace desktop.script.UX;

/// <summary>
/// 网速监测**桌面气泡**（主人指定形态：不是面板窗，是浮在桌面上的气泡）。
/// <para>
/// 形态：无边框 + 半透明圆角胶囊，两行数字（↓下载 / ↑上传）+ 最近 60 秒迷你曲线。
/// 交互：**左键拖**挪位置、**右键关**（位置与开关状态记在 `user://netspeed.json`，下次还在那）；
/// 开关入口＝**工具栏**「网速监控」小组件（`mods/toolbar/netspeed`：点按开/关；删目录或加 `_` 前缀＝移除/禁用）。
/// 启动时按上次状态恢复：enabled=true 且工具栏入口仍在 → 自动显示。
/// </para>
/// <para>
/// 数据：`NetworkInterface` 的 IPv4 收发字节差分 —— **纯本地读取，不联网、不读任何内容、不记历史到磁盘**。
/// 只统计「已启用 + 非回环 + 非隧道」的网卡（否则 VPN/虚拟网卡会把数字拱起来）。
/// </para>
/// <para>
/// 实现注记：**不在 game.tscn 里挂节点**，而是首次打开时用代码懒创建（避免给新 .cs 生成 .cs.uid 的编辑器依赖，
/// 也保证不开气泡时零开销）。
/// </para>
/// 约定：标识符英文，注释中文（见 AGENTS.md §8）。
/// </summary>
public partial class NetSpeedBubble : Window
{
    private const int 宽 = 132;
    private const int 高 = 24;      // 单行紧凑气泡（主人两次要求：一行、再小）——真实尺寸由内容决定，见 按内容定尺寸()
    private const int 采样数 = 60;          // 1 秒一个样本
    private const double 采样间隔秒 = 1.0;

    private static NetSpeedBubble _单例;

    private Label _下行, _上行;
    private readonly double[] _历史 = new double[采样数];
    private int _历史下一位;
    private long _上次收, _上次发;
    private double _计时;
    private bool _首采样 = true;
    private bool _拖动中;
    private Vector2I _拖动起点差;

    private static string 配置文件 => 探针_配置路径覆写 ?? ProjectSettings.GlobalizePath("user://netspeed.json");

    /// <summary>探针：覆写配置路径（避免探针写真实用户数据，见打包审计 B6）；null＝真实路径。</summary>
    public static string 探针_配置路径覆写;

    // 外观可调（settings/widget.json，缺省内置）：主人可自己微调大小/透明度，不用改代码
    private static int 字号 = 10;
    private static int 边距 = 2;
    private static float 透明度 = 0.62f;   // 更透（主人反馈「透明区域不是那么透」）；settings/widget.json 可调 0.3-1.0

    public static void 载入外观配置()
    {
        foreach (var 路径 in Util.ConfigFile.候选("widget.json").Concat(Util.ConfigFile.候选("settings/widget.json")))
        {
            try
            {
                if (!System.IO.File.Exists(路径)) continue;
                using var 文档 = JsonDocument.Parse(System.IO.File.ReadAllText(路径));
                var 根 = 文档.RootElement;
                if (根.TryGetProperty("气泡字号", out var a) && a.TryGetInt32(out var av)) 字号 = Math.Clamp(av, 8, 22);
                if (根.TryGetProperty("气泡边距", out var b) && b.TryGetInt32(out var bv)) 边距 = Math.Clamp(bv, 2, 20);
                if (根.TryGetProperty("气泡透明度", out var c) && c.TryGetSingle(out var cv)) 透明度 = Math.Clamp(cv, 0.3f, 1f);
                GD.Print($"[NetSpeed] 外观: 字号={字号} 边距={边距} 透明度={透明度}");
                break;
            }
            catch (Exception e) { GD.PrintErr($"[NetSpeed] 读外观配置失败 {路径}: {e.Message}"); }
        }
    }

    public static bool 存在 => _单例 != null;
    public static bool 可见 => _单例 is { Visible: true };

    /// <summary>探针：最近一次算出的下行/上行速度（字节/秒）。</summary>
    public static double 探针_下行速度 { get; private set; }
    public static double 探针_上行速度 { get; private set; }

    // ================= 对外 =================

    /// <summary>开关（命令栏「网速」按钮用）。</summary>
    public static void 开关()
    {
        if (可见) 隐藏();
        else 显示();
    }

    public static void 显示()
    {
        确保创建();
        if (_单例 == null) return;
        _单例.按内容定尺寸();   // 每次打开都校准一次（字体/DPI 变化也稳）
        _单例.确保在屏内();
        _单例.Show();
        _单例.保存状态();       // 记 enabled=true（下次启动按此自动恢复）
        GD.Print("[NetSpeed] 气泡已显示");
    }

    /// <summary>启动恢复：上次开着（enabled=true）且工具栏入口还在 → 自动显示；否则保持隐藏。</summary>
    public static void 启动恢复()
    {
        try
        {
            var 路径 = 配置文件;
            if (!File.Exists(路径)) return;
            using var 文档 = JsonDocument.Parse(File.ReadAllText(路径));
            if (!文档.RootElement.TryGetProperty("enabled", out var e) || !e.GetBoolean()) return;
            if (!ToolBar.动作存在("netspeed"))
            {
                GD.Print("[NetSpeed] 上次开着，但工具栏 mod 不在（已删除/已禁用）→ 不自动显示");
                return;
            }
            显示();
            GD.Print("[NetSpeed] 启动恢复：按上次状态自动显示");
        }
        catch (Exception ex) { GD.PrintErr($"[NetSpeed] 启动恢复失败: {ex.Message}"); }
    }

    public static void 隐藏()
    {
        if (_单例 == null) return;
        _单例.Hide();
        _单例.保存状态();   // Visible=false → enabled=false（下次启动不再自动开）
        GD.Print("[NetSpeed] 气泡已隐藏");
    }

    private static void 确保创建()
    {
        if (_单例 != null) return;
        try
        {
            var 气泡 = new NetSpeedBubble { Name = "NetSpeedBubble" };
            ((SceneTree)Engine.GetMainLoop()).Root.AddChild(气泡);
        }
        catch (Exception e) { GD.PrintErr($"[NetSpeed] 创建失败: {e.Message}"); }
    }

    // ================= 生命周期 =================

    public override void _Ready()
    {
        _单例 = this;
        Title = "网速";
        Visible = false;
        Borderless = true;
        Transparent = true;
        AlwaysOnTop = true;
        Unresizable = true;
        Unfocusable = true;                 // 气泡是"看"的，别抢焦点
        MinSize = new Vector2I(宽, 高);
        Size = new Vector2I(宽, 高);
        CloseRequested += 隐藏;
        构建界面();
        读位置();
        GD.Print("[NetSpeed] 就绪（桌面气泡）");
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _计时 += delta;
        if (_计时 < 采样间隔秒) return;
        采样一次(_计时);
        _计时 = 0;
    }

    private void 采样一次(double 间隔)
    {
        var (收, 发) = 采样();
        if (_首采样) { _上次收 = 收; _上次发 = 发; _首采样 = false; return; }

        var 下行 = Math.Max(0, (收 - _上次收) / 间隔);
        var 上行 = Math.Max(0, (发 - _上次发) / 间隔);
        _上次收 = 收; _上次发 = 发;

        刷新显示(下行, 上行);
    }

    private void 刷新显示(double 下行, double 上行)
    {
        探针_下行速度 = 下行;
        探针_上行速度 = 上行;
        if (_下行 != null) _下行.Text = $"↓{格式化(下行)}";
        if (_上行 != null) _上行.Text = $"↑{格式化(上行)}";
        _历史[_历史下一位] = 下行 + 上行;
        _历史下一位 = (_历史下一位 + 1) % 采样数;   // 历史保留（探针断言 + 以后要画曲线可用）
    }

    // ================= 采样与格式化（纯函数，探针可断言） =================

    /// <summary>读本机网卡累计收发字节（纯本地，不联网）。没网卡/读不到 → 返回 0，不炸。</summary>
    public static (long 收, long 发) 采样()
    {
        long 收 = 0, 发 = 0;
        try
        {
            foreach (var 网卡 in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (网卡.OperationalStatus != OperationalStatus.Up) continue;
                if (网卡.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var 统计 = 网卡.GetIPv4Statistics();
                收 += 统计.BytesReceived;
                发 += 统计.BytesSent;
            }
        }
        catch { /* 忽略：拿不到就显示 0 */ }
        return (收, 发);
    }

    /// <summary>字节/秒 → 人类可读文本（纯函数）。</summary>
    public static string 格式化(double 字节每秒)
    {
        var v = double.IsNaN(字节每秒) || 字节每秒 < 0 ? 0 : 字节每秒;
        if (v < 1000) return $"{v:0}B/s";                                  // 1000 以下不留小数（省宽度）
        if (v < 1000 * 1000) return $"{v / 1000:0}KB/s";
        if (v < 1000d * 1000 * 1000) return $"{v / 1000000:0.0}MB/s";
        return $"{v / 1000000000:0.0}GB/s";
    }

    // ================= 交互：左键拖、右键关 =================

    public override void _Input(InputEvent @event)
    {
        if (!Visible) return;
        switch (@event)
        {
            case InputEventMouseButton { Pressed: true } 按下 when 按下.ButtonIndex == MouseButton.Left:
                _拖动中 = true;
                _拖动起点差 = DisplayServer.MouseGetPosition() - Position;
                break;
            case InputEventMouseButton { Pressed: false } 松开 when 松开.ButtonIndex == MouseButton.Left:
                if (_拖动中) { _拖动中 = false; 保存状态(); }
                break;
            case InputEventMouseButton { Pressed: true } 右键 when 右键.ButtonIndex == MouseButton.Right:
                隐藏();
                break;
            case InputEventMouseMotion 移动 when _拖动中:
                Position = DisplayServer.MouseGetPosition() - _拖动起点差;
                break;
        }
    }

    // ================= 位置持久化 =================

    /// <summary>保证气泡在可见屏幕区内：偏出去就夹回来（换显示器/分辨率变了也不会丢在屏外）。</summary>
    private void 确保在屏内()
    {
        var 屏 = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
        if (屏.Size.X <= 0 || 屏.Size.Y <= 0) return;   // headless/无屏幕信息：别瞎动
        var 位 = Position;   // ★ 气泡自己的位置（DisplayServer.WindowGetPosition() 读的是当前窗口=桌宠，别用）
        var 目标X = Math.Clamp(位.X, 屏.Position.X, Math.Max(屏.Position.X, 屏.End.X - 宽));
        var 目标Y = Math.Clamp(位.Y, 屏.Position.Y, Math.Max(屏.Position.Y, 屏.End.Y - 高));
        if (目标X != 位.X || 目标Y != 位.Y) Position = new Vector2I(目标X, 目标Y);
    }

    /// <summary>默认位置：屏幕右上角（留 16px 边距）。</summary>
    private void 摆到默认位()
    {
        var 屏 = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
        if (屏.Size.X <= 0 || 屏.Size.Y <= 0) return;   // headless：不动
        Position = new Vector2I(屏.End.X - 宽 - 16, 屏.Position.Y + 16);
    }

    private void 读位置()
    {
        try
        {
            if (!File.Exists(配置文件)) { 摆到默认位(); return; }
            using var 文档 = JsonDocument.Parse(File.ReadAllText(配置文件));
            var 根 = 文档.RootElement;
            if (根.TryGetProperty("x", out var x) && 根.TryGetProperty("y", out var y) && x.TryGetInt32(out var xv) && y.TryGetInt32(out var yv))
                Position = new Vector2I(xv, yv);
            else 摆到默认位();
        }
        catch { 摆到默认位(); }
    }

    /// <summary>保存位置与开关状态（enabled 供下次启动恢复）。</summary>
    private void 保存状态()
    {
        try
        {
            File.WriteAllText(配置文件,
                JsonSerializer.Serialize(new { x = Position.X, y = Position.Y, enabled = Visible }));
        }
        catch (Exception e) { GD.PrintErr($"[NetSpeed] 保存状态失败: {e.Message}"); }
    }

    // ================= UI =================

    private void 构建界面()
    {
        var 边距像素 = 边距;   // 静态字段（可配置）与下面局部控件重名过一次 → 控件改名 边距框
        var 边距框 = new MarginContainer { Name = "Margin" };
        边距框.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        foreach (var 名 in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) 边距框.AddThemeConstantOverride(名, Math.Min(边距像素, 3));  // 只留一点给阴影；留白主要靠上面的 stylebox 内容边距控制
        AddChild(边距框);

        var 胶囊 = new PanelContainer();
        // **气泡专用紧凑样式**：不能复用 MicaTheme.面板（那是给窗口用的，内容边距 14/13 → 套在气泡上就是"留白太多"，
        // 主人实机指出）。这里内容边距压到 7/3，圆角也更小 —— 胶囊紧贴文字。
        var 底 = new StyleBoxFlat
        {
            BgColor = new Color(1, 1, 1, 透明度),
            BorderColor = new Color(0, 0, 0, 0.07f),
            BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            CornerRadiusTopLeft = 9, CornerRadiusTopRight = 9,
            CornerRadiusBottomLeft = 9, CornerRadiusBottomRight = 9,
            ContentMarginLeft = 7, ContentMarginRight = 7,
            ContentMarginTop = 3, ContentMarginBottom = 3,
            ShadowColor = new Color(0.10f, 0.18f, 0.34f, 0.16f),
            ShadowSize = 5, ShadowOffset = new Vector2(0, 2),
        };
        胶囊.AddThemeStyleboxOverride("panel", 底);
        边距框.AddChild(胶囊);

        // 单行：↓ 与 ↑ 并排（紧凑气泡，不占桌面）
        var 行 = new HBoxContainer { Name = "Line" };
        行.AddThemeConstantOverride("separation", 8);
        胶囊.AddChild(行);

        _下行 = new Label { Text = "↓——" };
        MicaTheme.应用(_下行, 字号);
        行.AddChild(_下行);

        _上行 = new Label { Text = "↑——" };
        MicaTheme.应用(_上行, 字号);
        _上行.AddThemeColorOverride("font_color", MicaTheme.次文字);
        行.AddChild(_上行);

        // 尺寸跟着内容走：**必须**——窗口比内容小就会把圆角边缘裁掉（主人实机发现「不是一个完整的气泡」）
        CallDeferred(nameof(按内容定尺寸));
    }

    /// <summary>按内容最小尺寸校准窗口大小（多留 2px 给描边与抗锯齿，避免圆角被裁）。</summary>
    public void 按内容定尺寸()
    {
        try
        {
            var 内容 = GetChildOrNull<MarginContainer>(0)?.GetCombinedMinimumSize() ?? Vector2.Zero;
            if (内容.X <= 0 || 内容.Y <= 0) return;
            var 目标 = new Vector2I(Mathf.CeilToInt(内容.X) + 2, Mathf.CeilToInt(内容.Y) + 2);
            MinSize = 目标;
            Size = 目标;
        }
        catch { /* 忽略：保持默认尺寸 */ }
    }

    /// <summary>按时间顺序返回历史样本（探针与曲线共用）。</summary>
    public double[] 历史快照()
    {
        var 结果 = new double[采样数];
        for (var i = 0; i < 采样数; i++) 结果[i] = _历史[(_历史下一位 + i) % 采样数];
        return 结果;
    }

    // ================= 探针专用 =================

    /// <summary>探针：直接注入一次采样结果（不依赖真实网卡），走「差值 → 文本 → 历史」完整链路。</summary>
    public void 探针_注入采样(long 收, long 发, double 间隔秒 = 1.0)
    {
        if (_首采样) { _上次收 = 收; _上次发 = 发; _首采样 = false; 刷新显示(0, 0); return; }
        var 下行 = Math.Max(0, (收 - _上次收) / 间隔秒);
        var 上行 = Math.Max(0, (发 - _上次发) / 间隔秒);
        _上次收 = 收; _上次发 = 发;
        刷新显示(下行, 上行);
    }

    public string 探针_下行文本 => _下行?.Text ?? "";
    public string 探针_上行文本 => _上行?.Text ?? "";
}
