using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using desktop.script.Agent;
using desktop.script.Loader;
using desktop.script.logic;
using desktop.script.State;
using Godot;

namespace desktop.script.UX;

/// <summary>
/// 聊天面板（**独立窗口**，与工具栏同型）：聊天记录 + 输入行 + 底部横排命令栏。
/// 唤出方式：右键桌宠（拖动桌宠时自动关闭）。
/// 视觉：云母（Mica）浅色磨砂，规范见 MicaTheme。
/// 不用 [Export] 中文属性绑定（Godot 不认中文属性名，会静默失败）——全代码构建。
/// 约定：标识符英文，注释中文（见 AGENTS.md §8）。
/// </summary>
public partial class ChatBox : Window
{
    private const int 面板宽 = 500;
    private const int 面板高 = 330;
    private const int 贴边间距 = 8;

    private static ChatBox _单例;

    private RichTextLabel _记录;
    private LineEdit _输入框;
    private ScrollContainer _滚动;
    private PanelContainer _根面板;
    private HBoxContainer _命令栏;
    private ScrollContainer _选项滚动;
    private GridContainer _选项栏;
    private Label _选项标题;
    private readonly List<(string 标签, Action 动作)> _选项项 = new();
    private Label _状态;

    private readonly System.Text.StringBuilder _历史 = new();
    private readonly System.Text.StringBuilder _流式缓冲 = new();
    private bool _流式中;

    public static bool 存在 => _单例 != null;

    /// <summary>面板是否可见（供状态机判断「主人正在跟我们互动」，属「不打扰」约束）。</summary>
    public static bool 可见 => _单例 is { Visible: true };

    /// <summary>
    /// 对话窗位于鼠标下方时优先接管指针，防止主桌宠窗口把这次左键误判为拖拽。
    /// 即使面板覆盖在桌宠上，也应优先操作最上层面板。
    /// </summary>
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
        Title = Soul.SoulTable.名字;
        Visible = false;
        Borderless = true;
        Transparent = true;
        Unresizable = true;
        MinSize = new Vector2I(420, 260);
        Size = new Vector2I(面板宽, 面板高);
        CloseRequested += 隐藏;
        BuildUi();
        AgentEvents.EnsureSubscribed();
    }

    // ================= UI 构建 =================

    private void BuildUi()
    {
        _根面板 = new PanelContainer { Name = "Root" };
        _根面板.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _根面板.AddThemeStyleboxOverride("panel", MicaTheme.面板());
        AddChild(_根面板);

        var 列 = new VBoxContainer { Name = "VBox" };
        列.AddThemeConstantOverride("separation", 6);
        _根面板.AddChild(列);

        // —— 标题行 ——
        var 标题底 = new PanelContainer { Name = "Header" };
        标题底.AddThemeStyleboxOverride("panel", MicaTheme.信息底());
        标题底.GuiInput += 处理标题输入;
        列.AddChild(标题底);
        var 标题行 = new HBoxContainer();
        标题行.AddThemeConstantOverride("separation", 8);
        标题底.AddChild(标题行);
        var 标题 = new Label { Text = Soul.SoulTable.名字, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        MicaTheme.应用(标题, 15);
        标题行.AddChild(标题);
        var 副标题 = new Label { Text = "陪你聊聊，也能帮你做事" };
        MicaTheme.应用(副标题, 11, 次要: true);
        标题行.AddChild(副标题);
        _状态 = new Label { Text = "● 待命" };
        MicaTheme.应用(_状态, 11, 次要: true);
        标题行.AddChild(_状态);
        var 关闭 = new Button { Text = "×", TooltipText = "关闭", CustomMinimumSize = new Vector2(26, 24) };
        MicaTheme.应用(关闭, 16, 扁平: true);
        关闭.Pressed += 隐藏;
        标题行.AddChild(关闭);

        var 上分隔 = new HSeparator();
        上分隔.AddThemeStyleboxOverride("separator", MicaTheme.分隔线());
        列.AddChild(上分隔);

        // —— 聊天记录（滚动） ——
        _滚动 = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        列.AddChild(_滚动);
        _记录 = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 118),
        };
        _记录.AddThemeColorOverride("default_color", MicaTheme.主文字);
        _记录.AddThemeFontSizeOverride("normal_font_size", 13);
        _滚动.AddChild(_记录);

        // —— 输入行 ——
        var 输入行 = new HBoxContainer();
        输入行.AddThemeConstantOverride("separation", 6);
        列.AddChild(输入行);
        _输入框 = new LineEdit
        {
            PlaceholderText = "说点什么…",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 28),
        };
        MicaTheme.应用(_输入框);
        _输入框.TextSubmitted += _ => 提交();
        输入行.AddChild(_输入框);
        var 发送 = new Button { Text = "发送", CustomMinimumSize = new Vector2(62, 28) };
        MicaTheme.应用强调按钮(发送);
        发送.Pressed += 提交;
        输入行.AddChild(发送);

        // —— 底部横排命令栏 ——
        var 快捷分隔 = new HSeparator();
        快捷分隔.AddThemeStyleboxOverride("separator", MicaTheme.分隔线());
        列.AddChild(快捷分隔);
        var 快捷标题 = new Label { Text = "快捷功能" };
        MicaTheme.应用(快捷标题, 11, 次要: true);
        列.AddChild(快捷标题);
        _命令栏 = new HBoxContainer { Name = "CommandBar" };
        _命令栏.AddThemeConstantOverride("separation", 4);
        列.AddChild(_命令栏);

        // —— 选项列表（点「配置」等切换进来） ——
        _选项标题 = new Label { Text = "", Visible = false };
        MicaTheme.应用(_选项标题, 11, 次要: true);
        列.AddChild(_选项标题);
        _选项滚动 = new ScrollContainer { Name = "OptionScroll", Visible = false };
        列.AddChild(_选项滚动);
        _选项栏 = new GridContainer { Name = "OptionBar", Columns = 2 };
        _选项栏.AddThemeConstantOverride("h_separation", 4);
        _选项栏.AddThemeConstantOverride("v_separation", 3);
        _选项栏.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _选项滚动.AddChild(_选项栏);
        重建命令栏();
    }

    /// <summary>无边框窗口的顶栏由我们自己绘制；按住空白标题区即可拖动。</summary>
    private void 处理标题输入(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            StartDrag();
    }

    /// <summary>从 ModLoader 指令表重建命令栏（与旧右键菜单同源）。</summary>
    private void 重建命令栏()
    {
        if (_命令栏 == null) return;
        命令模式();
        foreach (var c in _命令栏.GetChildren())
        {
            _命令栏.RemoveChild(c);
            c.QueueFree();
        }

        var 隐藏表 = 读取隐藏指令();

        foreach (var 脚本 in CommandLoader.直接指令列表)
        {
            if (隐藏表.Contains(脚本.name)) continue;
            var 捕获 = 脚本;
            var b = 新命令按钮(Tr(脚本.name), 脚本.IconImg);
            b.TooltipText = "执行：" + Tr(脚本.name);
            b.Pressed += () => { 隐藏(); Main.选择脚本(捕获); };
            _命令栏.AddChild(b);
        }

        // 「工具栏」：核心按钮（不依赖旧 mod 系统的 "tool" 组 —— 旧工具删除后按钮依然在）
        var 工具栏按钮 = 新命令按钮(Tr("toolbar"), null);
        工具栏按钮.TooltipText = "打开工具栏（小组件）";
        工具栏按钮.Pressed += ToolBar.显示;
        _命令栏.AddChild(工具栏按钮);

        foreach (var key in CommandLoader.直接指令组脚本映射.Keys)
        {
            if (!CommandLoader.直接指令组映射.TryGetValue(key, out var 组)) continue;
            if (隐藏表.Contains(组.name)) continue;
            var 捕获key = key;
            var b = 新命令按钮(Tr(组.name), 组.IconImg);
            b.Pressed += () =>
            {
                if (捕获key == "config") { SettingsWindow.显示(); return; }
                显示选项(CommandLoader.直接指令组脚本映射[捕获key], Tr(组.ask));
            };
            _命令栏.AddChild(b);
        }

        // 「状态」：桌宠数值可视化（主人指定放菜单栏；数值与人格分离，见 AGENTS.md §2）
        var 状态 = 新命令按钮("状态", null);
        状态.TooltipText = "看看它现在的心情 / 精力 / 亲密";
        状态.Pressed += StatsWindow.显示;
        _命令栏.AddChild(状态);

        // 「网速」已移到工具栏「网速监控」小组件（mods/toolbar/netspeed；删目录或加 _ 前缀＝移除/禁用）

        var 关闭 = 新命令按钮(Tr("close"), null);
        关闭.Pressed += () => CharAnim.播放退出动画();
        _命令栏.AddChild(关闭);
    }

    /// <summary>切回命令模式。</summary>
    private void 命令模式()
    {
        if (_命令栏 != null) _命令栏.Visible = true;
        if (_选项滚动 != null)
        {
            _选项滚动.Visible = false;
            _选项滚动.CustomMinimumSize = Vector2.Zero;
        }
        if (_选项标题 != null) _选项标题.Visible = false;
    }

    /// <summary>在面板内显示一组选项（替代旧的弹出菜单）。</summary>
    private void 显示选项(List<可见脚本信息> 脚本列表, string 询问)
    {
        if (_选项栏 == null) return;
        _选项项.Clear();
        var 隐藏表 = 读取隐藏指令();

        if (脚本列表 != null)
        {
            foreach (var 脚本 in 脚本列表)
            {
                if (隐藏表.Contains(脚本.name)) continue;
                var 捕获 = 脚本;
                _选项项.Add((Tr(脚本.name), () => { 返回命令栏(); Main.选择脚本(捕获); }));
            }
        }

        _选项项.Add((Tr("cancel"), 返回命令栏));

        重建选项栏();
        if (_选项标题 != null)
        {
            _选项标题.Text = 询问 ?? "";
            _选项标题.Visible = !string.IsNullOrEmpty(询问);
        }
        if (_命令栏 != null) _命令栏.Visible = false;
        _选项滚动.Visible = true;
        _选项滚动.CustomMinimumSize = new Vector2(0, 104);
    }

    private void 返回命令栏() => 命令模式();

    private void 重建选项栏()
    {
        foreach (var c in _选项栏.GetChildren())
        {
            _选项栏.RemoveChild(c);
            c.QueueFree();
        }
        foreach (var (标签, 动作) in _选项项)
        {
            var b = new Button
            {
                Text = 标签,
                Flat = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 24),
            };
            MicaTheme.应用(b, 11, 扁平: true, 对齐: true);
            b.Pressed += () => 动作();
            _选项栏.AddChild(b);
        }
    }

    /// <summary>从 config/panel.json 读「隐藏指令」名单（缺失/损坏 = 不隐藏）。</summary>
    private static HashSet<string> 读取隐藏指令()
    {
        var 结果 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var 路径 in Util.ConfigFile.候选("panel.json").Concat(Util.ConfigFile.候选("config/panel.json")))
        {
            try
            {
                if (!File.Exists(路径)) continue;
                var txt = File.ReadAllText(路径);
                if (string.IsNullOrWhiteSpace(txt)) continue;
                using var doc = JsonDocument.Parse(txt);
                if (doc.RootElement.TryGetProperty("隐藏指令", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in arr.EnumerateArray())
                    {
                        var v = e.GetString();
                        if (!string.IsNullOrWhiteSpace(v)) 结果.Add(v.Trim());
                    }
                    return 结果;
                }
            }
            catch (Exception e) { GD.PrintErr($"[ChatBox] 读 {路径} 失败: {e.Message}"); }
        }
        return 结果;
    }

    private static Button 新命令按钮(string 标签, Texture2D 图标)
    {
        var b = new Button
        {
            Text = 标签,
            Flat = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Icon = 图标,
            CustomMinimumSize = new Vector2(0, 26),
        };
        MicaTheme.应用命令按钮(b, 11);
        return b;
    }

    // ================= 显隐与摆位 =================

    /// <summary>贴在桌宠旁边显示（下方优先，放不下则上方），并按屏幕边界收拢。</summary>
    private void 摆位()
    {
        var 宠位 = DisplayServer.WindowGetPosition();
        var 宠尺 = DisplayServer.WindowGetSize();
        var 屏 = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());

        var x = 宠位.X + (宠尺.X - 面板宽) / 2;
        var y = 宠位.Y + 宠尺.Y + 贴边间距;
        if (y + 面板高 > 屏.End.Y) y = 宠位.Y - 面板高 - 贴边间距; // 下方放不下 -> 上方

        x = Math.Clamp(x, 屏.Position.X + 贴边间距, Math.Max(屏.Position.X + 贴边间距, 屏.End.X - 面板宽 - 贴边间距));
        y = Math.Clamp(y, 屏.Position.Y + 贴边间距, Math.Max(屏.Position.Y + 贴边间距, 屏.End.Y - 面板高 - 贴边间距));
        Position = new Vector2I(x, y);
    }

    public static void 显示()
    {
        if (_单例 == null) return;
        _单例.Size = new Vector2I(面板宽, 面板高);
        _单例.重建命令栏();
        _单例.摆位();
        _单例.Show();
        // 显示后再滚到底：面板隐藏期间布局未计算，滚动条 MaxValue 为 0，
        // 「追加记录」里的滚到底会失效 → 刚打开时会停在最旧的一条（实测踩过）。
        _单例.请求滚到底();
        _单例._输入框?.GrabFocus();
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

    private void 设置状态(string 文本)
    {
        if (_状态 == null) return;
        _状态.Text = string.IsNullOrEmpty(文本) ? "● 待命" : "● " + 文本;
    }

    // ================= 聊天记录 =================

    private static string 显示色(string 说话人) => 说话人 == "你"
        ? MicaTheme.你说色.ToHtml(false)
        : MicaTheme.桌宠说色.ToHtml(false);

    private void 追加记录(string 说话人, string 内容)
    {
        _历史.Append($"[color=#{显示色(说话人)}][b]{说话人}[/b][/color] {转义富文本(内容)}\n\n");
        重画并滚动();
    }

    /// <summary>聊天内容是用户/Agent 数据，不应被 RichTextLabel 当成 BBCode 解释。</summary>
    private static string 转义富文本(string 内容) => (内容 ?? string.Empty).Replace("[", "[lb]");

    private void 重画并滚动()
    {
        if (_记录 == null) return;
        _记录.Text = _历史.ToString();
        请求滚到底();
    }

    private int _滚到底剩余帧;

    /// <summary>请求滚到底。用「连续几帧施加」而不是单次 CallDeferred：
    /// 面板隐藏时容器布局未计算，滚动条 MaxValue 还是 0，单次施加会落空（实测：刚打开停在最旧一条）。</summary>
    private void 请求滚到底() => _滚到底剩余帧 = 3;

    public override void _Process(double delta)
    {
        if (_滚到底剩余帧 <= 0) return;
        _滚到底剩余帧--;
        滚到底();
    }

    private void 滚到底()
    {
        if (_滚动 == null) return;
        _滚动.ScrollVertical = (int)_滚动.GetVScrollBar().MaxValue;
    }

    // ================= 流式（可跨线程） =================

    /// <summary>探针：已提交的历史文本（不含正在流式的部分）。</summary>
    public static string 探针_历史文本 => _单例?._历史?.ToString() ?? "";

    /// <summary>探针：当前显示文本（历史 + 流式中）。</summary>
    public static string 探针_显示文本 => _单例?._记录?.Text ?? "";

    /// <summary>探针：命令栏里是否有指定文本的按钮（用于验证「状态」等入口真的挂上了）。</summary>
    public static bool 探针_命令栏有按钮(string 文本)
    {
        if (_单例?._命令栏 == null) return false;
        foreach (var c in _单例._命令栏.GetChildren())
            if (c is Button b && b.Text == 文本) return true;
        return false;
    }

    /// <summary>探针：最近一次结束流式时收到的**原始**缓冲（含指令块，未过滤）——排查泄漏用。</summary>
    public static string 探针_最近原始流式 { get; private set; } = "";

    public static void 流式追加(string 块)
    {
        if (string.IsNullOrEmpty(块) || _单例 == null) return;
        _单例.CallDeferred(nameof(单例流式追加), 块);
    }

    private void 单例流式追加(string 块)
    {
        StateMachine.SetState(StateMachine.Speak);
        if (!_流式中) { _流式中 = true; _流式缓冲.Clear(); 设置状态("思考中…"); }
        _流式缓冲.Append(块);
        if (_记录 != null)
        {
            var 色 = 显示色(Soul.SoulTable.名字);
            // 指令围栏块（```pet … ```）永不显示：流式中途未闭合的块也一并截掉（见 PetCommands.解析）
            var 可见 = PetCommands.过滤显示(_流式缓冲.ToString()).TrimEnd();
            _记录.Text = _历史 + $"[color=#{色}][b]{Soul.SoulTable.名字}[/b][/color] {转义富文本(可见)}";
            请求滚到底();
        }
    }

    public static void 结束流式()
    {
        if (_单例 == null) return;
        _单例.CallDeferred(nameof(单例结束流式));
    }

    private void 单例结束流式()
    {
        _流式中 = false;
        探针_最近原始流式 = _流式缓冲.ToString();
        if (_流式缓冲.Length > 0)
        {
            var 色 = 显示色(Soul.SoulTable.名字);
            // 落进历史的是**去掉指令块**的干净文本（AgentBridge 已执行过指令，这里只负责显示）
            var 干净 = PetCommands.过滤显示(_流式缓冲.ToString()).TrimEnd();
            if (干净.Length > 0)
                _历史.Append($"[color=#{色}][b]{Soul.SoulTable.名字}[/b][/color] {转义富文本(干净)}\n\n");
        }
        _流式缓冲.Clear();
        设置状态("");
        重画并滚动();
        StateMachine.SetState(StateMachine.Idle);
    }

    // ================= 会话恢复（历史回放） =================

    private bool _历史已加分隔;

    /// <summary>
    /// 会话恢复时回放出来的一条历史消息。**必须作为独立消息追加**——它与「实时流式回复」是两回事，
    /// 混在一起会把多轮历史合并成一大段（实测 bug：`好`+`西瓜`+`连通` → `好西瓜连通`）。可跨线程调用。
    /// </summary>
    public static void 历史消息(bool 是你, string 文本)
    {
        if (_单例 == null || string.IsNullOrEmpty(文本)) return;
        _单例.CallDeferred(nameof(单例历史消息), 是你, 文本);
    }

    private void 单例历史消息(bool 是你, string 文本)
    {
        if (_流式中) return; // 保险：不打断正在进行的实时流式
        if (!_历史已加分隔)
        {
            _历史已加分隔 = true;
            var 灰 = MicaTheme.次文字.ToHtml(false);
            _历史.Append($"[color=#{灰}]──── 以下为上次会话（它还记得的旧事）────[/color]\n\n");
        }
        // 回放路径同样要过滤指令块：hermes 存的是 Agent **原始**回复（含 ```pet 围栏），
        // 恢复会话时若不过滤，历史气泡里就会露出一堆围栏块（实测：4 条历史助手消息全带围栏）。
        // 主人消息不做过滤（我们发给 Agent 的就是主人原话，不再附加任何东西 —— 见「不做主动注入」硬规则）
        var 显示 = 是你
            ? 文本
            : PetCommands.过滤显示(文本).TrimEnd();
        if (显示.Length == 0) return; // 整条都是指令块 → 不产生空气泡
        追加记录(是你 ? "你" : Soul.SoulTable.名字, 显示);
    }

    /// <summary>供探针读取聊天记录原文（只读）。</summary>
    public static string 记录文本_只读 => _单例?._历史.ToString() ?? "";

    // ================= 输入 =================

    private void 提交()
    {
        var text = _输入框?.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        _输入框!.Text = "";

        追加记录("你", text);
        设置状态("连接中");
        _历史已加分隔 = false; // 进入实时对话：历史区块结束
        StateMachine.NotifyInteraction("chat");
        StateMachine.SetState(StateMachine.Think);

        if (!AgentBridge.Ask(text))
        {
            设置状态("未连接");
            StateMachine.SetState(StateMachine.Idle);
            GD.Print("[ChatBox] 发送失败（可能无 Agent）");
        }
    }
}

/// <summary>
/// 把 AgentBridge 的后端事件接到 UI（聊天记录/动画）。
/// 独立出来，便于后端在任意时机替换（mod 式）。
/// </summary>
public static class AgentEvents
{
    private static bool _subscribed;

    public static void EnsureSubscribed()
    {
        if (_subscribed) return;
        _subscribed = true;
    }

    /// <summary>由 AgentBridge 在 Start 后调用：绑定流式回复到 UI。</summary>
    public static void Bind(IAgentBackend backend)
    {
        if (backend == null) return;
        backend.OnReplyChunk += text => ChatBox.流式追加(text);
        // 会话恢复回放的历史：作为**独立消息**追加，绝不与实时流式混在一起（否则会被合并成一大段）
        backend.OnHistoryChunk += (是你, 文本) => ChatBox.历史消息(是你, 文本);
        backend.OnTurnEnd += reason =>
        {
            ChatBox.结束流式();
        };
        backend.OnError += msg =>
        {
            StateMachine.SetState(StateMachine.Idle);
            GD.PrintErr($"[Agent] {msg}");   // 原始错误只进日志（英文异常/堆栈不该出现在气泡里，更不该被念出来）
            Dialogue.显示临时标题("唔…我这边出了点小状况，先自己待着～");
        };
    }
}
