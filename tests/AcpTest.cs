using Godot;

namespace desktop.tests;

/// <summary>
/// headless 链路测试：注册后端 -> 启动 -> 发一句 -> 收流式回复 -> 打印 -> 退出。
/// 运行：Godot_console.exe --headless --path <项目> res://tests/AcpTest.tscn
/// 验证：Godot C# 侧 Process + 多线程轮询 真能驱动后端；且后端是 mod 式可插拔的。
/// </summary>
public partial class AcpTest : Node
{
    private desktop.script.Agent.IAgentBackend _backend;
    private readonly System.Text.StringBuilder _reply = new();
    private double _elapsed;
    private bool _sent;
    private bool _done;

    public override void _Ready()
    {
        GD.Print("[AcpTest] 注册后端…");
        desktop.script.Agent.AgentBackends.RegisterBuiltins();
        GD.Print($"[AcpTest] 已注册后端: {string.Join(", ", desktop.script.Agent.AgentBackends.Names)}");

        _backend = desktop.script.Agent.AgentBackends.Create("hermes-acp");
        GD.Print($"[AcpTest] 后端 = {_backend.Name}");

        _backend.OnReplyChunk += t => { _reply.Append(t); GD.Print($"[chunk] {t}"); };
        _backend.OnTurnEnd += reason =>
        {
            GD.Print($"[AcpTest] ===== 回复完成({reason}) =====");
            GD.Print($"[AcpTest] 完整回复: {_reply}");
            _done = true;
        };
        _backend.OnError += m => GD.PrintErr($"[AcpTest] ERR: {m}");

        var options = new desktop.script.Agent.AgentOptions
        {
            Executable = @"C:\Users\sench\AppData\Local\hermes\hermes-agent\venv\Scripts\hermes.exe",
            Arguments = "acp",
            WorkingDirectory = ProjectSettings.GlobalizePath("user://"),
        };
        if (!_backend.Start(options)) { GD.PrintErr("[AcpTest] 启动失败"); GetTree().Quit(1); return; }
        GD.Print("[AcpTest] 后端已启动，等待会话…");
    }

    public override void _Process(double delta)
    {
        _backend?.Poll();
        _elapsed += delta;

        if (!_sent && _backend?.IsReady == true)
        {
            _sent = true;
            GD.Print("[AcpTest] 会话就绪，发送测试消息");
            _backend.Ask("只回复四个字：桌宠链路已通");
        }

        if (_done) { GD.Print("[AcpTest] PASS"); _backend.Dispose(); GetTree().Quit(0); }
        if (_elapsed > 120) { GD.PrintErr("[AcpTest] 超时 FAIL"); _backend?.Dispose(); GetTree().Quit(2); }
    }
}