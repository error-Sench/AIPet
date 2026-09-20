using System;
using desktop.script.logic;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 爬边探针（headless，组②）：纯函数几何 + 相位机全流程（用「位置/屏幕/尺寸覆盖」注入假窗口，headless 可跑）。
/// 流程：近边触发 → 上墙 A → 吸附（核对挂边 X）→ 垂直爬（加速）→ 顶爬 → 对侧下爬 → 下落 → 落地 → idle。
/// 用法：Godot_v4.7.2-stable_mono_win64_console.exe --headless --path D:/Games/Github/AIPet res://tests/ClimbProbe.tscn
/// </summary>
public partial class ClimbProbe : Node
{
    private static readonly Rect2I 屏幕 = new(0, 0, 1920, 1040);
    private static readonly Vector2I 尺寸 = new(256, 256);

    private int _帧;
    private int _失败;
    private int _步;

    public override void _Ready()
    {
        Main.探针_禁首启提示 = true;
        MusicSense.启用 = false;   // 组③：隔离音乐反应（系统有声就跳舞会顶状态）
        StateMachine.设置.问候启用 = false;
        DailyRoutine.问候启用 = false;
        StateMachine.设置.磁盘提醒启用 = false;
        DailyRoutine.磁盘提醒启用 = false;
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== ClimbProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[CL] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[CL] FAIL  {描述}"); }
    }

    private void 结束()
    {
        Climb.探针_重置();
        GD.Print($"[CL] ===== 失败数 = {_失败} =====（步 {_步}/帧 {_帧}）");
        GD.Print(_失败 == 0 ? "[CL] PASS" : "[CL] FAIL");
        GetTree().Quit(_失败 == 0 ? 0 : 1);
    }

    public override void _Process(double delta)
    {
        _帧++;
        if (_帧 > 900) { 断言(false, $"超时（步 {_步} 卡住，相 {Climb.探针_相}）"); 结束(); return; }
        var 动画 = CharAnim.当前动画名_只读;
        var 相 = Climb.探针_相;

        switch (_步)
        {
            case 0 when _帧 >= 4:
                StateMachine.入场完成();
                // —— 纯函数：挂边/顶挂/落地/走到位/最近侧（字面量期望值） ——
                断言(Climb.挂边位置X(Climb.侧.左, 屏幕, 256, 0.52f, 0) == -123,
                    $"挂边位置X 左 = -123（实际 {Climb.挂边位置X(Climb.侧.左, 屏幕, 256, 0.52f, 0)}）");
                断言(Climb.挂边位置X(Climb.侧.右, 屏幕, 256, 0.52f, 0) == 1920 - 133,
                    $"挂边位置X 右 = 1787（实际 {Climb.挂边位置X(Climb.侧.右, 屏幕, 256, 0.52f, 0)}）");
                断言(Climb.挂边位置X(Climb.侧.左, 屏幕, 256, 0.52f, 10) == -113 && Climb.挂边位置X(Climb.侧.右, 屏幕, 256, 0.52f, 10) == 1777,
                    "偏移：左 +10 → -113 / 右 +10 → 1777（正=往屏内推）");
                断言(Climb.顶挂位置Y(屏幕, 256, 0.55f, 0) == -(256 - 140), $"顶挂位置Y = -116（实际 {Climb.顶挂位置Y(屏幕, 256, 0.55f, 0)}）");
                断言(Climb.落地Y(屏幕, 256, 6) == 790, $"落地Y = 790（实际 {Climb.落地Y(屏幕, 256, 6)}）");
                断言(Climb.走到位置X(Climb.侧.左, 屏幕, 256, 0.30f) == 76 && Climb.走到位置X(Climb.侧.右, 屏幕, 256, 0.30f) == 1588,
                    "走到位 X：左 76 / 右 1588");
                断言(Climb.最近侧(屏幕, 200) == Climb.侧.左 && Climb.最近侧(屏幕, 1700) == Climb.侧.右 && Climb.最近侧(屏幕, 960) == Climb.侧.左,
                    "最近侧（含正中对半：960 → 左）");
                // —— 循环模式（组②素材；游戏模式空中段共用 fall-B） ——
                断言(CharAnim.动画循环_只读("fall-left-b") && CharAnim.动画循环_只读("fall-right-b"),
                    "fall 的 -b（下落）按循环加载");
                断言(!CharAnim.动画循环_只读("fall-left-a") && !CharAnim.动画循环_只读("fall-left-c"),
                    "fall 的 -a/-c 段不循环（段推进靠播完信号）");
                // —— 覆盖 + 触发 ——
                Climb.探针_位置覆盖 = new Vector2I(80, 500);   // 近左边（走到位 76，差 4 ≤ 8 → 直接上墙）
                Climb.探针_屏幕覆盖 = 屏幕;
                Climb.探针_尺寸覆盖 = 尺寸;
                Climb.探针_设冷却(0f);
                断言(Climb.可触发(), "可触发（屏内 + 上方有空间 + 素材在）");
                Climb.探针_设冷却(100f);
                断言(!Climb.可触发(), "冷却中不可触发");
                Climb.探针_设冷却(0f);
                Climb.探针_位置覆盖 = new Vector2I(800, 80);
                断言(!Climb.可触发(), "太靠顶（上方空间 < 150）不可触发");
                Climb.探针_位置覆盖 = new Vector2I(80, 500);
                Climb.开始();
                断言(Climb.探针_相 == Climb.相.侧爬, $"近边直接上墙（相 {Climb.探针_相}）");
                _步 = 1;
                break;

            case 1 when 相 == Climb.相.侧爬 && 动画 == "climb-left-a":
                断言(StateMachine.CurrentState == StateMachine.ClimbState, $"爬边状态机 = climb（实际 {StateMachine.CurrentState}）");
                StateMachine.重播当前状态();   // 模拟 A 段播完
                _步 = 2;
                break;

            case 2 when 动画 == "climb-left-b":
                断言(Climb.探针_位置覆盖!.Value.X == -123, $"吸附：X 推出屏外到 -123（实际 {Climb.探针_位置覆盖!.Value.X}）");
                断言(Climb.探针_吸附完成, "吸附完成标记");
                Climb.速度侧爬 = 3000f;   // 加速过相位（探针提速）
                _步 = 3;
                break;

            case 3 when 相 == Climb.相.顶爬:
                _步 = 4;   // 下一帧动画才切
                break;

            case 4 when 相 == Climb.相.顶爬 && 动画.StartsWith("climb_top-"):
                断言(动画 == "climb_top-right-a", $"从左上来 → 顶爬向右 A 段（实际 {动画}）");
                断言(Climb.探针_位置覆盖!.Value.Y == -116, $"顶挂吸附：Y = -116（实际 {Climb.探针_位置覆盖!.Value.Y}）");
                StateMachine.重播当前状态();
                Climb.速度顶爬 = 3000f;
                _步 = 5;
                break;

            case 5 when 动画 == "climb_top-right-b" && 相 == Climb.相.顶爬:
                _步 = 6;   // 顶爬行进中，等它到右端
                break;

            case 6 when 相 == Climb.相.侧爬 && Climb.探针_侧 == Climb.侧.右:
                断言(Climb.探针_爬方向 == 1, "对侧转下爬（方向 +1 = 向下）");
                断言(Climb.探针_位置覆盖!.Value.X == 1787, $"右下挂位 X = 1787（实际 {Climb.探针_位置覆盖!.Value.X}）");
                Climb.速度侧爬 = 3000f;
                _步 = 7;
                break;

            case 7 when 动画 == "climb-right-b" && 相 == Climb.相.侧爬:
                _步 = 8;   // 下爬中，等近底转下落
                break;

            case 8 when 相 == Climb.相.下落:
                _步 = 9;
                break;

            case 9 when 相 == Climb.相.下落 && 动画 == "fall-right-a":
                断言(true, $"转下落：A 段 fall-right-a（实际 {动画}）");
                StateMachine.重播当前状态();
                Climb.掉落初速 = 1500f;
                Climb.掉落加速度 = 6000f;
                _步 = 10;
                break;

            case 10 when 动画 == "fall-right-b" && 相 == Climb.相.下落:
                _步 = 11;   // 下落中
                break;

            case 11 when 相 == Climb.相.落地:
                _步 = 12;
                break;

            case 12 when 相 == Climb.相.落地 && 动画 == "fall-right-c":
                断言(Climb.探针_位置覆盖!.Value.Y == 790, $"落地：脚踩屏底 Y = 790（实际 {Climb.探针_位置覆盖!.Value.Y}）");
                StateMachine.重播当前状态();   // 模拟 C 段播完
                _步 = 13;
                break;

            case 13 when 相 == Climb.相.无:
                断言(StateMachine.CurrentState == StateMachine.Idle, $"爬完回 idle（实际 {StateMachine.CurrentState}）");
                断言(Climb.探针_冷却 > 0f, "爬完进冷却");
                // —— 让位：爬半路被接管 → 窗口拉回屏内 ——
                Climb.探针_位置覆盖 = new Vector2I(-500, 500);   // 假装挂在屏外
                Climb.探针_到达边缘(Climb.侧.左);
                断言(Climb.探针_相 == Climb.相.侧爬, "让位前置：重新挂上左墙");
                StateMachine.SetState(StateMachine.Idle);   // 别人抢状态 → 应触发 Climb.让位
                断言(Climb.探针_相 == Climb.相.无, $"让位后相位清空（相 {Climb.探针_相}）");
                断言(Climb.探针_位置覆盖!.Value.X == 0, $"让位：屏外 X 拉回屏内 0（实际 {Climb.探针_位置覆盖!.Value.X}）");
                _步 = 14;
                break;

            case 14:
                结束();
                break;
        }
    }
}
