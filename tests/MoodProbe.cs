using desktop.script.Soul;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 情绪表达探针（headless，P5 第二切片）：数值真的**驱动表现与行为**，不只是躺在文件里。
/// 验证三件事：
///  A. 情绪变体：心情好 → 播 `think-happy`；心情糟 → 播 `think-poor`；中间 → 池内随机（不硬造）
///  B. 行为耦合：心情低落 → 走动间隔 ×1.6；精力不济 → 睡眠阈值提前
///  C. 降级安全：没有该变体的池（say）不许崩，仍回退随机
/// 用法：Godot_..._console.exe --headless --path D:/Games/Github/AIPet res://tests/MoodProbe.tscn
/// </summary>
public partial class MoodProbe : Node
{
    private int _帧;
    private int _失败;
    private float _原心情, _原精力, _原亲密;
    private string _待查;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        _原心情 = StatsTable.当前心情; _原精力 = StatsTable.当前精力; _原亲密 = StatsTable.当前亲密;
        GD.Print("=== MoodProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[MP] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[MP] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        if (_帧 == 5) { B组(); 步骤 = 0; return; }

        // 步进：每 7 帧走一步（PlayNamed 走 CallDeferred，必须等下一帧才读得到动画名）
        if (_帧 < 12 || (_帧 - 12) % 7 != 0) { if (_帧 > 600) 超时(); return; }
        switch (步骤++)
        {
            case 0:
                StatsTable.探针_设值(90f, 80f, 0f);
                StateMachine.SetState(StateMachine.Idle);
                StateMachine.SetState(StateMachine.Think);
                break;
            case 1:
                断言(CharAnim.当前动画名_只读 == "think-happy",
                    $"A1 心情 90 → 思考播 think-happy（实际 {CharAnim.当前动画名_只读}）");
                StatsTable.探针_设值(20f, 80f, 0f);
                StateMachine.SetState(StateMachine.Idle);
                StateMachine.SetState(StateMachine.Think);
                break;
            case 2:
                断言(CharAnim.当前动画名_只读 == "think-poor",
                    $"A2 心情 20 → 思考播 think-poor（实际 {CharAnim.当前动画名_只读}）");
                StatsTable.探针_设值(50f, 80f, 0f);
                StateMachine.SetState(StateMachine.Idle);
                StateMachine.SetState(StateMachine.Think);
                break;
            case 3:
                var 名 = CharAnim.当前动画名_只读;
                断言(名.StartsWith("think-"),
                    $"A3 心情中间值 → 用池内随机（实际 {名}，不做硬造情绪）");
                StatsTable.探针_设值(95f, 80f, 0f); // say 池没有 happy 变体 → 必须安全退回
                StateMachine.SetState(StateMachine.Idle);
                StateMachine.SetState(StateMachine.Speak);
                break;
            case 4:
                var 说 = CharAnim.当前动画名_只读;
                断言(说.StartsWith("say-"),
                    $"C1 say 池无 happy 变体 → 安全回退池内随机（实际 {说}）");
                StatsTable.探针_设值(_原心情, _原精力, _原亲密);
                StateMachine.SetState(StateMachine.Idle);
                GD.Print($"[MP] 已恢复数值 → {StatsTable.概述}");
                GD.Print($"[MP] ===== 失败数 = {_失败} =====");
                GD.Print(_失败 == 0 ? "[MP] PASS" : "[MP] FAIL");
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                break;
        }
    }

    private int 步骤;

    private void 超时()
    {
        GD.PrintErr("[MP] 超时 FAIL");
        StatsTable.探针_设值(_原心情, _原精力, _原亲密);
        GetTree().Quit(2);
    }

    private void B组()
    {
        GD.Print("--- B 组：行为耦合（纯函数，直接断言） ---");
        StatsTable.探针_设值(80f, 80f, 0f);
        断言(Mathf.IsEqualApprox(StateMachine.走动间隔倍率, 1f), "B1 心情正常 → 走动间隔倍率 1.0");
        断言(Mathf.IsEqualApprox(StateMachine.有效睡眠空闲秒, StateMachine.设置.睡眠空闲秒),
            $"B1 心情正常 → 睡眠阈值不变（{StateMachine.有效睡眠空闲秒:0}s）");

        StatsTable.探针_设值(20f, 80f, 0f);
        断言(StateMachine.走动间隔倍率 > 1.5f, $"B2 心情低落 → 走动间隔倍率 {StateMachine.走动间隔倍率}（少乱跑）");

        StatsTable.探针_设值(80f, 10f, 0f);
        断言(StateMachine.有效睡眠空闲秒 <= 120f, $"B3 精力不济 → 睡眠阈值提前到 {StateMachine.有效睡眠空闲秒:0}s");
        GD.Print("--- A 组：情绪变体 ---");
    }
}