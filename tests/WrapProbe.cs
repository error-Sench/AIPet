using System;
using desktop.script.logic;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 包裹段探针（headless，组①）：验证 think / sleep / 说话 / 干活类的「进入 A → 循环 B → 退出 C」会话机制——
/// ① 进入播 A 段；② 播完接主段且变体钉死（循环不换）；③ 退出先播 C、状态延迟落地；④ 硬接管（拖拽）跳过 C 立刻生效。
/// 用 重播当前状态() 模拟「动画播完」（不靠实时）。用法：
/// Godot_v4.7.2-stable_mono_win64_console.exe --headless --path D:/Games/Github/AIPet res://tests/WrapProbe.tscn
/// </summary>
public partial class WrapProbe : Node
{
    private int _帧;
    private int _失败;
    private string _think主名 = "";
    private string _sleep主名 = "";
    private string _say主名 = "";
    private string _work主名 = "";

    private static bool 结束段(string 名) => 名.EndsWith("-a", StringComparison.Ordinal) || 名.EndsWith("-c", StringComparison.Ordinal);

    public override void _Ready()
    {
        Main.探针_禁首启提示 = true;                       // 与首启提示互不打扰
        MusicSense.启用 = false;                           // 组③：隔离音乐反应（系统有声就跳舞会顶状态）
        StateMachine.设置.问候启用 = false;                 // 隔离时间驱动（同 StateProbe 口径）
        DailyRoutine.问候启用 = false;
        StateMachine.设置.磁盘提醒启用 = false;
        DailyRoutine.磁盘提醒启用 = false;
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== WrapProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[WRAP] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[WRAP] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        var 动画 = CharAnim.当前动画名_只读;
        switch (_帧)
        {
            case 4:
                StateMachine.入场完成();   // 解除入场门（包裹机制与入场无关，但让状态可控）
                断言(!StateMachine.入场未完成_只读, "入场门已解除");
                break;

            // ── think：进入 A → 主段钉死 → 退出 C → 延迟落地 ──
            case 8:
                StateMachine.SetState(StateMachine.Think);
                break;
            case 10:
                断言(StateMachine.CurrentState == StateMachine.Think, $"think 进入（当前 {StateMachine.CurrentState}）");
                断言(StateMachine.包裹中, "think 进入后包裹会话建立");
                断言(动画.StartsWith("think-") && 结束段(动画), $"进入段先播 A（实际 {动画}）");
                // 加载模式：A/C 段必须非循环（循环动画不回「播完」→ 段推进卡死）；主段照旧循环
                断言(!CharAnim.动画循环_只读("sleep-a") && !CharAnim.动画循环_只读("sleep-c") && CharAnim.动画循环_只读("sleep-loop"),
                    "sleep A/C 段非循环加载、主段循环加载");
                break;
            case 12:
                StateMachine.重播当前状态();   // 模拟 A 播完
                break;
            case 14:
                _think主名 = 动画;
                断言(动画.StartsWith("think-") && !结束段(动画), $"A 播完接主段（实际 {动画}）");
                break;
            case 16:
                StateMachine.重播当前状态();   // 模拟主段播完（循环）
                break;
            case 18:
                断言(动画 == _think主名, $"主段循环钉死变体不换（{_think主名} → {动画}）");
                break;
            case 20:
                StateMachine.SetState(StateMachine.Idle);   // 软退出 → 应播 C、状态延迟落地
                break;
            case 22:
                断言(StateMachine.CurrentState == StateMachine.Think, $"退出段期间状态保持 think（当前 {StateMachine.CurrentState}）");
                断言(动画.EndsWith("-c", StringComparison.Ordinal), $"退出先播 C（实际 {动画}）");
                break;
            case 24:
                StateMachine.重播当前状态();   // 模拟 C 播完 → 落地
                break;
            case 26:
                断言(StateMachine.CurrentState == StateMachine.Idle, $"C 播完延迟落地 idle（当前 {StateMachine.CurrentState}）");
                断言(!StateMachine.包裹中, "落地后包裹会话结束");
                break;

            // ── sleep：A/C 用池级回退段名（sleep-a / sleep-c）──
            case 28:
                StateMachine.SetState(StateMachine.Sleep);
                break;
            case 30:
                断言(StateMachine.CurrentState == StateMachine.Sleep, $"sleep 进入（当前 {StateMachine.CurrentState}）");
                断言(动画.StartsWith("sleep-") && 结束段(动画), $"睡眠进入段（实际 {动画}）");
                break;
            case 32:
                StateMachine.重播当前状态();
                break;
            case 34:
                _sleep主名 = 动画;
                断言(动画.StartsWith("sleep-") && !结束段(动画), $"睡眠主段不是 A/C 段（实际 {动画}）");
                break;
            case 36:
                StateMachine.SetState(StateMachine.Greet);   // 醒来去打招呼（非 idle 目标）
                break;
            case 38:
                断言(StateMachine.CurrentState == StateMachine.Sleep, $"睡醒退出段期间状态保持 sleep（当前 {StateMachine.CurrentState}）");
                断言(动画.EndsWith("-c", StringComparison.Ordinal), $"睡醒先播起身 C（实际 {动画}）");
                break;
            case 40:
                StateMachine.重播当前状态();
                break;
            case 42:
                断言(StateMachine.CurrentState == StateMachine.Greet, $"起身完落地目标 greet（当前 {StateMachine.CurrentState}）");
                break;

            // ── 说话（气泡，非锁定态）：包裹中让重播接管段推进 ──
            case 44:
                StateMachine.SetState(StateMachine.BubbleTalk, 99f);   // 时长拉长：不让定时回 idle 干扰
                break;
            case 46:
                断言(StateMachine.CurrentState == StateMachine.BubbleTalk, $"气泡说话进入（当前 {StateMachine.CurrentState}）");
                断言(动画.StartsWith("say-") && 结束段(动画), $"说话进入段先播 A（实际 {动画}）");
                break;
            case 48:
                StateMachine.重播当前状态();
                break;
            case 50:
                _say主名 = 动画;
                断言(动画.StartsWith("say-") && !结束段(动画), $"说话主段不是 A/C 段（实际 {动画}）");
                break;
            case 52:
                StateMachine.SetState(StateMachine.Idle);
                break;
            case 54:
                断言(StateMachine.CurrentState == StateMachine.BubbleTalk, $"说话退出段期间状态保持（当前 {StateMachine.CurrentState}）");
                断言(动画.EndsWith("-c", StringComparison.Ordinal), $"说话退出先播 C（实际 {动画}）");
                break;
            case 56:
                StateMachine.重播当前状态();
                break;
            case 58:
                断言(StateMachine.CurrentState == StateMachine.Idle, $"说话 C 播完回 idle（当前 {StateMachine.CurrentState}）");
                break;

            // ── 硬接管：拖拽跳过退出段，立刻生效 ──
            case 60:
                StateMachine.SetState(StateMachine.Think);
                break;
            case 62:
                断言(StateMachine.包裹中, "think 包裹会话（硬接管前置）");
                StateMachine.SetState(StateMachine.Drag);
                break;
            case 64:
                断言(StateMachine.CurrentState == StateMachine.Drag, $"拖拽硬接管立刻生效、不等 C（当前 {StateMachine.CurrentState}）");
                断言(!StateMachine.包裹中, "硬接管后旧包裹会话作废");
                break;
            case 66:
                StateMachine.SetState(StateMachine.Idle);
                break;

            // ── work：干活 = 包裹段（主段 = B 干活循环；A/C = `-a`/`-c` 段）──
            case 68:
                StateMachine.SetState(StateMachine.Working);
                break;
            case 70:
                断言(StateMachine.包裹中, "干活进入开包裹会话");
                断言(动画.StartsWith("work-") && 结束段(动画), $"干活进入播 -a 段（实际 {动画}）");
                StateMachine.重播当前状态();   // 模拟 A 播完
                break;
            case 72:
                _work主名 = 动画;
                断言(动画.StartsWith("work-") && !结束段(动画), $"干活主段 = B 循环（实际 {动画}）");
                StateMachine.重播当前状态();   // 模拟主段播完（循环）
                break;
            case 74:
                断言(动画 == _work主名, $"干活主段钉死不换（{_work主名} → {动画}）");
                StateMachine.SetState(StateMachine.WorkOut);   // 模拟收工 → 应播 -c、延迟落地
                break;
            case 76:
                断言(StateMachine.CurrentState == StateMachine.Working, $"收工先播 -c、状态延迟落地（当前 {StateMachine.CurrentState}）");
                断言(动画 == _work主名 + "-c", $"-c 段名 = 主名 + c（实际 {动画}）");
                StateMachine.重播当前状态();   // 模拟 C 播完 → 落地
                break;
            case 78:
                断言(StateMachine.CurrentState == StateMachine.WorkOut, $"干活退出段落点 WorkOut（当前 {StateMachine.CurrentState}）");
                断言(!StateMachine.包裹中, "落地后包裹会话结束");
                break;
            case 80:
                StateMachine.SetState(StateMachine.Idle);
                break;

            case 90:
                GD.Print($"[WRAP] ===== 失败数 = {_失败} =====（think 主段 {_think主名} / sleep 主段 {_sleep主名} / say 主段 {_say主名} / work 主段 {_work主名}）");
                GD.Print(_失败 == 0 ? "[WRAP] PASS" : "[WRAP] FAIL");
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                break;
        }
    }
}
