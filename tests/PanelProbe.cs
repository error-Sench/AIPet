using Godot;

namespace desktop.tests;

/// <summary>
/// 面板诊断：加载 game.tscn -> 等 Main._Ready 加载模组 -> 唤出面板 -> 检查命令栏与选项栏。
/// </summary>
public partial class PanelProbe : Node
{
    private int _frames;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== PanelProbe: 场景已实例化 ===");
    }

    public override void _Process(double delta)
    {
        _frames++;
        if (_frames == 3)
        {
            GD.Print($"[Probe] 直接指令数 = {desktop.script.Loader.CommandLoader.直接指令列表.Count}");
            GD.Print($"[Probe] 指令组数   = {desktop.script.Loader.CommandLoader.直接指令组脚本映射.Count}");
            desktop.script.UX.ChatBox.显示();
            GD.Print($"[Probe] ChatBox.存在 = {desktop.script.UX.ChatBox.存在}");
            desktop.script.State.StateMachine.SetState(desktop.script.State.StateMachine.Think);
            GD.Print($"[Probe] 状态机当前状态 = {desktop.script.State.StateMachine.CurrentState}");
            if (desktop.script.State.StateMachine.CurrentState != desktop.script.State.StateMachine.Think)
                GD.PrintErr("[Probe] 状态机未进入 think ❌");
            Dump栏("命令栏", "Root/VBox/CommandBar");
        }
        else if (_frames == 5)
        {
            // 按下「配置」按钮 -> 应弹出独立的配置窗
            var bar = 节点("Root/VBox/CommandBar");
            if (bar != null)
            {
                foreach (var c in bar.GetChildren())
                {
                    if (c is Button b && b.Text == "配置")
                    {
                        GD.Print("[Probe] 按下「配置」…");
                        b.EmitSignal(BaseButton.SignalName.Pressed);
                        break;
                    }
                }
            }
        }
        else if (_frames == 7)
        {
            GD.Print($"[Probe] SettingsWindow.存在 = {desktop.script.UX.SettingsWindow.存在}");
            GD.Print($"[Probe] SettingsWindow.可见 = {desktop.script.UX.SettingsWindow.可见}");

            var 语言 = desktop.script.UX.SettingsWindow.语言项;
            GD.Print($"[Probe] 语言下拉项数 = {语言.Count}");
            foreach (var t in 语言) GD.Print($"[Probe]   · {t}");

            var 路径 = desktop.script.UX.SettingsWindow.路径文本;
            GD.Print($"[Probe] 目录行数 = {路径.Count}");
            foreach (var p in 路径) GD.Print($"[Probe]   · {p}");

            if (!desktop.script.UX.SettingsWindow.可见) GD.PrintErr("[Probe] 配置窗未弹出");
            if (语言.Count < 5) GD.PrintErr("[Probe] 语言下拉项过少");
            if (路径.Count != 3) GD.PrintErr("[Probe] 目录行数应为 3");

            // 切回聊天面板并确认命令栏仍在（配置不再占用面板内的选项栏）
            desktop.script.UX.ChatBox.显示();
            var cmd = 节点("Root/VBox/CommandBar");
            GD.Print($"[Probe] 命令栏可见 = {(cmd as CanvasItem)?.Visible}（应为 True）");

            desktop.script.State.StateMachine.SetState(desktop.script.State.StateMachine.Idle);
            GD.Print($"[Probe] 状态机回到 = {desktop.script.State.StateMachine.CurrentState}");
            GD.Print("[Probe] DONE");
            GetTree().Quit(0);
        }
    }

    private Node 节点(string 路径)
    {
        var box = GetChild(0).GetNodeOrNull("ChatBox");
        return box?.GetNodeOrNull(路径);
    }

    private void Dump栏(string 名, string 路径)
    {
        var bar = 节点(路径);
        if (bar == null) { GD.PrintErr($"[Probe] {名}缺失 ❌"); return; }
        var n = 0;
        foreach (var c in bar.GetChildren()) if (c is Button) n++;
        GD.Print($"[Probe] {名} 按钮数 = {n}");
        foreach (var c in bar.GetChildren())
            if (c is Button b) GD.Print($"[Probe]   [{b.Text}]");
            else if (c is Label l && !string.IsNullOrEmpty(l.Text)) GD.Print($"[Probe]   ({l.Text})");
    }
}
