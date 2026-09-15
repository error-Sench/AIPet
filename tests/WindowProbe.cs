using Godot;

namespace desktop.tests;

/// <summary>
/// 窗口几何诊断（新模型）：桌宠窗口恒为角色大小；聊天面板是独立窗口。
/// 验证点：唤出/关闭面板时，桌宠窗口尺寸与位置都不能变（不漂移）。
/// 需真实窗口，不要 --headless。
/// </summary>
public partial class WindowProbe : Node
{
    private int _f;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== WindowProbe ===");
    }

    public override void _Process(double delta)
    {
        _f++;
        switch (_f)
        {
            case 4:
                报告("启动后");
                break;
            case 6:
                GD.Print("[WP] 唤出聊天面板…");
                desktop.script.UX.ChatBox.显示();
                break;
            case 9:
                报告("面板显示后（桌宠窗口应完全不变）");
                报告面板();
                break;
            case 11:
                GD.Print("[WP] 关闭面板…");
                desktop.script.UX.ChatBox.隐藏();
                break;
            case 14:
                报告("面板关闭后");
                GD.Print("[WP] DONE");
                GetTree().Quit(0);
                break;
        }
    }

    private static void 报告(string 阶段)
    {
        var s = DisplayServer.WindowGetSize();
        var p = DisplayServer.WindowGetPosition();
        GD.Print($"[WP] {阶段}: 桌宠窗口={s.X}x{s.Y} pos=({p.X},{p.Y}) 角色S={desktop.script.UX.PetWindow.S}");
    }

    private void 报告面板()
    {
        var box = GetTree().Root.FindChild("ChatBox", recursive: true, owned: false) as Window;
        if (box == null) { GD.PrintErr("[WP] 找不到 ChatBox 窗口"); return; }
        GD.Print($"[WP] 聊天面板: visible={box.Visible} pos=({box.Position.X},{box.Position.Y}) size={box.Size.X}x{box.Size.Y}");
    }
}