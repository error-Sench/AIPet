using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 聊天面板实机探针（**非 headless**：要真实布局与渲图）：
/// ① 命令栏入口：有「游戏模式」、无「状态」、无「关闭」（主人 2026-09-20 定）；
/// ②「退出桌宠」按钮：红底 + 关机图标 + 贴命令栏最右（穷尽的数值断言）；
/// ③ 面板渲成 PNG（红底 / 贴边这类视觉判断必须看得见，不能只靠数值）。
/// 用法：Godot_..._console.exe --path D:/Games/Github/AIPet res://tests/ChatBoxShot.tscn
/// </summary>
public partial class ChatBoxShot : Node
{
    private int _帧;
    private int _失败;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== ChatBoxShot: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[CS] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[CS] FAIL  {描述}"); }
    }

    private static Button 找按钮(Node 栏, string 文本)
    {
        if (栏 == null) return null;
        foreach (var c in 栏.GetChildren())
            if (c is Button b && b.Text == 文本) return b;
        return null;
    }

    public override void _Process(double delta)
    {
        _帧++;

        if (_帧 == 10)
        {
            ChatBox.显示();
            GD.Print("[CS] 聊天面板已打开");
        }
        else if (_帧 == 40)
        {
            var 面板 = GetChild(0).GetNodeOrNull("ChatBox") as Window;
            var 栏 = 面板?.GetNodeOrNull("Root/VBox/CommandBar") as Control;
            if (栏 == null) { GD.PrintErr("[CS] 命令栏缺失"); GetTree().Quit(1); return; }

            // —— ① 入口替换（主人 2026-09-20：「状态」改成游戏模式入口） ——
            断言(找按钮(栏, "游戏模式") != null, "命令栏有「游戏模式」入口（原「状态」位）");
            断言(找按钮(栏, "状态") == null, "命令栏不再有「状态」按钮（状态窗退为隐藏界面）");
            断言(找按钮(栏, "关闭") == null, "旧的「关闭」按钮已改名（不残留）");

            // —— ②「退出桌宠」：红底 + 关机图标 + 贴最右 ——
            var 退出 = 找按钮(栏, "退出桌宠");
            断言(退出 != null, "命令栏有「退出桌宠」按钮");
            if (退出 != null)
            {
                断言(退出.Icon != null, "带图标（关机图标）");
                var 底 = (退出.GetThemeStylebox("normal") as StyleBoxFlat)?.BgColor ?? Colors.Transparent;
                GD.Print($"[CS] 「退出桌宠」底色 = {底}");
                断言(底.R > 0.6f && 底.G < 0.5f && 底.B < 0.5f, $"红色底包裹（{底}）");

                var 栏子 = 栏.GetChildren();
                var 是末位 = 栏子.Count > 0 && 栏子[栏子.Count - 1] == 退出;
                断言(是末位, "是命令栏最后一个按钮（结构上贴右）");
                var 右缘 = 退出.Position.X + 退出.Size.X;
                GD.Print($"[CS] 退出按钮 位置={退出.Position} 尺寸={退出.Size} 右缘={右缘} 栏宽={栏.Size.X}");
                断言(Mathf.Abs(右缘 - 栏.Size.X) <= 6f,
                    $"贴近右边：按钮右缘 {右缘} ≈ 命令栏右缘 {栏.Size.X}");
            }

            // —— ③ 面板渲图（供视觉复核） ——
            var 图 = 面板?.GetTexture()?.GetImage();
            if (图 != null)
            {
                var 路径 = ProjectSettings.GlobalizePath("user://chatbox_shot.png");
                图.SavePng(路径);
                GD.Print($"[CS] 面板截图 = {路径}（{图.GetWidth()}x{图.GetHeight()}，保持 5 秒供视觉复核）");
            }
            else 断言(false, "面板渲图失败（拿不到窗口纹理）");
        }
        else if (_帧 == 40 + 60 * 5)
        {
            GD.Print($"[CS] ===== 失败数 = {_失败} =====");
            GD.Print(_失败 == 0 ? "[CS] PASS" : "[CS] FAIL");
            GetTree().Quit(_失败 == 0 ? 0 : 1);
        }
        else if (_帧 > 60 * 30)
        {
            GD.PrintErr("[CS] 超时 FAIL");
            GetTree().Quit(2);
        }
    }
}
