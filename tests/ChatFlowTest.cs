using Godot;

namespace desktop.tests;

/// <summary>
/// 惰性连接 + 端到端测试：**不预先启动后端**，直接 Ask（模拟用户右键输入发送）。
/// 验证：惰性启动 -> 握手 -> 补发排队发言 -> 流式回复。
/// 运行：Godot_console.exe --headless --path <项目> res://tests/ChatFlowTest.tscn
/// </summary>
public partial class ChatFlowTest : Node
{
    private double _elapsed;
    private bool _asked;
    private bool _done;

    public override void _Ready()
    {
        GD.Print("[ChatFlow] 惰性连接测试开始（未预启动后端）");
        GD.Print($"[ChatFlow] 启动前 Backend = {(desktop.script.Agent.AgentBridge.Backend == null ? "null ✓（惰性）" : "已存在")}");

        desktop.script.Agent.AgentBridge.TurnEnded += 回复 =>
        {
            GD.Print($"[ChatFlow] 收到回复: {回复}");
            _done = true;
        };
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;

        // 第 2 帧模拟用户发送（此时后端尚未连接，考验排队）
        if (!_asked && _elapsed > 0.1)
        {
            _asked = true;
            GD.Print("[ChatFlow] 模拟发送（触发惰性启动）");
            desktop.script.Agent.AgentBridge.Ask("只回复四个字：惰性已通");
        }

        if (_done)
        {
            var r = desktop.script.Agent.AgentBridge.最后回复;
            GD.Print(r.Length > 0 ? "[ChatFlow] PASS — 惰性连接端到端通" : "[ChatFlow] WARN — 空回复");
            desktop.script.Agent.AgentBridge.Stop();
            GetTree().Quit(0);
        }
        if (_elapsed > 120) { GD.PrintErr("[ChatFlow] 超时 FAIL"); GetTree().Quit(2); }
    }
}