using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using desktop.script.Audio;
using desktop.script.logic;
using desktop.script.State;
using desktop.script.Util;
using Godot;

namespace desktop.script.UX;

/// <summary>
/// 配置窗（**独立窗口**，云母风格）——四个选项卡：
///   ① **常规**：语言（下拉，选项取 change-language 模组）+ 目录（配置/模组/存储/数据，点击打开）；
///   ② **行为**：状态机与主动行为的全部开关（启停/预算/问候/磁盘/久坐/贴边/环境感知）；
///   ③ **语音**：TTS 开关与音色（默认关；换引擎方向见 PLAN）；
///   ④ **高级**：Agent 后端 / 激进模式 / 桌宠缩放。
/// <para>
/// **写入方式**：`ConfigEdit` 只改目标键、保留 `_comment` 与未知键。行为/语音改完**立即生效**
/// （改完当场调 `设置.加载()` / `Tts.载入配置()`）；缩放与 Agent 后端需要**重启生效**（界面会提示）。
/// </para>
/// <para>**尺寸**：固定尺寸 + 内容滚动（**不做自适应高度**）；尺寸可在 `config/panel.json` 用
/// `配置窗宽` / `配置窗高` 覆盖（缺省 500×375，高:宽 = 3:4），改完重启即可，不必重新编译。</para>
/// </summary>
public partial class SettingsWindow : Window
{
    private const int 最小宽 = 470;
    private const int 语言下拉最大高 = 240;

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

    private PanelContainer _根面板;
    private VBoxContainer _列;
    private PanelContainer _标题底;
    private TabContainer _页签;
    private OptionButton _语言框;
    private readonly List<string> _语言代码 = new();
    private readonly List<Button> _路径按钮 = new();
    private readonly List<Func<string>> 目录取路径 = new();
    private readonly List<string> _行为键 = new();
    private readonly List<string> _语音键 = new();

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
        CloseRequested += 隐藏;
        BuildUi();
        应用固定尺寸();
        GD.Print($"[SettingsWindow] 就绪: 尺寸={Size} 页签={页签数}");
    }

    // ================= UI 构建 =================

    private void BuildUi()
    {
        _根面板 = new PanelContainer { Name = "Root" };
        _根面板.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _根面板.AddThemeStyleboxOverride("panel", MicaTheme.面板(14));
        AddChild(_根面板);

        var 列 = new VBoxContainer { Name = "VBox" };
        列.AddThemeConstantOverride("separation", 7);
        _列 = 列;
        _根面板.AddChild(列);

        // —— 标题行（无边框窗口没有系统标题栏 → 关闭必须自绘） ——
        var 标题底 = new PanelContainer { Name = "Header" };
        _标题底 = 标题底;
        标题底.AddThemeStyleboxOverride("panel", MicaTheme.信息底());
        标题底.GuiInput += 处理标题输入;
        列.AddChild(标题底);
        var 标题行 = new HBoxContainer();
        标题行.AddThemeConstantOverride("separation", 8);
        标题底.AddChild(标题行);
        var 标题 = new Label { Text = Tr("config"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        MicaTheme.应用(标题, 15);
        标题行.AddChild(标题);
        var 副标题 = new Label { Text = "设置" };
        MicaTheme.应用(副标题, 11, 次要: true);
        标题行.AddChild(副标题);
        var 关闭 = new Button { Text = "×", TooltipText = Tr("close"), CustomMinimumSize = new Vector2(26, 24) };
        MicaTheme.应用(关闭, 16, 扁平: true);
        关闭.Pressed += 隐藏;
        标题行.AddChild(关闭);

        var 分隔 = new HSeparator();
        分隔.AddThemeStyleboxOverride("separator", MicaTheme.分隔线());
        列.AddChild(分隔);

        // —— 选项卡（外套滚动容器：内容比窗口高时滚动，绝不裁切） ——
        var 滚动 = new ScrollContainer
        {
            Name = "Scroll",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            FollowFocus = true,
        };
        列.AddChild(滚动);

        _页签 = new TabContainer { Name = "Tabs", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        MicaTheme.应用(_页签);
        滚动.AddChild(_页签);
        AddTab(构建常规页(), "常规");
        AddTab(构建行为页(), "行为");
        AddTab(构建语音页(), "语音");
        AddTab(构建高级页(), "高级");

        刷新();
    }

    private void AddTab(Control 页, string 标题)
    {
        _页签.AddChild(页);
        _页签.SetTabTitle(_页签.GetTabCount() - 1, 标题);
    }

    // ================= ① 常规页 =================

    private Control 构建常规页()
    {
        var 页 = 新页("TabGeneral");

        _语言框 = new OptionButton
        {
            Name = "LanguageSelector",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 28),
            ClipText = true,
        };
        MicaTheme.应用(_语言框);
        var 语言弹窗 = _语言框.GetPopup();
        语言弹窗.MaxSize = new Vector2I(0, 语言下拉最大高);
        MicaTheme.应用弹窗(语言弹窗);
        内部左对齐(_语言框);
        _语言框.AddThemeIconOverride("arrow", 深色箭头());
        _语言框.ItemSelected += 序号 => 切换语言((int)序号);
        页.AddChild(行(Tr("language"), _语言框));

        foreach (var (标签, 取路径) in 目录项())
        {
            var 按钮 = new Button
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 28),
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            };
            MicaTheme.应用(按钮, 11, 扁平: true, 对齐: true);
            按钮.AddThemeStyleboxOverride("normal", MicaTheme.控件());
            按钮.AddThemeStyleboxOverride("hover", MicaTheme.控件(聚焦: true));
            按钮.AddThemeStyleboxOverride("pressed", MicaTheme.控件(聚焦: true));
            按钮.Pressed += () => 打开目录(按钮.TooltipText.Split('\n')[0]);
            _路径按钮.Add(按钮);
            目录取路径.Add(取路径);
            页.AddChild(行(标签, 按钮));
        }
        return 页;
    }

    private List<(string 标签, Func<string> 取路径)> 目录项() => new()
    {
        (Tr("config"), () => 全局路径("config", () => ProjectSettings.GlobalizePath("res://config"))),
        (Tr("mods"), () => 全局路径("mod", () => LoadUtil.ModPath)),
        (Tr("save"), () => 全局路径("save", () => LoadUtil.GetOutputDir())),
        (Tr("data"), () => ProjectSettings.GlobalizePath("user://")),
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

    // ================= ② 行为页（状态机 / 主动行为全开关） =================

    private Control 构建行为页()
    {
        var 页 = 新页("TabBehavior");

        页.AddChild(数字行("每小时主动上限", StateMachine.设置.每小时主动上限, 0, 200, true, 键 => 行为("每小时主动上限", (int)键), _行为键, "每小时主动上限",
            "走动/问候/提醒共享的滑动 1 小时预算（0 = 只关主动行为）"));

        页.AddChild(分区("问候与提醒"));
        页.AddChild(开关行("磁盘提醒启用", StateMachine.设置.磁盘提醒启用, 键 => 行为("磁盘提醒启用", 键), _行为键, "磁盘提醒启用", "只提醒「自己不易察觉的事」——磁盘悄悄变满"));
        页.AddChild(数字行("磁盘剩余下限", StateMachine.设置.磁盘剩余下限GB, 1, 1000, true, 键 => 行为("磁盘剩余下限GB", (int)键), _行为键, "磁盘剩余下限GB",
            "单位 GB；余量低于它才提醒（每天一次）"));
        页.AddChild(数字行("久坐提醒", StateMachine.设置.久坐提醒分钟, 0, 600, true, 键 => 行为("久坐提醒分钟", (int)键), _行为键, "久坐提醒分钟",
            "分钟；连续活跃到点提醒休息（0 = 关）"));
        页.AddChild(数字行("久坐冷却", StateMachine.设置.久坐提醒冷却分钟, 0, 600, true, 键 => 行为("久坐提醒冷却分钟", (int)键), _行为键, "久坐提醒冷却分钟",
            "分钟；两次提醒的最小间隔"));

        页.AddChild(分区("状态与感知"));
        页.AddChild(开关行("环境感知启用", StateMachine.设置.环境感知启用, 键 => 行为("环境感知启用", 键), _行为键, "环境感知启用", "默认关：开了才读「空闲时长 + 是否全屏」，只在本机用"));
        页.AddChild(开关行("全屏静默", StateMachine.设置.全屏静默, 键 => 行为("全屏静默", 键), _行为键, "全屏静默", "全屏（游戏/视频/演示）时完全不主动打扰"));
        return 页;
    }

    // ================= ③ 语音页 =================

    private Control 构建语音页()
    {
        var 页 = 新页("TabVoice");
        页.AddChild(开关行("语音启用", Tts.启用, 键 => 语音("启用", 键), _语音键, "启用", "默认关 —— 桌宠冒泡时才念（聊天面板长回复永不念）"));
        页.AddChild(下拉行("引擎", new[] { "edge", "sapi" }, Tts.引擎, 值 => 语音("引擎", 值), _语音键, "引擎",
            "edge = 在线（自然，但偏机械；需要 tools/install_edge_tts.ps1 装一次）｜sapi = Windows 自带（离线、机械）"));
        页.AddChild(下拉行("edge 音色", new[] { "zh-CN-XiaoxiaoNeural", "zh-CN-XiaoyiNeural", "zh-CN-YunxiNeural", "zh-CN-YunyangNeural" },
            Tts.Edge语音, 值 => 语音("edge语音", 值), _语音键, "edge语音", "晓晓 / 晓伊 / 云希(男) / 云扬(男)"));
        页.AddChild(数字行("语速", Tts.语速, -10, 10, true, 键 => 语音("语速", (int)键), _语音键, "语速", "−10 ~ 10"));
        页.AddChild(数字行("音量", Tts.音量, 0, 100, true, 键 => 语音("音量", (int)键), _语音键, "音量", "0 ~ 100"));
        页.AddChild(数字行("最大字数", Tts.最大字数, 0, 500, true, 键 => 语音("最大字数", (int)键), _语音键, "最大字数", "超过就不念（0 = 不限）"));
        return 页;
    }

    // ================= ④ 高级页 =================

    private Control 构建高级页()
    {
        var 页 = 新页("TabAdvanced");
        var 后端 = ConfigEdit.读文本("config/agent.json", "backend", "hermes-acp");
        var 激进 = string.Equals(ConfigEdit.读文本("config/agent.json", "aggressiveMode", "false"), "true", StringComparison.OrdinalIgnoreCase);
        var 缩放 = double.TryParse(ConfigEdit.读文本("config/pet.json", "缩放", "0.5"), out var z) ? z : 0.5;

        页.AddChild(下拉行("Agent 后端", new[] { "hermes-acp", "none" }, 后端,
            值 => 高级("config/agent.json", "backend", 值, "需重启生效"), null, null,
            "hermes-acp = 走 ACP 接 Agent｜none = 无 Agent（本地模式，桌宠照常）"));
        页.AddChild(开关行("激进指令模式", 激进, 键 => 高级("config/agent.json", "aggressiveMode", 键, "需重启生效"), null, null,
            "实验性：放宽指令白名单（当前只加 open_url）。默认关"));
        页.AddChild(数字行("桌宠缩放", 缩放, 0.2, 1.5, false,
            键 => 高级("config/pet.json", "缩放", Math.Round(键, 2), "需重启生效"), null, null,
            "0.5 = 对齐 VPet 官方内部 ZoomRatio；动画按这个标准制作，改它会让贴边/偏移失配"));
        return 页;
    }

    // ================= 行构件 =================

    private VBoxContainer 新页(string 名)
    {
        var 页 = new VBoxContainer { Name = 名, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        页.AddThemeConstantOverride("separation", 7);
        return 页;
    }

    private static Label 分区(string 文本)
    {
        var l = new Label { Text = 文本 };
        MicaTheme.应用(l, 11, 次要: true);
        return l;
    }

    /// <summary>「标签 + 控件」一行：标签固定宽度，右侧控件纵向对齐。</summary>
    private static HBoxContainer 行(string 标签, Control 控件, string 提示 = "")
    {
        var 行 = new HBoxContainer();
        行.AddThemeConstantOverride("separation", 8);
        var 标签节点 = new Label
        {
            Text = 标签,
            CustomMinimumSize = new Vector2(104, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        MicaTheme.应用(标签节点, 12, 次要: true);
        行.AddChild(标签节点);
        行.AddChild(控件);
        if (!string.IsNullOrEmpty(提示))
        {
            var 提示节点 = new Label
            {
                Text = 提示,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            MicaTheme.应用(提示节点, 10, 次要: true);
            行.AddChild(提示节点);
        }
        return 行;
    }

    /// <summary>开关行：标签 + 「开/关」按钮（ToggleMode）。键会登记进 清单（探针回归：保证开关不丢）。</summary>
    private static HBoxContainer 开关行(string 标签, bool 初值, Action<bool> 改动, List<string> 登记, string 键, string 提示 = "")
    {
        var 钮 = new Button { ToggleMode = true, CustomMinimumSize = new Vector2(58, 26), ButtonPressed = 初值 };
        MicaTheme.应用开关(钮, 初值);
        钮.Toggled += 状态 => { MicaTheme.应用开关(钮, 状态); 改动(状态); };
        if (登记 != null && 键 != null) 登记.Add(键);
        return 行(标签, 钮, 提示);
    }

    /// <summary>数字行：标签 + 输入框（回车 / 失焦提交，越界夹取，未变不写盘）。</summary>
    private static HBoxContainer 数字行(string 标签, double 初值, double 最小, double 最大, bool 取整,
        Action<double> 提交, List<string> 登记, string 键, string 提示 = "")
    {
        var 框 = new LineEdit
        {
            Text = 取整 ? ((int)初值).ToString() : 初值.ToString("0.##"),
            CustomMinimumSize = new Vector2(70, 26),
            Alignment = HorizontalAlignment.Right,
            TooltipText = $"范围 {最小:0.##} ~ {最大:0.##}（回车确认）",
        };
        MicaTheme.应用(框, 12);
        var 上次 = 初值;
        void 提交文本()
        {
            if (!double.TryParse(框.Text.Trim(), out var 值)) { 框.Text = 取整 ? ((int)上次).ToString() : 上次.ToString("0.##"); return; }
            var 夹 = Math.Clamp(值, 最小, 最大);
            框.Text = 取整 ? ((int)夹).ToString() : 夹.ToString("0.##");
            if (Math.Abs(夹 - 上次) < 0.0001) return;   // 没变就不写盘
            上次 = 夹;
            提交(夹);
        }
        框.TextSubmitted += _ => 提交文本();
        框.FocusExited += 提交文本;
        if (登记 != null && 键 != null) 登记.Add(键);
        return 行(标签, 框, 提示);
    }

    /// <summary>下拉行：标签 + OptionButton（云母样式 + 深色箭头 + 左对齐）。</summary>
    private HBoxContainer 下拉行(string 标签, string[] 选项, string 当前, Action<string> 改动, List<string> 登记, string 键, string 提示 = "")
    {
        var 框 = new OptionButton
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 26),
            ClipText = true,
        };
        MicaTheme.应用(框);
        MicaTheme.应用弹窗(框.GetPopup());
        内部左对齐(框);
        框.AddThemeIconOverride("arrow", 深色箭头());
        foreach (var 项 in 选项) 框.AddItem(项);
        var 序号 = Array.IndexOf(选项, 当前);
        框.Selected = 序号 >= 0 ? 序号 : 0;
        框.ItemSelected += i => 改动(选项[(int)i]);
        if (登记 != null && 键 != null) 登记.Add(键);
        return 行(标签, 框, 提示);
    }

    // ================= 写配置 =================

    private void 行为(string 键, object 值)
    {
        if (!ConfigEdit.写("config/behavior.json", 键, 值)) return;
        StateMachine.设置.加载();   // 立即生效（含注入 DailyRoutine / EdgeHide / EnvironmentSense）
        GD.Print($"[SettingsWindow] 行为 · {键} = {值}（已生效）");
    }

    private void 语音(string 键, object 值)
    {
        if (!ConfigEdit.写("config/tts.json", 键, 值)) return;
        Tts.载入配置();             // 立即生效
        GD.Print($"[SettingsWindow] 语音 · {键} = {值}（已生效）");
    }

    private void 高级(string 配置路径, string 键, object 值, string 提示)
    {
        if (!ConfigEdit.写(配置路径, 键, 值)) return;
        GD.Print($"[SettingsWindow] 高级 · {配置路径} · {键} = {值}（{提示}）");
        Dialogue.显示临时标题(提示);
    }

    // ================= 工具 =================

    /// <summary>Godot 默认下拉箭头是白色图标，浅色磨砂面板上不可见 → 运行时生成细「v」形深色箭头。</summary>
    private static Texture2D 深色箭头()
    {
        const int 宽 = 9;
        const int 高 = 5;
        var 图 = Image.CreateEmpty(宽, 高, false, Image.Format.Rgba8);
        图.Fill(new Color(0, 0, 0, 0));
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

    /// <summary>OptionButton 内部是 HBox（文字 + 箭头），默认居中 → 改为左对齐（贴近系统下拉框观感）。</summary>
    private static void 内部左对齐(OptionButton 框)
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

    // ================= 尺寸（固定尺寸 + 滚动 + 面板配置覆盖；主人 2026-09-19） =================

    /// <summary>
    /// **固定尺寸 + 内容滚动**（不做自适应高度）。历史教训记在这里：
    /// ① 曾把高度写死 224px 又没滚动 → 加了「数据」目录行后底部被裁（「只有一截」）；
    /// ② 改成按内容自适应后，TabContainer 的最小尺寸**只统计当前可见页** → 切到行为页窗口没跟着变、照样被裁；
    /// ③ 直接量页签容器又会碰到「切页瞬间量到垃圾值」（实测 3671px）。
    /// 结论：**别猜高度** —— 固定一个比例舒服的尺寸，超出就让 `Scroll` 容器滚。
    /// 比例：**高:宽 = 3:4**（500 宽 → 375 高）。
    /// <para>
    /// **改尺寸不用改代码**：`config/panel.json` 写 `"配置窗宽": 500, "配置窗高": 665`（缺省 500×375）。
    /// 改这里的常量必须重新编译才生效 —— 若「改了数字窗口没变化」，先看启动日志那行
    /// `[SettingsWindow] 固定尺寸: (宽, 高)`：数值没跟着变 = 没编译进去（编辑器内 ▶ 会自动编；
    /// 构建失败时 Godot 会继续跑旧 DLL，输出窗口有 error CS）。
    /// </para>
    /// </summary>
    private const int 默认宽 = 500;
    private const int 默认高 = 默认宽 * 9 / 16;   // 高:宽 = 3:4

    /// <summary>读 panel.json 里的一个正整数键（优先；缺失/坏值 → 缺省）。</summary>
    private static int 读面板尺寸(string 键, int 缺省)
    {
        foreach (var 路径 in Util.ConfigFile.候选("panel.json").Concat(Util.ConfigFile.候选("config/panel.json")))
        {
            try
            {
                if (!File.Exists(路径)) continue;
                var 文本 = File.ReadAllText(路径);
                if (string.IsNullOrWhiteSpace(文本)) continue;
                using var 文档 = JsonDocument.Parse(文本);
                if (文档.RootElement.TryGetProperty(键, out var 值) && 值.TryGetInt32(out var 数) && 数 > 0) return 数;
            }
            catch (Exception e) { GD.PrintErr($"[SettingsWindow] 读 {路径} 失败: {e.Message}"); }
        }
        return 缺省;
    }

    /// <summary>期望尺寸：`config/panel.json` 的 配置窗宽/配置窗高 优先，缺省 500×375。</summary>
    public static Vector2I 期望尺寸()
    {
        var 宽 = Mathf.Clamp(读面板尺寸("配置窗宽", 默认宽), 320, 1600);
        var 高 = Mathf.Clamp(读面板尺寸("配置窗高", 默认高), 240, 2400);
        return new Vector2I(宽, 高);
    }

    public void 应用固定尺寸()
    {
        var 目标 = 期望尺寸();
        if (Size != 目标)
        {
            Size = 目标;
            GD.Print($"[SettingsWindow] 固定尺寸: {Size}（可在 config/panel.json 写 配置窗宽/配置窗高 调整）");
        }
    }

    /// <summary>探针：是否存在滚动容器（回归：「内容比窗口高时必须能滚」）。</summary>
    public static bool 有滚动 => _单例?._根面板?.GetNodeOrNull("VBox/Scroll") != null;

    /// <summary>探针：切到指定页签（比让探针自己爬节点路径稳）。尺寸固定，切页不用重算。</summary>
    public static void 探针_切页(int 序号)
    {
        if (_单例?._页签 == null) return;
        if (序号 < 0 || 序号 >= _单例._页签.GetTabCount()) return;
        _单例._页签.CurrentTab = 序号;
        GD.Print($"[SettingsWindow] 切到页签 {序号}：{_单例._页签.GetTabTitle(序号)}");
    }

    // ================= 数据刷新 =================

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
                foreach (var 行文本 in 文件.GetAsText().Split('\n'))
                {
                    var 匹配 = Regex.Match(行文本, @"^\s*-\s*(.+?)\s*\[#lang=([A-Za-z_]+)\]\s*$");
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

    private void 打开目录(string 路径)
    {
        if (string.IsNullOrWhiteSpace(路径)) return;
        if (!Directory.Exists(路径))
        {
            GD.PrintErr($"[SettingsWindow] 目录不存在: {路径}");
            Dialogue.显示临时标题($"目录不存在：{路径}");
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
        var 尺寸 = 期望尺寸();
        _单例.应用固定尺寸();
        // 显式传尺寸：无参 PopupCentered() 的语义是「按窗口内容的最小尺寸弹出」，会盖掉我们设的固定尺寸
        _单例.PopupCentered(尺寸);
        GD.Print($"[SettingsWindow] 已弹出: visible={_单例.Visible}, pos={_单例.Position}, size={_单例.Size}, 内容最小={_单例.GetContentsMinimumSize()}");
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
        : 枚举下拉(_单例._语言框);

    public static IReadOnlyList<string> 路径文本 => _单例 == null
        ? Array.Empty<string>()
        : _单例._路径按钮.Select(b => b.Text).ToArray();

    /// <summary>探针：选项卡数量与标题（「别忘了新增选项卡」的回归锁）。</summary>
    public static int 页签数 => _单例?._页签?.GetTabCount() ?? 0;
    public static IReadOnlyList<string> 页签名
    {
        get
        {
            var 结果 = new List<string>();
            if (_单例?._页签 == null) return 结果;
            for (var i = 0; i < _单例._页签.GetTabCount(); i++) 结果.Add(_单例._页签.GetTabTitle(i));
            return 结果;
        }
    }

    /// <summary>探针：窗口高 / 内容高（回归：窗口必须装得下内容，否则「只有一截」）。</summary>
    public static int 窗口高 => _单例?.Size.Y ?? 0;
    public static int 内容高 => _单例?._根面板 == null ? 0 : (int)MathF.Ceiling(_单例._根面板.GetCombinedMinimumSize().Y);

    /// <summary>探针：行为页 / 语音页登记过的配置键（保证「功能开关都塞进配置」）。</summary>
    public static IReadOnlyList<string> 行为键 => _单例?._行为键 ?? (IReadOnlyList<string>)Array.Empty<string>();
    public static IReadOnlyList<string> 语音键 => _单例?._语音键 ?? (IReadOnlyList<string>)Array.Empty<string>();

    private static List<string> 枚举下拉(OptionButton 框)
    {
        var 结果 = new List<string>();
        for (var i = 0; i < 框.ItemCount; i++) 结果.Add(框.GetItemText(i));
        return 结果;
    }
}
