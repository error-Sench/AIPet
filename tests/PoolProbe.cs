using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 动画池验证探针（headless）：逐个 SetState，断言实际播出的动画属于**对应池**
/// （即 P2 导入生效、且没有回退到兼容池 fidget/idle/celerate）。
/// 用法：Godot_..._console.exe --headless --path D:/Games/Github/AIPet res://tests/PoolProbe.tscn
/// </summary>
public partial class PoolProbe : Node
{
    private int _帧;
    private int _失败;

    /// <summary>(状态, 期望的动画名前缀, 说明)</summary>
    private static readonly (string 状态, string 期望, string 说明)[] 用例 =
    [
        (StateMachine.Think, "think", "思考 → think 池"),
        (StateMachine.Speak, "say", "说话 → say 池"),
        (StateMachine.Working, "work", "执行 → work 池"),
        (StateMachine.Sleep, "sleep", "休眠 → sleep 池"),
        (StateMachine.Greet, "greet", "打招呼 → greet 池"),
        (StateMachine.Interact, "interact", "被摸 → interact 池"),
        (StateMachine.InteractBody, "interact_body", "摸身体 → interact_body 池（P10）"),
        (StateMachine.Turn, "turn", "被摸转身 → turn 池（P10）"),
        (StateMachine.WorkIn, "switch-up", "开工过渡 → switch-up（P10，VPet Switch_Up）"),
        (StateMachine.WorkOut, "switch-down", "收工过渡 → switch-down（P10，VPet Switch_Down）"),
    ];

    /// <summary>P10 新素材必须真的在池里（缺一个 = 导入或注册漏了；池里少了变体时「池内随机」会悄悄少一种观感）。</summary>
    private static readonly string[] 必存在动画 =
    [
        "say-shy",
        "work-calligraphy", "work-paint", "work-study2", "work-sausage", "work-clean",
        "work-fixmenu", "work-game", "work-water", "work-remove", "work-rope",
        "walk-left-fast", "walk-right-fast", "walk-left-slow", "walk-right-slow",
        "fidget-squat", "fidget-tennis", "fidget-bubbles", "fidget-boring", "fidget-aside",
        "interact_body-a", "interact_body-b", "interact_body-c",
        "turn-a", "turn-b", "turn-c",
        "interact-happy-a", "interact-happy-b", "interact-happy-c",
        "switch-up", "switch-down",
        // 2026-09-20 组①：State 待机变体 / 爱心彩蛋 / 生日三段 / idle 三档（重命名后必须还在）
        "fidget-state-one", "fidget-state-two", "fidget-happy520",
        "idle-happy-1", "idle-nomal-1", "idle-poor-1",
        "bday-a", "bday-b", "bday-c",
        // 2026-09-20 组①·过渡段（包裹段 A/C；挑主名要排除它们、段名解析要能找到它们）
        "think-nomal-a", "think-nomal-c", "think-happy-a", "think-happy-c", "think-poor-a", "think-poor-c",
        "say-smile-a", "say-smile-c", "say-self-a", "say-self-c",
        "say-serious-a", "say-serious-c", "say-shy-a", "say-shy-c",
        "sleep-a", "sleep-c", "sleep-happy-a", "sleep-happy-c",
        // 2026-09-20 组②·爬边（Climb.cs 按名精确播；缺一段就断链）
        "climb-left-a", "climb-left-b", "climb-left-c", "climb-right-a", "climb-right-b", "climb-right-c",
        "climb_top-left-a", "climb_top-left-b", "climb_top-left-c", "climb_top-right-a", "climb_top-right-b", "climb_top-right-c",
        "crawl-left", "crawl-right",
        "fall-left-a", "fall-left-b", "fall-left-c", "fall-right-a", "fall-right-b", "fall-right-c",
    ];

    public override void _Ready()
    {
        StateMachine.探针_禁用包裹 = true;   // 本探针只测「池路由」：包裹段（进入 A / 退出 C）旁路，断言即时播出池内主段
        MusicSense.启用 = false;   // 组③：隔离音乐反应（系统有声就跳舞会顶状态）
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        // 数值已与择档解耦（2026-09-20）：动画变体只由「三档状态」手动开关驱动（默认关），无需钉值隔离。
        GD.Print("=== PoolProbe: 场景已实例化 ===");
    }

    public override void _Process(double delta)
    {
        _帧++;
        if (_帧 == 19)
        {
            // P10 素材核对：全部必须在（缺失即报 FAIL，别让「池里少一种」悄悄溜过）
            var 缺 = new System.Collections.Generic.List<string>();
            foreach (var 名 in 必存在动画) if (!CharAnim.有动画(名)) 缺.Add(名);
            if (缺.Count == 0) GD.Print($"[PL] PASS  P10 新素材齐全（{必存在动画.Length} 个动画）");
            else { _失败++; GD.PrintErr($"[PL] FAIL  缺素材：{string.Join(", ", 缺)}"); }
            return;
        }
        if (_帧 < 20) return;
        var i = (_帧 - 20) / 2;
        if (i >= 用例.Length)
        {
            GD.Print($"[PL] ===== 失败数 = {_失败} =====");
            GD.Print(_失败 == 0 ? "[PL] PASS" : "[PL] FAIL");
            GetTree().Quit(_失败 == 0 ? 0 : 1);
            return;
        }
        var (状态, 期望, 说明) = 用例[i];
        if ((_帧 - 20) % 2 == 0)
        {
            StateMachine.SetState(状态);
        }
        else
        {
            var 实际 = CharAnim.当前动画名_只读;
            var ok = 实际.StartsWith(期望, System.StringComparison.Ordinal);
            if (ok) GD.Print($"[PL] PASS  {说明}: 实际={实际}");
            else { _失败++; GD.PrintErr($"[PL] FAIL  {说明}: 实际={实际}（期望前缀 {期望}）"); }
            StateMachine.SetState(StateMachine.Idle);
        }
    }
}