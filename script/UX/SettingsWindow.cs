using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using desktop.script.logic;
using desktop.script.Util;
using Godot;

namespace desktop.script.UX;

/// <summary>
/// 配置窗（**独立窗口**，与工具栏同型）：
///   ① 语言：下拉框（选项取自 change-language 模组，改语言即调 IO.ChangeLang）；
///   ② 目录：配置 / 模组 / 存储，显示实际路径，点击用系统默认程序打开文件夹。
/// 视觉：云母（Mica）浅色磨砂，规范见 MicaTheme。
/// 无边框窗口的关闭沿用工具栏范式（`CloseRequested` + 系统标题栏），内容区不再重复放 ×。
/// 约定：标识符英文，注释中文（见 AGENTS.md §8）。
/// </summary>
public partial class SettingsWindow : Window
{
    private const int 面板宽 = 470;
    private const int 面板高 = 224;

    /// <summary>内置兜底语言清单（与 change-language 模组的 option.dialogue 保持一致）。</summary>
    private static readonly (string 标签, string 代码)[] 默认语言 =
    {
        ("简体中文", "zh_CN"), ("繁體中文", "zh_TW"), ("English", "en"), ("Русский", "ru"),
        ("日本語", "ja"), ("한국어", "ko"), ("Deutsch", "de"), ("Français", "fr"),
        ("Español", "es"), ("Italiano", "it"), ("Português", "pt"), ("Tiếng Việt", "vi"),
        ("ภาษาไทย", "th"),
    };

    private const string 语言模组对话 = "res://mods/main_command/command/change-language/option.dialogue";

    private static SettingsWindow _单例;

    private OptionButton _语言框;
    private readonly List<string> _语言代码 = new();
    private readonly List<Button> _路径按钮 = new();

    public static bool 存在 => _单例 != null;
    public static bool 可见 => _单例 is { Visible: true };

    /// <summary>配置窗位于鼠标下方时优先接管指针，避免底层桌宠误入拖拽（同 ChatBox / ToolBar）。</summary>
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
        Title = Tr("config");
        Visible = false;
        Borderless = true;
        Transparent = true;
        Unresizable = true;
        MinSize = new Vector2I(360, 180);
        Size = new Vector2I(面板宽, 面板高);
        CloseRequested += 隐藏;
        BuildUi();
        GD.Print($"[SettingsWindow] 就绪: 尺寸={Size}");
    }

    // ================= UI 构建 =================

    private void BuildUi()
    {
        var 根面板 = new PanelContainer { Name = "Root" };
        根面板.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        根面板.AddThemeStyleboxOverride("panel", MicaTheme.面板(14));
        AddChild(根面板);

        var 列 = new VBoxContainer { Name = "VBox" };
        列.AddThemeConstantOverride("separation", 7);
        根面板.AddChild(列);

        // —— 标题行（关闭靠系统标题栏，内容区不再重复放 ×） ——
        var 标题底 = new PanelContainer { Name = "Header" };
        标题底.AddThemeStyleboxOverride("panel", MicaTheme.信息底());
        标题底.GuiInput += 处理标题输入;
        列.AddChild(标题底);
        var 标题行 = new HBoxContainer();
        标题行.AddThemeConstantOverride("separation", 8);
        标题底.AddChild(标题行);
        var 标题 = new Label { Text = Tr("config"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        MicaTheme.应用(标题, 15);
        标题行.AddChild(标题);
        var 副标题 = new Label { Text = "语言与目录" };
        MicaTheme.应用(副标题, 11, 次要: true);
        标题行.AddChild(副标题);
        // 无边框窗口没有系统标题栏（实测），关闭按钮必须自绘，否则窗口关不掉。
        var 关闭 = new Button { Text = "×", TooltipText = Tr("close"), CustomMinimumSize = new Vector2(26, 24) };
        MicaTheme.应用(关闭, 16, 扁平: true);
        关闭.Pressed += 隐藏;
        标题行.AddChild(关闭);

        var 分隔 = new HSeparator();
        分隔.AddThemeStyleboxOverride("separator", MicaTheme.分隔线());
        列.AddChild(分隔);

        // —— 语言：下拉框 ——
        _语言框 = new OptionButton
        {
            Name = "LanguageSelector",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 28),
            ClipText = true,
        };
        MicaTheme.应用(_语言框);
        MicaTheme.应用弹窗(_语言框.GetPopup());
        语言框内容对齐(_语言框);
        _语言框.AddThemeIconOverride("arrow", 深色箭头());
        _语言框.ItemSelected += 序号 => 切换语言((int)序号);
        列.AddChild(路径行(Tr("language"), _语言框));

        // —— 目录：显示路径，点击打开文件夹 ——
        foreach (var (标签, 取路径) in 目录项())
        {
            var 按钮 = new Button
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 28),
                ClipText = true,
                // 长路径不要硬裁切，用省略号收尾（完整路径在 tooltip 里）
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            };
            MicaTheme.应用(按钮, 11, 扁平: true, 对齐: true);
            按钮.AddThemeStyleboxOverride("normal", MicaTheme.控件());
            按钮.AddThemeStyleboxOverride("hover", MicaTheme.控件(聚焦: true));
            按钮.AddThemeStyleboxOverride("pressed", MicaTheme.控件(聚焦: true));
            按钮.Pressed += () => 打开目录(按钮.Text);
            _路径按钮.Add(按钮);
            列.AddChild(路径行(标签, 按钮));
            // 捕获取路径函数（延迟到刷新时求值，保证 IO/ 配置已初始化）
            目录取路径.Add(取路径);
        }

        刷新();
    }

    private readonly List<Func<string>> 目录取路径 = new();

    /// <summary>目录项定义（标签取自既有 i18n 键，路径由 IO 全局表提供，缺省回退到工具函数）。
    /// 非静态：Tr 是 GodotObject 的实例方法。</summary>
    private List<(string 标签, Func<string> 取路径)> 目录项() => new()
    {
        (Tr("config"), () => 全局路径("config", () => ProjectSettings.GlobalizePath("res://config"))),
        (Tr("mods"), () => 全局路径("mod", () => LoadUtil.ModPath)),
        (Tr("save"), () => 全局路径("save", LoadUtil.GetOutputDir)),
    };

    private static string 全局路径(string 键, Func<string> 兜底)
    {
        if (IO.单例 != null && IO.单例.getG(键, out var 值))
        {
            var 路径 = 值.AsString();
            if (!string.IsNullOrWhiteSpace(路径)) return 路径;
        }
        return 兜底();
    }

    /// <summary>「标签 + 控件」的一行：标签固定宽度，保证右侧控件纵向对齐。</summary>
    private static HBoxContainer 路径行(string 标签, Control 控件)
    {
        var 行 = new HBoxContainer();
        行.AddThemeConstantOverride("separation", 8);
        var 标签节点 = new Label
        {
            Text = 标签,
            CustomMinimumSize = new Vector2(72, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        MicaTheme.应用(标签节点, 12, 次要: true);
        行.AddChild(标签节点);
        行.AddChild(控件);
        return 行;
    }

    /// <summary>Godot 默认下拉箭头是白色图标，浅色磨砂面板上不可见，故运行时生成一枚浅色细箭头（够轻，不抢标题）。</summary>
    private static Texture2D 深色箭头()
    {
        const int 宽 = 9;
        const int 高 = 5;
        var 图 = Image.CreateEmpty(宽, 高, false, Image.Format.Rgba8);
        图.Fill(new Color(0, 0, 0, 0));
        // 细「v」形（2px 描边），比实心三角更轻，贴合柔和浅色风格
        for (var y = 0; y < 高; y++)
        {
            var 左 = y;
            var 右 = 宽 - 1 - y;
            图.SetPixel(左, y, MicaTheme.次文字);
            图.SetPixel(右, y, MicaTheme.次文字);
            if (左 + 1 < 宽) 图.SetPixel(左 + 1, y, MicaTheme.次文字);
            if (右 - 1 >= 0) 图.SetPixel(右 - 1, y, MicaTheme.次文字);
        }
        return ImageTexture.CreateFromImage(图);
    }

    /// <summary>OptionButton 内部是 HBox（文字 + 箭头），默认居中；这里改为左对齐，贴近系统下拉框观感。</summary>
    private static void 语言框内容对齐(OptionButton 框)
    {
        foreach (var 子 in 框.GetChildren())
        {
            if (子 is HBoxContainer 行) 行.Alignment = BoxContainer.AlignmentMode.Begin;
            else if (子 is Label 文本) 文本.HorizontalAlignment = HorizontalAlignment.Left;
            else if (子 is Control 控件) 控件.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        }
    }

    /// <summary>无边框窗口的自绘标题区域负责拖动。</summary>
    private void 处理标题输入(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            StartDrag();
    }

    // ================= 数据刷新 =================

    /// <summary>Windows 下把正/反斜杠统一成反斜杠，路径显示更符合系统习惯（打开目录仍可用）。</summary>
    private static string 规范显示(string 路径)
    {
        if (string.IsNullOrEmpty(路径)) return "";
        return OS.GetName() == "Windows" ? 路径.Replace('/', '\\') : 路径;
    }

    /// <summary>每次弹出前刷新：目录路径取实时值，语言列表按当前语言定位。</summary>
    private void 刷新()
    {
        for (var i = 0; i < _路径按钮.Count && i < 目录取路径.Count; i++)
        {
            var 路径 = 规范显示(目录取路径[i]() ?? "");
            _路径按钮[i].Text = 路径;
            _路径按钮[i].TooltipText = $"{路径}\n点击打开该文件夹";
        }
        刷新语言列表();
    }

    private void 刷新语言列表()
    {
        if (_语言框 == null) return;
        _语言代码.Clear();
        _语言框.Clear();
        foreach (var (标签, 代码) in 读取语言清单())
        {
            _语言框.AddItem(标签);
            _语言代码.Add(代码);
        }

        var 当前 = TranslationServer.GetLocale();
        if (string.IsNullOrEmpty(当前) && IO.单例 != null)
            当前 = IO.配置.GetValue("Player", "lang", "").AsString();

        var 序号 = _语言代码.FindIndex(c => string.Equals(c, 当前, StringComparison.OrdinalIgnoreCase));
        if (序号 < 0 && !string.IsNullOrEmpty(当前))
            序号 = _语言代码.FindIndex(c => c.StartsWith(当前, StringComparison.OrdinalIgnoreCase));
        _语言框.Selected = 序号 >= 0 ? 序号 : 0;
    }

    /// <summary>语言清单以模组文件为准（mods 是可改资产）；解析不到则回退内置清单。</summary>
    private static List<(string 标签, string 代码)> 读取语言清单()
    {
        var 结果 = new List<(string, string)>();
        try
        {
            using var 文件 = Godot.FileAccess.Open(语言模组对话, Godot.FileAccess.ModeFlags.Read);
            if (文件 != null)
            {
                foreach (var 行 in 文件.GetAsText().Split('\n'))
                {
                    var 匹配 = Regex.Match(行, @"^\s*-\s*(.+?)\s*\[#lang=([A-Za-z_]+)\]\s*$");
                    if (匹配.Success) 结果.Add((匹配.Groups[1].Value.Trim(), 匹配.Groups[2].Value));
                }
            }
        }
        catch (Exception e) { GD.PrintErr($"[SettingsWindow] 解析语言清单失败: {e.Message}"); }

        if (结果.Count == 0) 结果.AddRange(默认语言.Select(t => (t.标签, t.代码)));
        return 结果;
    }

    // ================= 行为 =================

    private void 切换语言(int 序号)
    {
        if (序号 < 0 || 序号 >= _语言代码.Count) return;
        var 代码 = _语言代码[序号];
        IO.单例?.ChangeLang(代码);
        GD.Print($"[SettingsWindow] 切换语言: {代码}");
    }

    /// <summary>用系统默认程序打开目录（跨平台走 OS.ShellOpen）。</summary>
    private void 打开目录(string 路径)
    {
        if (string.IsNullOrWhiteSpace(路径)) return;
        if (!Directory.Exists(路径))
        {
            GD.PrintErr($"[SettingsWindow] 目录不存在: {路径}");
            Dialogue.显示临时标题($"目录不存在：{路径}", 3000);
            return;
        }
        var 结果 = OS.ShellOpen(路径);
        GD.Print($"[SettingsWindow] 打开目录: {路径} (err={结果})");
    }

    // ================= 显隐 =================

    public static void 显示()
    {
        if (_单例 == null) return;
        _单例.刷新();
        _单例.PopupCentered();
        GD.Print($"[SettingsWindow] 已弹出: visible={_单例.Visible}, pos={_单例.Position}, size={_单例.Size}");
    }

    public static void 隐藏()
    {
        if (_单例 == null) return;
        _单例.Hide();
    }

    public static void 切换()
    {
        if (_单例 == null) return;
        if (_单例.Visible) 隐藏(); else 显示();
    }

    // ================= 供探针读取（只读） =================

    public static IReadOnlyList<string> 语言项 => _单例?._语言框 == null
        ? Array.Empty<string>()
        : _单例._语言框.GetItemText_所有();

    public static IReadOnlyList<string> 路径文本 => _单例 == null
        ? Array.Empty<string>()
        : _单例._路径按钮.Select(b => b.Text).ToArray();
}

/// <summary>OptionButton 的只读小工具（避免探针直接依赖内部节点结构）。</summary>
internal static class OptionButtonExtensions
{
    public static IReadOnlyList<string> GetItemText_所有(this OptionButton 框)
    {
        var 结果 = new List<string>();
        for (var i = 0; i < 框.ItemCount; i++) 结果.Add(框.GetItemText(i));
        return 结果;
    }
}