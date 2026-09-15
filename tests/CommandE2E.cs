using desktop.script.Agent;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 指令通道**真·端到端**探针：真实 Agent（ACP）→ 流式回复 → 围栏块过滤 → 指令执行。
/// 与 CommandProbe 的区别：那边是合成文本（逻辑正确性），这边验的是**真实 LLM 输出**这条链，
/// 尤其是「跨 chunk 拼接的围栏块会不会漏到界面上」。
/// 会真实调用一次 LLM（污染会话列表，可接受）。
/// 用法：Godot_..._console.exe --headless --path D:/Games/Github/AIPet res://tests/CommandE2E.tscn
/// </summary>
public partial class CommandE2E : Node
{
    private int _帧;
    private int _失败;
    private bool _已问;
    private bool _见interact;
    private bool _见气泡;
    private int _问前历史长度 = -1;
    private const int 超时帧 = 60 * 120;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== CommandE2E: 场景已实例化 ===");
        AgentBridge.TurnEnded += 收到回复;
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[E2E] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[E2E] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;

        // 采样瞬时状态：interact 是短序列，事后读不到，必须边走边记
        if (StateMachine.CurrentState == StateMachine.Interact) _见interact = true;
        if (Dialogue.探针_当前标题.Contains("E2E")) _见气泡 = true;

        if (_帧 == 10 && !_已问)
        {
            _已问 = true;
            _问前历史长度 = ChatBox.探针_历史文本.Length; // 只查本轮新增：会话恢复回放里含「我自己的提示词」，它当然带围栏块
            var 提示 =
                "请原样复制下面这个代码块（一个字都不要改，保留 ```pet 围栏），" +
                "然后在它后面另起一行写「指令已下达」。不要解释，不要额外包装。\n" +
                "```pet\n{\"cmd\":\"set_state\",\"state\":\"interact\"}\n" +
                "{\"cmd\":\"speak\",\"text\":\"E2E 验证：指令通道已通\"}\n```";
            GD.Print("[E2E] 已向真实 Agent 发问（含 pet 围栏块）");
            if (!AgentBridge.Ask(提示)) { GD.PrintErr("[E2E] Ask 失败（Agent 未就绪？）"); GetTree().Quit(3); }
        }
        else if (_帧 > 超时帧 && _已问)
        {
            GD.PrintErr("[E2E] 超时 FAIL");
            GD.Print($"[E2E] ===== 失败数 = {_失败} =====");
            GetTree().Quit(2);
        }
    }

    private void 收到回复(string 干净)
    {
        GD.Print($"[E2E] 收到的干净回复 = 「{干净}」");
        GD.Print($"[E2E] 历史文本 = 「{ChatBox.探针_历史文本}」");
        GD.Print($"[E2E] 累计执行指令数 = {PetCommands.累计执行}");
        foreach (var l in PetCommands.最近日志) GD.Print($"[E2E]   日志: {l}");

        断言(!干净.Contains("pet") && !干净.Contains("cmd"), "E2E1 AgentBridge 给出的回复已剔除围栏块");
        断言(PetCommands.累计执行 >= 1, "E2E2 真实 Agent 的指令真的被执行了（累计≥1）");

        var 历史 = ChatBox.探针_历史文本;
        var 助手们 = 助手消息(历史);
        GD.Print($"[E2E] 助手消息条数 = {助手们.Count}，最后一条 = 「{(助手们.Count > 0 ? 助手们[^1] : "")}」");
        // 只查**助手消息**：会话恢复回放里含我自己发的提示词（它当然带围栏块），不该被算成残留
        // 诊断：把每条助手消息的首行打出来，定位围栏泄漏来自哪一条
        for (var k = 0; k < 助手们.Count; k++)
            GD.Print($"[E2E]   助手#{k}: 「{助手们[k].Replace("\n", "\\n")}」");
        GD.Print($"[E2E] 本轮原始流式 = 「{ChatBox.探针_最近原始流式.Replace("\n", "\\n")}」");
        GD.Print($"[E2E] 本轮提交后历史 = 「{历史.Replace("\n", "\\n")}」");
        断言(助手们.Count > 0 && 助手们.TrueForAll(m => !m.Contains("```") && !m.Contains("cmd")),
            "E2E3 所有助手气泡里都没有围栏块残留（跨 chunk 拼接也没漏到界面）");

        // interact 与气泡是瞬时表现，等几帧确认
        CallDeferred(nameof(延迟断言));
    }

    /// <summary>从历史富文本里抽出所有「小萝」气泡的正文（用于断言残留）。</summary>
    private static System.Collections.Generic.List<string> 助手消息(string 历史)
    {
        var 结果 = new System.Collections.Generic.List<string>();
        const string 标记 = "[b]小萝[/b][/color] ";
        var i = 0;
        while ((i = 历史.IndexOf(标记, i, System.StringComparison.Ordinal)) >= 0)
        {
            var 起 = i + 标记.Length;
            var 止 = 历史.IndexOf('\n', 起);
            结果.Add(止 < 0 ? 历史[起..] : 历史[起..止]);
            i = 起;
        }
        return 结果;
    }

    private void 延迟断言()
    {
        _ = 延迟断言协程();
    }

    private async System.Threading.Tasks.Task 延迟断言协程()
    {
        await ToSignal(GetTree().CreateTimer(2.0), SceneTreeTimer.SignalName.Timeout);
        断言(_见interact || StateMachine.CurrentState == StateMachine.Interact,
            $"E2E4 set_state=interact 真的进了交互状态（采到={_见interact}）");
        断言(_见气泡, "E2E5 speak 真的落到气泡上");
        GD.Print($"[E2E] ===== 失败数 = {_失败} =====");
        GD.Print(_失败 == 0 ? "[E2E] PASS" : "[E2E] FAIL");
        GetTree().Quit(_失败 == 0 ? 0 : 1);
    }
}