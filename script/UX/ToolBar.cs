using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using desktop.script.Loader;
using desktop.script.Util;
using desktop.script.logic;
using Godot;

namespace desktop.script.UX;

/// <summary>
/// 工具栏：独立窗口，承载一堆「小组件 / 工具」按钮（与指令栏分离）。
/// 扩展方式（mod 式）：在 `mods/toolbar/<工具名>/` 放 info.json —— `name`（显示名）、`desc`（悬停说明）、
/// `action`（可选的内置动作名，点击执行代码；无 action 的 mod 只占位）。
/// 禁用 / 删除：目录加 `_` 前缀＝禁用（与 mods 既有约定一致，见 Main.IgnorePath）；直接删目录＝移除。
/// 导出后 `mods/` 在 exe 旁边（`LoadUtil.ModPath`），用户随时可增删。
/// 关闭方式：① 内容区 × 按钮；② 标题栏关闭按钮（CloseRequested）。
/// 约定：标识符英文，注释中文（见 AGENTS.md §8）。
/// </summary>
public partial class ToolBar : Window
{
    private static ToolBar _单例;
    private GridContainer _网格;
    private Label _占位;

    /// <summary>内置动作：mod 的 info.json 用 `action` 声明名字 → 点击时执行这里的代码（声明与实现分离）。</summary>
    private sealed record 工具动作(Action 执行, Func<bool> 启用中);

    private static readonly Dictionary<string, 工具动作> 内置动作 = new()
    {
        ["netspeed"] = new(NetSpeedBubble.开关, () => NetSpeedBubble.可见),
    };

    /// <summary>是否还有（未被禁用的）mod 声明该动作 —— 网速气泡据此判断工具栏入口是否仍在。</summary>
    public static bool 动作存在(string 动作名)
    {
        foreach (var (_, _, 动作, _) in 扫描工具())
            if (动作 == 动作名) return true;
        return false;
    }

    public static bool 存在 => _单例 != null;

    /// <summary>工具栏是否可见（供状态机判断「主人正在跟我们互动」，属「不打扰」约束）。</summary>
    public static bool 可见 => _单例 is { Visible: true };

    /// <summary>工具栏位于鼠标下方时优先接管指针，避免底层桌宠误入拖拽。</summary>
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
        Title = "工具栏";
        Visible = false;
        Borderless = true;
        Transparent = true;
        Unresizable = true;
        MinSize = new Vector2I(300, 210);
        Size = new Vector2I(360, 260);
        // 注意：不要设 AlwaysOnTop —— Windows 下置顶窗口不能是 transient（PopupCentered 会强制 transient），
        // 否则每次弹出都报 "Windows with the 'on top' can't become transient."

        // 标题栏关闭按钮：显式处理（不依赖引擎默认行为）
        CloseRequested += 隐藏;

        BuildUi();
        GD.Print($"[ToolBar] 就绪: 嵌入子窗口={GetTree().Root.GuiEmbedSubwindows}, 尺寸={Size}, 无边框={Borderless}");
    }

    private void BuildUi()
    {
        var 面板 = new PanelContainer();
        面板.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        面板.AddThemeStyleboxOverride("panel", MicaTheme.面板(14));
        AddChild(面板);

        var 列 = new VBoxContainer();
        列.AddThemeConstantOverride("separation", 8);
        面板.AddChild(列);

        // —— 标题行（无边框窗口没有系统标题栏，关闭按钮自绘） ——
        var 标题底 = new PanelContainer();
        标题底.AddThemeStyleboxOverride("panel", MicaTheme.信息底());
        标题底.GuiInput += 处理标题输入;
        列.AddChild(标题底);
        var 标题行 = new HBoxContainer();
        标题行.AddThemeConstantOverride("separation", 8);
        标题底.AddChild(标题行);
        var 标题 = new Label { Text = "工具栏", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        MicaTheme.应用(标题, 15);
        标题行.AddChild(标题);
        var 副标题 = new Label { Text = "本地快捷能力" };
        MicaTheme.应用(副标题, 11, 次要: true);
        标题行.AddChild(副标题);
        var 关闭 = new Button { Text = "×", TooltipText = "关闭", CustomMinimumSize = new Vector2(26, 24) };
        MicaTheme.应用(关闭, 16, 扁平: true);
        关闭.Pressed += 隐藏;
        标题行.AddChild(关闭);

        var 分隔 = new HSeparator();
        分隔.AddThemeStyleboxOverride("separator", MicaTheme.分隔线());
        列.AddChild(分隔);

        _网格 = new GridContainer { Columns = 3, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _网格.AddThemeConstantOverride("h_separation", 6);
        _网格.AddThemeConstantOverride("v_separation", 6);
        列.AddChild(_网格);

        _占位 = new Label
        {
            Text = "这里还没有额外工具\n在 mods/toolbar/ 放入工具目录后，它会出现在这里。",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        MicaTheme.应用(_占位, 12, 次要: true);
        列.AddChild(_占位);

        重建();
    }

    /// <summary>无边框工具窗的自绘标题区域负责拖动。</summary>
    private void 处理标题输入(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            StartDrag();
    }

    /// <summary>扫描 mods/toolbar 重建工具格。</summary>
    private void 重建()
    {
        foreach (var c in _网格.GetChildren()) { _网格.RemoveChild(c); c.QueueFree(); }

        var 工具 = 扫描工具();
        _网格.Visible = 工具.Count > 0;
        _占位.Visible = 工具.Count == 0;

        foreach (var (名称, 目录, 动作, 说明) in 工具)
        {
            var b = new Button { Text = 名称, CustomMinimumSize = new Vector2(0, 44) };
            MicaTheme.应用(b, 12);
            // 有开关状态的动作：标题带「·开 / ·关」（如「网速监控 ·开」）
            if (!string.IsNullOrEmpty(动作) && 内置动作.TryGetValue(动作, out var 实现) && 实现.启用中 != null)
                b.Text = $"{名称} ·{(实现.启用中() ? "开" : "关")}";
            if (!string.IsNullOrEmpty(说明)) b.TooltipText = 说明;
            捕获(b, 名称, 目录, 动作);
            _网格.AddChild(b);
        }
    }

    private static void 捕获(Button b, string 名称, string 目录, string 动作)
    {
        b.Pressed += () =>
        {
            if (!string.IsNullOrEmpty(动作) && 内置动作.TryGetValue(动作, out var 实现))
            {
                GD.Print($"[ToolBar] 执行动作: {动作}");
                实现.执行();
                _单例?.重建();   // 刷新按钮的「·开 / ·关」
                return;
            }
            // 旧指令派发：action = "command:<指令名>"（如 command:task）→ 与命令栏同一条执行路径
            if (动作 != null && 动作.StartsWith("command:", StringComparison.Ordinal))
            {
                var 名 = 动作["command:".Length..];
                var 脚本 = CommandLoader.按名查找(名);
                if (脚本 == null)
                {
                    GD.PrintErr($"[ToolBar] 找不到指令: {名}（mods 里没有 name={名} 的指令）");
                    Dialogue.显示临时标题($"工具「{名称}」的指令不存在", 2500);
                    return;
                }
                GD.Print($"[ToolBar] 派发旧指令: {名}");
                隐藏();                 // 启动器语义：点完收起
                Main.选择脚本(脚本);
                return;
            }
            // mod 只声明了目录、没有可执行动作：占位提示
            GD.Print($"[ToolBar] 点击工具: {目录}（无动作）");
            Dialogue.显示临时标题($"工具「{名称}」尚未实现", 2500);
        };
    }

    /// <summary>扫描工具栏 mod（`mods/toolbar/`；导出后 `mods/` 在 exe 旁，与主 mod 系统同一根 —— LoadUtil.ModPath）。</summary>
    private static List<(string 名称, string 目录, string 动作, string 说明)> 扫描工具()
    {
        var 结果 = new List<(string, string, string, string)>();
        string 根;
        try { 根 = Path.Combine(LoadUtil.ModPath, "toolbar"); }
        catch { return 结果; }
        if (!Directory.Exists(根)) return 结果;

        foreach (var 目录 in Directory.GetDirectories(根))
        {
            var 子 = Path.GetFileName(目录);
            if (子.StartsWith("_")) continue;   // `_` 前缀＝禁用（与 mods 既有约定一致，见 Main.IgnorePath）
            var 名称 = 子;
            string 动作 = null, 说明 = null;
            try
            {
                var info = Path.Combine(目录, "info.json");
                if (File.Exists(info))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(info));
                    var 元素 = doc.RootElement;
                    if (元素.TryGetProperty("name", out var n)) 名称 = n.GetString() ?? 名称;
                    if (元素.TryGetProperty("action", out var a)) 动作 = a.GetString();
                    if (元素.TryGetProperty("desc", out var d)) 说明 = d.GetString();
                }
            }
            catch (Exception e) { GD.PrintErr($"[ToolBar] 读 {目录}/info.json 失败: {e.Message}"); }
            结果.Add((名称, 目录, 动作, 说明));
        }
        return 结果;
    }

    public static void 显示()
    {
        if (_单例 == null) return;
        _单例.重建();
        _单例.PopupCentered();
        GD.Print($"[ToolBar] 已弹出: visible={_单例.Visible}, pos={_单例.Position}, size={_单例.Size}");
    }

    public static void 隐藏()
    {
        if (_单例 == null) return;
        _单例.Hide();
        GD.Print("[ToolBar] 已关闭");
    }
}
