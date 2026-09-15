using Godot;

namespace desktop.tests;

/// <summary>
/// 会话复用验证（跨进程）：
///   第一趟（无参数）  : 告诉桌宠一个暗号并按记录会话
///   第二趟（-- read） : 询问暗号 —— 若回复内容含暗号，说明真的复用/恢复了上次会话
/// 运行：
///   Godot_console.exe --headless --path <项目> res://tests/SessionProbe.tscn
///   Godot_console.exe --headless --path <项目> res://tests/SessionProbe.tscn -- read
/// </summary>
public partial class SessionProbe : Node
{
    private const string 暗号 = "西瓜";
    private double _elapsed;
    private bool _asked;
    private bool _done;
    private bool _读模式;

    public override void _Ready()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.Trim().ToLowerInvariant() == "read") _读模式 = true;
        GD.Print($"[SP] 模式 = {(_读模式 ? "询问暗号" : "设定暗号")}");

        desktop.script.Agent.AgentBridge.TurnEnded += 回复 =>
        {
            GD.Print($"[SP] 回复: {回复}");
            _done = true;
        };
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (!_asked && _elapsed > 0.1)
        {
            _asked = true;
            var 提问 = _读模式
                ? "我刚才让你记住的暗号是什么？只回复那个词。"
                : $"请记住这个暗号：{暗号}。只回复「好」。";
            GD.Print($"[SP] 发送: {提问}");
            desktop.script.Agent.AgentBridge.Ask(提问);
        }

        if (_done)
        {
            var r = desktop.script.Agent.AgentBridge.最后回复;
            if (_读模式)
                GD.Print(r.Contains(暗号) ? "[SP] PASS — 会话记忆延续成功" : "[SP] FAIL — 未延续（失忆）");
            else
                GD.Print("[SP] 设定完成");
            desktop.script.Agent.AgentBridge.Stop();
            GetTree().Quit(0);
        }
        if (_elapsed > 120) { GD.PrintErr("[SP] 超时"); GetTree().Quit(2); }
    }
}