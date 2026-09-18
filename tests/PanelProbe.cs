using System.Linq;
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
            if (路径.Count != 4) GD.PrintErr("[Probe] 目录行数应为 4（配置/模组/存储/数据）");

            // —— 选项卡 + 自适应高度（回归：曾经窗口高写死 224 → 加了「数据」行后底部被裁） ——
            var 页签 = desktop.script.UX.SettingsWindow.页签名;
            GD.Print($"[Probe] 选项卡数 = {页签.Count}：{string.Join(" / ", 页签)}");
            var 窗高 = desktop.script.UX.SettingsWindow.窗口高;
            var 内容高 = desktop.script.UX.SettingsWindow.内容高;
            GD.Print($"[Probe] 窗口高 = {窗高}，内容高 = {内容高}");
            if (页签.Count < 4) GD.PrintErr("[Probe] 选项卡应 >= 4（常规/行为/语音/高级）");
            if (!desktop.script.UX.SettingsWindow.有滚动) GD.PrintErr("[Probe] FAIL 缺少滚动容器（内容比窗口高时要能滚）");
            else GD.Print("[Probe] 滚动容器存在 ✓（固定尺寸 + 超出滚动，不做自适应高度）");

            // —— 功能开关都进配置（键清单在构建时登记） ——
            var 行为键 = desktop.script.UX.SettingsWindow.行为键;
            var 语音键 = desktop.script.UX.SettingsWindow.语音键;
            GD.Print($"[Probe] 行为页开关数 = {行为键.Count}：{string.Join(" / ", 行为键)}");
            GD.Print($"[Probe] 语音页开关数 = {语音键.Count}：{string.Join(" / ", 语音键)}");
            if (行为键.Count < 10) GD.PrintErr("[Probe] FAIL 行为页开关过少（应含 总开关/预算/问候/磁盘/久坐/贴边/感知）");
            if (!行为键.Contains("启用") || !行为键.Contains("问候启用") || !行为键.Contains("磁盘提醒启用"))
                GD.PrintErr("[Probe] FAIL 行为页缺关键开关");
            if (!行为键.Contains("贴边循环次数") || !行为键.Contains("贴边循环内间隔秒"))
                GD.PrintErr("[Probe] FAIL 行为页缺贴边节拍开关（每两秒循环两次贴边动画）");
            if (语音键.Count < 6) GD.PrintErr("[Probe] FAIL 语音页开关过少");

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
