using System.Collections.Generic;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 摸摸反应「完整播放」实证探针（headless）：
/// 断言摸头反应按 **进入(interact-a) → 保持(interact-b) → 退出(interact-c)** 三段完整播放，
/// 播完后自动回待机（而不是在「抱头」姿势上硬切到 idle —— 用户反馈的突兀问题）。
/// （数值已与择档解耦 —— 2026-09-20，无需钉值。）
/// 用法：Godot_..._console.exe --headless --path D:/Games/Github/AIPet res://tests/InteractProbe.tscn
/// </summary>
public partial class InteractProbe : Node
{
    private int _帧;
    private int _失败;
    private readonly List<string> 轨迹 = new();

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== InteractProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[IX] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[IX] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        if (_帧 == 30)
        {
            GD.Print("[IX] 触发摸摸（SetState(interact)）…");
            StateMachine.SetState(StateMachine.Interact);
        }
        else if (_帧 > 30 && StateMachine.CurrentState == StateMachine.Interact)
        {
            var 名 = CharAnim.当前动画名_只读;
            if (!string.IsNullOrEmpty(名) && (轨迹.Count == 0 || 轨迹[^1] != 名)) 
            {
                轨迹.Add(名);
                GD.Print($"[IX] 帧={_帧} 动画={名} 进度={StateMachine.探针_序列进度}");
            }
        }
        else if (_帧 == 400)
        {
            GD.Print($"[IX] 动画轨迹 = [{string.Join(" → ", 轨迹)}]");
            GD.Print($"[IX] 终态 = {StateMachine.CurrentState}");
            断言(轨迹.Count >= 3, "观察到了 3 段以上的动画切换");
            断言(轨迹.Count > 0 && 轨迹[0] == "interact-a", "第 1 段是 interact-a（进入：抬手）");
            断言(轨迹.Contains("interact-b"), "第 2 段是 interact-b（保持：抱头）");
            断言(轨迹.Count > 0 && 轨迹[^1] == "interact-c", "末段是 interact-c（退出：放下手回待机 —— 这就是原版的后半段）");
            断言(StateMachine.CurrentState == StateMachine.Idle, "序列播完自动回待机");
            GD.Print($"[IX] ===== 失败数 = {_失败} =====");
            GD.Print(_失败 == 0 ? "[IX] PASS" : "[IX] FAIL");
            GetTree().Quit(_失败 == 0 ? 0 : 1);
        }
        else if (_帧 > 1200)
        {
            GD.PrintErr("[IX] 超时 FAIL");
            GetTree().Quit(2);
        }
    }
}