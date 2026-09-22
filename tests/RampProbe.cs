using System;
using desktop.script.logic;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 爬坡骰子探针（headless，重构#3）：验证自主走动的「概率爬坡」调度（VPet MainLogic.cs:489-494 同款）——
/// ① 窗口公式 max(下限, 周期-连续待机秒)；② 闲置越久命中概率越高（爬坡方向）；
/// ③ 互动后爬坡清零（NotifyInteraction → 刚陪过它不急着乱走）；④ 大样本概率与理论一致。
/// 端到端走位由 WalkProbe（非 headless）覆盖，这里只验骰子数学与清零语义。用法：
/// Godot_v4.7.2-stable_mono_win64_console.exe --headless --path D:/Games/Github/AIPet res://tests/RampProbe.tscn
/// </summary>
public partial class RampProbe : Node
{
    private int _帧;
    private int _失败;

    public override void _Ready()
    {
        Main.探针_禁首启提示 = true;
        MusicSense.启用 = false;
        StateMachine.设置.问候启用 = false;
        DailyRoutine.问候启用 = false;
        StateMachine.设置.磁盘提醒启用 = false;
        DailyRoutine.磁盘提醒启用 = false;
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== RampProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[RAMP] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[RAMP] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 4:
                StateMachine.入场完成();
                // 默认参数：爬坡秒 15 / 周期 200 / 下限 20 / 移动槽 3（与 VPet intercycle 默认一致）
                断言(StateMachine.设置.爬坡周期 == 200 && StateMachine.设置.爬坡下限 == 20 && StateMachine.设置.爬坡移动槽 == 3,
                    $"爬坡参数就位（周期{StateMachine.设置.爬坡周期} 下限{StateMachine.设置.爬坡下限} 槽{StateMachine.设置.爬坡移动槽}）");
                break;

            // ── ① 窗口公式：max(下限, 周期 - 连续待机秒) ──
            case 8:
                断言(StateMachine.爬坡窗口(0) == 200, $"刚互动完窗口=周期（实际 {StateMachine.爬坡窗口(0)}）");
                断言(StateMachine.爬坡窗口(100) == 100, $"闲置 100s 窗口缩到 100（实际 {StateMachine.爬坡窗口(100)}）");
                断言(StateMachine.爬坡窗口(180) == 20, $"闲置 180s 窗口触底=下限 20（实际 {StateMachine.爬坡窗口(180)}）");
                断言(StateMachine.爬坡窗口(5000) == 20, $"再久也不破下限（实际 {StateMachine.爬坡窗口(5000)}）");
                break;

            // ── ②④ 大样本：闲置越久命中率越高，且与理论 p = 槽/窗口 一致 ──
            case 12:
                var 随 = new Random(20260922);
                const int 样本 = 100000;
                int 命中(int 待机秒)
                {
                    var n = 0;
                    for (var i = 0; i < 样本; i++)
                        if (StateMachine.爬坡掷骰(待机秒, 随)) n++;
                    return n;
                }
                var 刚互动 = 命中(0);          // 理论 3/200 = 1.5%
                var 半路 = 命中(100);          // 理论 3/100 = 3%
                var 触底 = 命中(180);          // 理论 3/20 = 15%
                GD.Print($"[RAMP] 命中率：闲置0s={刚互动 * 100.0 / 样本:F2}% 100s={半路 * 100.0 / 样本:F2}% 180s={触底 * 100.0 / 样本:F2}%");
                断言(刚互动 > 样本 * 0.010 && 刚互动 < 样本 * 0.020, $"刚互动完 ≈1.5%（实测 {刚互动 * 100.0 / 样本:F2}%）");
                断言(半路 > 刚互动 * 1.5, $"闲置 100s 命中率明显爬坡（{半路 * 100.0 / 样本:F2}% > 1.5×初始）");
                断言(触底 > 样本 * 0.13 && 触底 < 样本 * 0.17, $"触底 ≈15%（实测 {触底 * 100.0 / 样本:F2}%）");
                break;

            // ── ③ 互动后爬坡清零 ──
            case 16:
                StateMachine.探针_设爬坡待机秒(600f);   // 假装已闲置 10 分钟
                StateMachine.NotifyInteraction("探针");
                断言(StateMachine.探针_爬坡待机秒 == 0f, $"互动后爬坡清零（实际 {StateMachine.探针_爬坡待机秒}）");
                GD.Print($"[RAMP] ===== 失败数 = {_失败} =====");
                GD.Print(_失败 == 0 ? "[RAMP] PASS" : "[RAMP] FAIL");
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                break;
        }
    }
}
