using System;
using System.Linq;
using desktop.script.Logic;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 智能移动探针（headless，重构#4：抄 VPet 原库的移动方式）。
/// 覆盖：纯函数几何/骰子 + 移动表加载 + 触发/检查语义（近/远距离门）+ 冷却只挡爬边族 +
/// 接力方向评分 + 全流程（上墙 → 吸附 → 爬 → 顶爬 → 角上接力 → 下落 → 落地 → 收势 → idle）。
/// 方法：位置/屏幕/尺寸覆盖注入假窗口 + 逐帧木偶式推进位置（headless 可跑，确定性）。
/// 用法：Godot_v4.7.2-stable_mono_win64_console.exe --headless --path D:/Games/Github/AIPet res://tests/MoveProbe.tscn
/// </summary>
public partial class MoveProbe : Node
{
    private static readonly Rect2I 屏幕 = new(0, 0, 1920, 1040);
    private static readonly Vector2I 尺寸 = new(256, 256);

    private int _帧;
    private int _失败;
    private int _步;

    public override void _Ready()
    {
        Main.探针_禁首启提示 = true;
        // 隔离「时间驱动」（问候/磁盘/音乐）：加载() 会读 behavior.json 覆盖这些开关 →
        // 用冻结开关（否则共享存档下问候气泡会在探针中途抢状态，全量回归断言随顺序抖）
        StateMachine.设置.探针_冻结时间驱动开关 = true;
        MusicSense.启用 = false;   // 组③：隔离音乐反应（系统有声就跳舞会顶状态）
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== MoveProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[MV] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[MV] FAIL  {描述}"); }
    }

    private void 结束()
    {
        MoveRunner.探针_重置();
        GD.Print($"[MV] ===== 失败数 = {_失败} =====（步 {_步}/帧 {_帧}）");
        GD.Print(_失败 == 0 ? "[MV] PASS" : "[MV] FAIL");
        GetTree().Quit(_失败 == 0 ? 0 : 1);
    }

    public override void _Process(double delta)
    {
        _帧++;
        if (_帧 > 900) { 断言(false, $"超时（步 {_步} 卡住，相 {MoveRunner.探针_相}）"); 结束(); return; }
        var 动画 = CharAnim.当前动画名_只读;
        var 相 = MoveRunner.探针_相;

        switch (_步)
        {
            // ============ ① 纯函数 + 表加载 + 触发/检查语义 ============
            case 0 when _帧 >= 4:
                StateMachine.入场完成();
                // —— 几何（字面量期望值，沿用旧 ClimbProbe 的锚点） ——
                断言(MoveRunner.挂边位置X("left", 屏幕, 256, 0.52f, 0) == -123,
                    $"挂边位置X 左 = -123（实际 {MoveRunner.挂边位置X("left", 屏幕, 256, 0.52f, 0)}）");
                断言(MoveRunner.挂边位置X("right", 屏幕, 256, 0.52f, 0) == 1920 - 133,
                    $"挂边位置X 右 = 1787（实际 {MoveRunner.挂边位置X("right", 屏幕, 256, 0.52f, 0)}）");
                断言(MoveRunner.挂边位置X("left", 屏幕, 256, 0.52f, 10) == -113 && MoveRunner.挂边位置X("right", 屏幕, 256, 0.52f, 10) == 1777,
                    "偏移：左 +10 → -113 / 右 +10 → 1777");
                断言(MoveRunner.顶挂位置Y(屏幕, 256, 0.55f, 0) == -(256 - 140), $"顶挂位置Y = -116（实际 {MoveRunner.顶挂位置Y(屏幕, 256, 0.55f, 0)}）");
                断言(MoveRunner.落地Y(屏幕, 256, 6) == 790, $"落地Y = 790（实际 {MoveRunner.落地Y(屏幕, 256, 6)}）");
                // —— 距()：近/远门的原料 ——
                断言(MoveRunner.距("左", new Vector2I(80, 500), 屏幕, 尺寸) == 80
                    && MoveRunner.距("下", new Vector2I(80, 500), 屏幕, 尺寸) == 284,
                    $"距(左)=80 / 距(下)=284（实际 {MoveRunner.距("左", new Vector2I(80, 500), 屏幕, 尺寸)} / {MoveRunner.距("下", new Vector2I(80, 500), 屏幕, 尺寸)}）");
                // —— 骰子：距离骰前 3 圈必继续（Next(k<距离) 恒 < 距离，确定性）——
                var 骰 = new Random(12345);
                断言(MoveRunner.距离续圈(0, 3, 骰) && MoveRunner.距离续圈(1, 3, 骰) && MoveRunner.距离续圈(2, 3, 骰),
                    "距离骰：圈数 0/1/2 < 距离 3 → 必继续");
                MoveRunner.接力概率 = 1f;
                断言(MoveRunner.接力掷骰(骰), "接力骰：概率 1 → 必中");
                MoveRunner.接力概率 = 0f;
                断言(!MoveRunner.接力掷骰(骰), "接力骰：概率 0 → 必不中");
                MoveRunner.接力概率 = 0.8f;
                // —— 表加载 + 档位过滤 ——
                断言(MoveRunner.表.Count >= 16 && MoveRunner.探针_定义("walk-left") != null && MoveRunner.探针_定义("fall-right") != null,
                    $"移动表加载（{MoveRunner.表.Count} 条 ≥ 16，含 walk-left / fall-right）");
                断言(MoveRunner.探针_定义("climb_top-right").吸附 == "top" && MoveRunner.探针_定义("fall-left").重力,
                    "表字段解析：climb_top 吸附=top / fall 重力=true");
                断言(StateMachine.当前情绪档 == "nomal" && MoveRunner.探针_定义("walk-left-slow") != null,
                    "当前情绪档 = nomal（三档默认）");
                // —— 覆盖 + 触发/检查（近/远门） ——
                MoveRunner.探针_位置覆盖 = new Vector2I(40, 500);
                MoveRunner.探针_屏幕覆盖 = 屏幕; MoveRunner.探针_尺寸覆盖 = 尺寸;
                MoveRunner.探针_设冷却(0f);
                断言(MoveRunner.探针_触发("climb-left-up"), "近左触发：climb-left-up（左距 40 ≤ 64 且上方空间 500 ≥ 100）");
                断言(MoveRunner.探针_触发("walk-right"), "远右触发：walk-right（右距 1624 ≥ 100）");
                断言(!MoveRunner.探针_触发("walk-left"), "远左不满足：walk-left 不触发（左距 40 < 100）");
                断言(!MoveRunner.探针_触发("climb-right-up") && !MoveRunner.探针_触发("climb_top-right"), "近右/近上不满足 → 不触发");
                断言(MoveRunner.探针_触发("fall-left"), "fall-left 触发（近左 + 下距 284 ≥ 100，VPet TriggerDown 语义）");
                断言(!MoveRunner.探针_触发("walk-right-slow"), "档位过滤：poor 档的 walk-right-slow 在 nomal 下不触发");
                断言(MoveRunner.探针_检查("climb-left-up"), "检查通过：climb-left-up（上距 500 ≥ 50）");
                // —— 冷却只挡爬边族 ——
                MoveRunner.探针_设冷却(100f);
                var 冷却期候选 = MoveRunner.探针_候选名();
                断言(!冷却期候选.Contains("climb-left-up") && !冷却期候选.Contains("fall-left"),
                    $"冷却期不接爬边族（候选 {string.Join("/", 冷却期候选)}）");
                断言(冷却期候选.Contains("walk-right") && 冷却期候选.Contains("crawl-right"), "冷却期走/趴照常可跑");
                MoveRunner.探针_设冷却(0f);
                // —— 兼容接力方向评分（VPet GetCompatibilityMove：同向 +1 / 反向 -1，某轴为 0 不参与）——
                MoveRunner.探针_位置覆盖 = new Vector2I(40, 40);
                MoveRunner.探针_开始("climb-left-up");
                var 兼容 = MoveRunner.探针_兼容名();
                断言(兼容.Contains("climb_top-right") && 兼容.Contains("walk-right"),
                    $"兼容含 0 分项（爬左上的 y- 轴对横爬的 x± 不参与）：{string.Join("/", 兼容)}");
                断言(!兼容.Contains("climb-left-down") && !兼容.Contains("fall-left"),
                    "兼容排除反向项（climb-left-down y+ / fall-left y+ 判 -1）");
                断言(!兼容.Contains("climb-left-up"), "兼容排除自己（VPet 未排除，我们有意排除）");
                _步 = 1;
                break;

            // ============ ② 上墙：A 段 → 吸附 → B 循环 ============
            case 1 when 相 == MoveRunner.相.进入 && 动画 == "climb-left-a":
                断言(StateMachine.CurrentState == StateMachine.MoveState, $"移动状态机 = move（实际 {StateMachine.CurrentState}）");
                StateMachine.重播当前状态();   // 模拟 A 段播完
                _步 = 2;
                break;

            case 2 when 动画 == "climb-left-b":
                断言(MoveRunner.探针_位置覆盖!.Value.X == -123, $"吸附：X 推出屏外到 -123（实际 {MoveRunner.探针_位置覆盖!.Value.X}）");
                断言(MoveRunner.探针_吸附完成, "吸附完成标记");
                _步 = 3;
                break;

            // ============ ③ 垂直爬（木偶推进）→ 顶部接力 → 顶爬 ============
            case 3 when 相 == MoveRunner.相.循环:
                {
                    var y = MoveRunner.探针_位置覆盖!.Value.Y;
                    if (y > 40) { MoveRunner.探针_位置覆盖 = new Vector2I(-123, Math.Max(40, y - 60)); return; }
                    // 到头顶：上调一个圈 → 检查(上距 50)不过 → 接力（强制到顶爬）
                    断言(!MoveRunner.探针_检查("climb-left-up"), "到顶：检查(上距 ≥ 50)不过");
                    MoveRunner.探针_接力目标 = "climb_top-right";
                    MoveRunner.探针_圈完成();
                    _步 = 4;
                }
                break;

            case 4 when 动画 == "climb_top-right-a":
                断言(MoveRunner.探针_当前名 == "climb_top-right", $"顶部接力 → 顶爬（实际 {MoveRunner.探针_当前名}）");
                StateMachine.重播当前状态();
                _步 = 5;
                break;

            case 5 when 动画 == "climb_top-right-b":
                断言(MoveRunner.探针_位置覆盖!.Value.Y == -116, $"顶挂吸附：Y = -116（实际 {MoveRunner.探针_位置覆盖!.Value.Y}）");
                _步 = 6;
                break;

            // ============ ④ 顶爬到底 → 角上接力（候选核对）→ 下落 ============
            case 6 when 相 == MoveRunner.相.循环:
                {
                    var x = MoveRunner.探针_位置覆盖!.Value.X;
                    if (x < 1640) { MoveRunner.探针_位置覆盖 = new Vector2I(Math.Min(1640, x + 140), -116); return; }
                    断言(!MoveRunner.探针_检查("climb_top-right"), "到右端：检查(右距 ≥ 50)不过");
                    var 角候选 = MoveRunner.探针_兼容名();
                    断言(角候选.Contains("fall-right") && 角候选.Contains("climb-right-down") && !角候选.Contains("walk-left"),
                        $"右上角兼容候选 = 下爬/下落，同向评分排除 walk-left（{string.Join("/", 角候选)}）");
                    MoveRunner.探针_接力目标 = "fall-right";
                    MoveRunner.探针_圈完成();
                    _步 = 7;
                }
                break;

            case 7:
                if (动画 == "fall-right-a") _步 = 8;
                else if (相 == MoveRunner.相.无) { 断言(false, "角上接力应接 fall-right（相已清空）"); 结束(); }
                break;

            case 8 when 动画 == "fall-right-a":
                断言(MoveRunner.探针_当前名 == "fall-right", $"角上接力 → 下落（实际 {MoveRunner.探针_当前名}）");
                StateMachine.重播当前状态();
                _步 = 9;
                break;

            // ============ ⑤ 下落 → 触地 → 收势 C → idle + 冷却 ============
            case 9 when 动画 == "fall-right-b":
                断言(MoveRunner.探针_位置覆盖!.Value.Y <= 500, $"下落中（Y {MoveRunner.探针_位置覆盖!.Value.Y}）");
                // 木偶推到地面：下一帧 推进 会夹到落地线并触发「触地 → 圈完成 → 停/接力」
                MoveRunner.探针_位置覆盖 = new Vector2I(1787, 780);
                _步 = 10;
                break;

            case 10 when 动画 == "fall-right-c":
                断言(MoveRunner.探针_位置覆盖!.Value.Y == 790, $"落地：脚踩屏底 Y = 790（实际 {MoveRunner.探针_位置覆盖!.Value.Y}）");
                断言(MoveRunner.探针_位置覆盖!.Value.X == 1664, $"回位：右侧推出 123px → 拉回贴边 X = 1664（实际 {MoveRunner.探针_位置覆盖!.Value.X}）");
                StateMachine.重播当前状态();   // 模拟 C 段播完
                _步 = 11;
                break;

            case 11 when 相 == MoveRunner.相.无:
                断言(StateMachine.CurrentState == StateMachine.Idle, $"收步回 idle（实际 {StateMachine.CurrentState}）");
                断言(MoveRunner.探针_冷却 > 0f, $"落地进冷却（{MoveRunner.探针_冷却:0}s）");
                _步 = 12;
                break;

            // ============ ⑥ 下爬 junction（近底 240）→ 下落 → 落地 ============
            case 12:
                MoveRunner.探针_设冷却(0f);
                MoveRunner.探针_位置覆盖 = new Vector2I(1787, -116);
                MoveRunner.探针_接力目标 = null;
                MoveRunner.探针_开始("climb-right-down");
                _步 = 13;
                break;

            case 13 when 相 == MoveRunner.相.进入 && 动画 == "climb-right-a":
                StateMachine.重播当前状态();   // A 段播完 → 吸附右墙
                _步 = 14;
                break;

            case 14 when 动画 == "climb-right-b":
                断言(MoveRunner.探针_位置覆盖!.Value.X == 1787, $"右下挂位 X = 1787（实际 {MoveRunner.探针_位置覆盖!.Value.X}）");
                _步 = 15;
                break;

            case 15 when 相 == MoveRunner.相.循环:
                {
                    var y = MoveRunner.探针_位置覆盖!.Value.Y;
                    if (y < 584) { MoveRunner.探针_位置覆盖 = new Vector2I(1787, Math.Min(584, y + 100)); return; }
                    // 近底（下距 200 ∈ [100,240) 的 junction 带）：检查不过但下落仍可触发
                    断言(!MoveRunner.探针_检查("climb-right-down"), "近底：检查(下距 ≥ 240)不过");
                    var 底候选 = MoveRunner.探针_兼容名();
                    断言(底候选.Contains("fall-right"), $"近底兼容候选含下落（{string.Join("/", 底候选)}）");
                    MoveRunner.探针_接力目标 = "fall-right";
                    MoveRunner.探针_圈完成();
                    _步 = 16;
                }
                break;

            case 16:
                if (动画 == "fall-right-a") { 断言(MoveRunner.探针_当前名 == "fall-right", $"近底接力 → 下落（实际 {MoveRunner.探针_当前名}）"); StateMachine.重播当前状态(); _步 = 17; }
                else if (相 == MoveRunner.相.无) { 断言(false, "近底接力应接 fall-right（相已清空）"); 结束(); }
                break;

            case 17 when 动画 == "fall-right-b":
                MoveRunner.探针_位置覆盖 = new Vector2I(1787, 780);
                _步 = 18;
                break;

            case 18 when 动画 == "fall-right-c":
                StateMachine.重播当前状态();
                _步 = 19;
                break;

            case 19 when 相 == MoveRunner.相.无:
                断言(StateMachine.CurrentState == StateMachine.Idle, "第二段落地下落收步回 idle");
                // ============ ⑦ 让位：挂在墙上被接管 → 窗口拉回屏内 ============
                MoveRunner.探针_设冷却(0f);
                MoveRunner.探针_位置覆盖 = new Vector2I(40, 500);
                MoveRunner.探针_开始("climb-left-up");
                _步 = 20;
                break;

            case 20 when 动画 == "climb-left-a":
                StateMachine.重播当前状态();
                _步 = 21;
                break;

            case 21 when 动画 == "climb-left-b":
                MoveRunner.探针_位置覆盖 = new Vector2I(-500, 500);   // 假装挂在屏外
                StateMachine.SetState(StateMachine.Idle);             // 别人抢状态 → 应触发 让位
                断言(MoveRunner.探针_相 == MoveRunner.相.无, $"让位后相位清空（相 {MoveRunner.探针_相}）");
                断言(MoveRunner.探针_位置覆盖!.Value.X == 0, $"让位：屏外 X 拉回屏内 0（实际 {MoveRunner.探针_位置覆盖!.Value.X}）");
                _步 = 22;
                break;

            case 22:
                结束();
                break;
        }
    }
}
