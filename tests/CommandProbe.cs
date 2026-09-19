using desktop.script.Agent;
using desktop.script.Mode;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 指令通道探针（headless）：Agent 回复内嵌指令块的**解析 / 白名单校验 / 执行 / 显示过滤**。
/// 三组：
///  A. 解析（纯函数，含流式中途未闭合的块、块外普通代码块不得误伤）
///  B. 执行（真实落到状态机/动画/气泡/模式；含越权与超限的拒绝）
///  C. 显示（真的走 ChatBox 流式接口，断言围栏块永不显示）
/// 用法：Godot_..._console.exe --headless --path D:/Games/Github/AIPet res://tests/CommandProbe.tscn
/// </summary>
public partial class CommandProbe : Node
{
    private int _帧;
    private int _失败;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== CommandProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[CP] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[CP] FAIL  {描述}"); }
    }

    private void 解析组()
    {
        GD.Print("--- A 组：解析 ---");

        // A1 基本：文字 + 围栏块 + 文字
        var 原 = "好的，我这就去看看喵~\n```pet\n{\"cmd\":\"set_state\",\"state\":\"think\"}\n```\n看完了！";
        var (显示, 指令) = PetCommands.解析(原);
        断言(指令.Count == 1 && 指令[0].Cmd == "set_state" && 指令[0].取值("state") == "think",
            $"A1 解析出 1 条 set_state=think（实际 {指令.Count} 条 / {指令[0].Cmd}）");
        断言(!显示.Contains("pet") && !显示.Contains("cmd") && 显示.Contains("好的") && 显示.Contains("看完了"),
            "A1 显示文本剔除了围栏块但保留前后文字");

        // A2 键值行 + 键名别名（不同 Agent 写法不一）
        var (_, 指令2) = PetCommands.解析("```pet\nset_state state=working\n{\"命令\":\"speak\",\"文本\":\"查到了\"}\n```");
        断言(指令2.Count == 2 && 指令2[0].Cmd == "set_state" && 指令2[0].取值("state") == "working",
            "A2a 键值行 set_state state=... 解析正确");
        断言(指令2[1].Cmd == "speak" && 指令2[1].取值("text") == "查到了",
            "A2b 中文别名键（命令/文本）归位正确");

        // A3 未闭合的块（流式中途）→ 必须被截掉
        var (显示3, _) = PetCommands.解析("开始流式…\n```pet\n{\"cmd\":\"speak\",\"text\":\"喵");
        断言(!显示3.Contains("pet") && !显示3.Contains("喵") && 显示3.Contains("开始流式"),
            "A3 流式中途未闭合的块被截掉（不会闪出半截指令）");

        // A4 块外普通代码块不得误伤
        var (显示4, 指令4) = PetCommands.解析("看代码：\n```json\n{\"cmd\":\"delete_everything\"}\n```\n就这样");
        断言(指令4.Count == 0, $"A4a 非 pet 围栏（```json）不被当成指令（实际 {指令4.Count} 条）");
        断言(显示4.Contains("delete_everything"), "A4b 普通代码块照常显示");

        // A6（已删）：原「剥掉注入的状态行」断言 —— 主人 2026-09-16 定稿「不做主动注入」后，
        // 我们发给 Agent 的文本就是主人原话，没有需要剥离的东西；对应的 `PetCommands.去掉状态行()` 也已删除。

        // A5 坏 JSON 行不炸，只是忽略
        var (_, 指令5) = PetCommands.解析("```pet\n{坏JSON\nset_state state=idle\n```");
        断言(指令5.Count == 1 && 指令5[0].Cmd == "set_state", "A5 坏 JSON 行被忽略，同块其它行仍生效");
    }

    private void 执行组()
    {
        GD.Print("--- B 组：执行（白名单/参数校验） ---");

        // B1 set_state 合法
        StateMachine.SetState(StateMachine.Idle);
        var (_, c1) = PetCommands.解析("```pet\n{\"cmd\":\"set_state\",\"state\":\"think\"}\n```");
        var n1 = PetCommands.执行(c1, out var log1);
        断言(n1 == 1 && StateMachine.CurrentState == StateMachine.Think, "B1 set_state=think 执行成功");

        // B2 set_state 越权（任意字符串）
        var (_, c2) = PetCommands.解析("```pet\n{\"cmd\":\"set_state\",\"state\":\"../../evil\"}\n```");
        var n2 = PetCommands.执行(c2, out var log2);
        断言(n2 == 0 && StateMachine.CurrentState == StateMachine.Think,
            $"B2 未知状态被拒绝且状态不变（日志：{log2[0]}）");

        // B4 play_anim 不存在
        var (_, c4) = PetCommands.解析("```pet\n{\"cmd\":\"play_anim\",\"anim\":\"rm -rf /\"}\n```");
        var n4 = PetCommands.执行(c4, out var log4);
        断言(n4 == 0, $"B4 未知动画被拒绝（日志：{log4[0]}）");

        // B5 speak 带 BBCode 注入 → 转义
        var (_, c5) = PetCommands.解析("```pet\n{\"cmd\":\"speak\",\"text\":\"[img]http://x/y.png[/img]喵\"}\n```");
        var n5 = PetCommands.执行(c5);
        _待查气泡 = true;
        断言(n5 == 1, "B5 speak 执行成功");

        // B6 speak 超长
        var 长 = new string('喵', 300);
        var (_, c6) = PetCommands.解析($"```pet\n{{\"cmd\":\"speak\",\"text\":\"{长}\"}}\n```");
        var n6 = PetCommands.执行(c6, out var log6);
        断言(n6 == 0 && log6[0].Contains("过长"), $"B6 超长文本被拒绝（日志：{log6[0]}）");

        // B7 set_mode
        var (_, c7) = PetCommands.解析("```pet\n{\"cmd\":\"set_mode\",\"mode\":\"game\"}\n```");
        var n7 = PetCommands.执行(c7);
        断言(n7 == 1 && ModeManager.CurrentMode == ModeManager.Mode.Game, "B7 set_mode=game 切换成功");
        ModeManager.SwitchMode(ModeManager.Mode.Office);

        // B8 queue_chain
        var (_, c8) = PetCommands.解析("```pet\n{\"cmd\":\"queue_chain\",\"steps\":\"think:1,idle:1\"}\n```");
        var n8 = PetCommands.执行(c8);
        断言(n8 == 1 && StateMachine.CurrentState == StateMachine.Think, "B8 queue_chain 入队并开始执行");

        // B9 queue_chain 链内非法状态
        var (_, c9) = PetCommands.解析("```pet\n{\"cmd\":\"queue_chain\",\"steps\":\"think:1,hack:1\"}\n```");
        var n9 = PetCommands.执行(c9, out var log9);
        断言(n9 == 0, $"B9 链内未知状态被拒绝（日志：{log9[0]}）");

        // B10 越权命令（典型注入）
        var (_, c10) = PetCommands.解析("```pet\n{\"cmd\":\"run_shell\",\"cmdline\":\"rm -rf /\"}\n{\"cmd\":\"set_state\",\"state\":\"speak\"}\n```");
        var n10 = PetCommands.执行(c10, out var log10);
        断言(n10 == 1 && log10[0].StartsWith("✗ 不在白名单"), $"B10 非白名单命令被拒绝，同块合法命令仍执行（{log10[0]}）");

        // B11 每轮上限
        var 多 = "```pet\n";
        for (var i = 0; i < 8; i++) 多 += "{\"cmd\":\"set_state\",\"state\":\"idle\"}\n";
        多 += "```";
        var (_, c11) = PetCommands.解析(多);
        var n11 = PetCommands.执行(c11, out var log11);
        断言(n11 == 6 && log11.Count == 8, $"B11 每轮上限 6 条生效（成功 {n11}，日志 {log11.Count} 条）");

        // B12 已登记但未实现（不假装成功）
        var (_, c12) = PetCommands.解析("```pet\n{\"cmd\":\"soul_set\",\"key\":\"mood\",\"value\":\"happy\"}\n```");
        var n12 = PetCommands.执行(c12, out var log12);
        断言(n12 == 0 && log12[0].Contains("未实现"), $"B12 soul_set 记为「未实现」而非成功（set_mood 已实现，见 StatsProbe；{log12[0]}）");

        // B13 激进开关默认关：open_url 被拒
        AgentBridge.Options.AggressiveMode = false;
        var (_, c13) = PetCommands.解析("```pet\n{\"cmd\":\"open_url\",\"url\":\"https://example.com\"}\n```");
        var n13 = PetCommands.执行(c13, out var log13);
        断言(n13 == 0 && log13[0].Contains("不在白名单"), $"B13 默认保守：open_url 被拒（{log13[0]}）");

        // B14 激进开关打开后，非 http(s) 仍被拒（不真的打开浏览器）
        AgentBridge.Options.AggressiveMode = true;
        var (_, c14) = PetCommands.解析("```pet\n{\"cmd\":\"open_url\",\"url\":\"file:///C:/Windows/system.ini\"}\n```");
        var n14 = PetCommands.执行(c14, out var log14);
        断言(n14 == 0 && log14[0].Contains("只允许 http/https"), $"B14 激进模式下非 http(s) 仍被拒（{log14[0]}）");
        AgentBridge.Options.AggressiveMode = false;

        // B3 play_anim 合法 —— **必须放在 B 组最末**：状态机是动画的「所有者」，
        // 之后若再有任何 set_state 切换，都会合法地把 walk-left 顶掉（这条踩过一次）。
        StateMachine.SetState(StateMachine.Sleep); // sleep 池是循环动画 → 不产生「播完」事件 → 不会触发重播覆盖
        var (_, c3) = PetCommands.解析("```pet\n{\"cmd\":\"play_anim\",\"anim\":\"walk-left\"}\n```");
        var n3 = PetCommands.执行(c3);
        断言(n3 == 1, "B3a play_anim=walk-left 通过白名单与存在性校验");
        _待查动画 = "walk-left";
    }

    private string _待查动画;
    private bool _待查气泡;

    public override void _Process(double delta)
    {
        _帧++;

        if (_帧 == 5) { 解析组(); 执行组(); }
        else if (_帧 == 12)
        {
            // 延迟生效的断言（PlayNamed / 气泡走 CallDeferred，要下一帧才可见）
            if (_待查动画 != null)
            {
                断言(CharAnim.当前动画名_只读 == _待查动画,
                    $"B3b play_anim=walk-left 真的播了（实际 {CharAnim.当前动画名_只读}）");
                _待查动画 = null;
            }
            if (_待查气泡)
            {
                var t = Dialogue.探针_当前标题;
                断言(t.Contains("喵") && !t.Contains("["), $"B5 气泡文本已被转义（实际「{t}」）");
                _待查气泡 = false;
            }
            StateMachine.SetState(StateMachine.Idle);
        }
        else if (_帧 == 14)
        {
            // C 组：逐块送入，第 2 块正好把围栏拆开（跨块拼接是流式下最容易翻车的地方）
            ChatBox.流式追加("好的，我看看喵~");
            ChatBox.流式追加("\n```pe");
            ChatBox.流式追加("t\n{\"cmd\":\"speak\",\"text\":\"这是一条不该出现的");
            ChatBox.流式追加("指令\"}\n``");
            ChatBox.流式追加("`\n看完了！");
        }
        else if (_帧 == 18)
        {
            // 注意：流式追加内部走 CallDeferred，必须等下一帧才能读显示文本（否则测的是空串＝假通过）
            var 显示 = ChatBox.探针_显示文本;
            GD.Print($"[CP] 流式显示原文 = 「{显示}」");
            断言(显示.Contains("好的") && 显示.Contains("看完了"), "C1 流式过程中文字正常显示");
            断言(!显示.Contains("pet") && !显示.Contains("cmd") && !显示.Contains("不该出现"),
                "C2 跨块拼接的围栏块在流式过程中也从未显示");
            ChatBox.结束流式();
        }
        else if (_帧 == 22)
        {
            var 历史 = ChatBox.探针_历史文本;
            GD.Print($"[CP] 提交后历史 = 「{历史}」");
            断言(历史.Contains("好的") && 历史.Contains("看完了") && !历史.Contains("pet") && !历史.Contains("指令"),
                "C3 落进历史的是干净文本（无围栏块、无指令内容）");
        }
        else if (_帧 == 26)
        {
            // C4 完整链路：整段回复（含围栏）交给 AgentBridge 的处理入口
            StateMachine.SetState(StateMachine.Idle);
            var 干净 = PetCommands.处理回复("在的喵~\n```pet\n{\"cmd\":\"set_state\",\"state\":\"working\"}\n```\n马上开始");
            断言(干净.Contains("在的") && 干净.Contains("马上开始") && !干净.Contains("pet"),
                "C4 处理回复() 返回干净文本");
            断言(StateMachine.CurrentState == StateMachine.Working, "C4 同一次调用里指令已执行（state=working）");
        }
        else if (_帧 == 32)
        {
            StateMachine.SetState(StateMachine.Idle);
            GD.Print($"[CP] ===== 失败数 = {_失败} =====");
            GD.Print(_失败 == 0 ? "[CP] PASS" : "[CP] FAIL");
            GetTree().Quit(_失败 == 0 ? 0 : 1);
        }
        else if (_帧 > 600)
        {
            GD.PrintErr("[CP] 超时 FAIL");
            GetTree().Quit(2);
        }
    }
}