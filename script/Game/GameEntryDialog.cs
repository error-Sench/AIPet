using Godot;
using desktop.script.UX;

namespace desktop.script.Game;

/// <summary>
/// 「进入游戏模式」确认弹窗（入口：右键双击桌宠 / 聊天面板「游戏模式」按钮）。
/// <para>
/// 操作：**空格 = 确认**（主人指定）；Esc = 取消；也可以点按钮。
/// 键盘双保险：`_Input` 只负责「消费」空格/Esc（防焦点按钮重复触发），
/// 真正的动作走 `_Process` 轮询 —— 应用在前台时，即使焦点不在本窗，空格照样确认。
/// </para>
/// 懒创建（首次请求时才建窗口；同面板样式）。约定：标识符英文，注释中文。
/// </summary>
public partial class GameEntryDialog : Window
{
    private const int 宽 = 320;
    private const int 高 = 148;

    private static GameEntryDialog _单例;

    public static bool 存在 => _单例 != null;

    /// <summary>探针：最近一次动作（「进入」/「取消」/ 空串 = 还没操作过）。</summary>
    public static string 探针_最近动作 { get; private set; } = "";

    /// <summary>探针：弹窗是否可见。</summary>
    public static bool 探针_可见 => _单例 is { Visible: true };

    private bool _上次空格;
    private bool _上次Esc;

    /// <summary>入口：打开确认弹窗（懒创建；重复调用只是再弹一次）。</summary>
    public static void 请求进入()
    {
        if (_单例 == null)
        {
            _单例 = new GameEntryDialog { Name = "GameEntryDialog", Visible = false };
            var 根 = ((SceneTree)Engine.GetMainLoop()).Root;
            根.CallDeferred(Node.MethodName.AddChild, _单例);
            _单例.CallDeferred(nameof(延迟弹出));
        }
        else
        {
            _单例.弹出();
        }
    }

    private void 延迟弹出()
    {
        if (!IsInsideTree()) { CallDeferred(nameof(延迟弹出)); return; }   // 等上树（只重试一帧）
        弹出();
    }

    public override void _Ready()
    {
        Title = "进入游戏模式";
        Visible = false;
        Borderless = true;
        Transparent = true;
        Unresizable = true;
        Size = new Vector2I(宽, 高);
        CloseRequested += 取消;
        BuildUi();
    }

    private void BuildUi()
    {
        var 根面板 = new PanelContainer();
        根面板.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        根面板.AddThemeStyleboxOverride("panel", MicaTheme.面板(16));
        AddChild(根面板);

        var 列 = new VBoxContainer();
        列.AddThemeConstantOverride("separation", 8);
        根面板.AddChild(列);

        var 标题 = new Label { Text = "进入游戏模式？" };
        MicaTheme.应用(标题, 15);
        列.AddChild(标题);

        var 副题 = new Label { Text = "横板玩法，随时切回办公（进度保留）\n空格 确认 · Esc 取消" };
        MicaTheme.应用(副题, 11, 次要: true);
        列.AddChild(副题);

        var 行 = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        行.AddThemeConstantOverride("separation", 6);
        列.AddChild(行);

        var 取消钮 = new Button { Text = "取消", CustomMinimumSize = new Vector2(72, 28) };
        MicaTheme.应用(取消钮, 12);
        取消钮.Pressed += 取消;
        行.AddChild(取消钮);

        var 进入钮 = new Button { Text = "进入游戏", CustomMinimumSize = new Vector2(96, 28) };
        MicaTheme.应用强调按钮(进入钮, 12);
        进入钮.Pressed += 确认;
        行.AddChild(进入钮);
    }

    private void 弹出()
    {
        if (!IsInsideTree()) return;
        _上次空格 = Input.IsPhysicalKeyPressed(Key.Space);
        _上次Esc = Input.IsPhysicalKeyPressed(Key.Escape);
        var 屏 = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
        var 位 = new Vector2I(屏.Position.X + (屏.Size.X - 宽) / 2, 屏.Position.Y + (屏.Size.Y - 高) / 2);
        Popup(new Rect2I(位, new Vector2I(宽, 高)));
        GrabFocus();
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible) return;
        // 只消费、不办事（真动作在 _Process 轮询里，避免与焦点按钮重复触发）
        if (@event is InputEventKey { Pressed: true } 键 &&
            (键.PhysicalKeycode == Key.Space || 键.PhysicalKeycode == Key.Escape))
            GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        var 空格 = Input.IsPhysicalKeyPressed(Key.Space);
        var esc = Input.IsPhysicalKeyPressed(Key.Escape);
        if (空格 && !_上次空格) 确认();
        else if (esc && !_上次Esc) 取消();
        _上次空格 = 空格;
        _上次Esc = esc;
    }

    private void 确认()
    {
        探针_最近动作 = "进入";
        Hide();
        GD.Print("[Game] 确认进入 → 切换游戏模式");
        // 写全限定名：本类继承 Window，裸写 `Mode.` 会被 Window.Mode 成员遮蔽（C# 命名空间 §16 坑）
        desktop.script.Mode.ModeManager.SwitchMode(desktop.script.Mode.ModeManager.Mode.Game);
    }

    private void 取消()
    {
        探针_最近动作 = "取消";
        Hide();
        GD.Print("[Game] 已取消（留在办公模式）");
    }
}
