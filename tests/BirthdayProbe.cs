using System;
using desktop.script.Logic;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 生日彩蛋探针（headless）：把「生日」覆写成今天 → 场景实例化 → 入场完成时应自动进 bday 序列，
/// 三段逐段推进（用 重播当前状态() 模拟「动画播完」，不靠实时），播完回 idle。不碰真实 config。
/// 用法：Godot_v4.7.2-stable_mono_win64_console.exe --headless --path D:/Games/Github/AIPet res://tests/BirthdayProbe.tscn
/// </summary>
public partial class BirthdayProbe : Node
{
    private int _帧;
    private int _失败;

    public override void _Ready()
    {
        Main.探针_禁首启提示 = true;                          // 与首启提示互不打扰
        Main.探针_生日覆写 = DateTime.Now.ToString("MM-dd");  // 隔离：不碰 config 文件
        StateMachine.设置.问候启用 = false;                    // 隔离时间驱动（同 StateProbe 口径）
        DailyRoutine.问候启用 = false;
        StateMachine.设置.磁盘提醒启用 = false;
        DailyRoutine.磁盘提醒启用 = false;
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== BirthdayProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[BD] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[BD] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        if (_帧 == 4)
        {
            断言(StateMachine.入场未完成_只读, "入场未完成（前置）");
            StateMachine.入场完成();   // 解除入场门 → 生日回调应立刻把状态切进 bday
            断言(StateMachine.CurrentState == StateMachine.Bday, $"生日命中 → 进 bday（当前 {StateMachine.CurrentState}）");
        }
        else if (_帧 == 6)
        {
            // PlayNamed 走 CallDeferred → 下一帧才落
            断言(CharAnim.当前动画名_只读 == "bday-a", $"序列第 1 段 bday-a（实际 {CharAnim.当前动画名_只读}）");
        }
        else if (_帧 == 10)
        {
            StateMachine.重播当前状态();   // 模拟 bday-a 播完
        }
        else if (_帧 == 12)
        {
            断言(CharAnim.当前动画名_只读 == "bday-b", $"序列第 2 段 bday-b（实际 {CharAnim.当前动画名_只读}）");
        }
        else if (_帧 == 16)
        {
            StateMachine.重播当前状态();   // 模拟 bday-b 播完
        }
        else if (_帧 == 18)
        {
            断言(CharAnim.当前动画名_只读 == "bday-c", $"序列第 3 段 bday-c（实际 {CharAnim.当前动画名_只读}）");
        }
        else if (_帧 == 22)
        {
            StateMachine.重播当前状态();   // 模拟 bday-c 播完 → 序列收尾
        }
        else if (_帧 == 24)
        {
            断言(StateMachine.CurrentState == StateMachine.Idle, $"序列播完回 idle（当前 {StateMachine.CurrentState}）");
        }
        else if (_帧 == 28)
        {
            GD.Print($"[BD] ===== 失败数 = {_失败} =====");
            GD.Print(_失败 == 0 ? "[BD] PASS" : "[BD] FAIL");
            Main.探针_生日覆写 = null;
            GetTree().Quit(_失败 == 0 ? 0 : 1);
        }
    }
}
