using System;
using desktop.script.Logic;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 双缓冲验证探针（headless，重构#4 决策依据）：VPet 的双 Grid 交替（MainDisplay.cs:556-608
/// petgridcrlf 翻转）是给 WPF「新动画异步载图期间旧 Grid 已停」打的补丁，防切段空帧；
/// 我们启动时全部纹理已预载进 SpriteFrames，Play(新名) 同帧生效，理论上不存在空窗。
/// 本探针实证：连续快速切换不同动画名（段切换风暴，每 3 帧一换），每帧断言
/// 「当前动画在 SpriteFrames 里存在且帧数 ≥1」——若全过 → 重构#4 判定为**不需要**，文档记录理由。
/// Godot_v4.7.2-stable_mono_win64_console.exe --headless --path D:/Games/Github/AIPet res://tests/BufferProbe.tscn
/// </summary>
public partial class BufferProbe : Node
{
    private int _帧;
    private int _失败;
    private int _检查帧数;
    private int _空窗帧;

    // 模拟真实段切换风暴：每 3 帧换一个完全不同的动画名（跨池、长短混合）
    private static readonly string[] 切换序列 =
    [
        "idle-nomal-1", "fidget-squat-a", "fidget-squat", "fidget-squat-c",
        "think-nomal-a", "think-nomal", "walk-right-a", "walk-right", "walk-right-c",
        "say-shining-a", "say-shining", "work-calligraphy-a", "music-nomal-1", "sleep-loop",
        "idle-happy-1", "fidget-tennis", "climb-left-b", "interact-a",
    ];

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
        GD.Print("=== BufferProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[BUF] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[BUF] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 4:
                StateMachine.入场完成();
                StateMachine.探针_禁用包裹 = true;   // 直接点播动画名，绕过包裹会话（只验渲染层）
                return;
            case 420:
                断言(_检查帧数 > 380, $"检查了 {_检查帧数} 帧（切换风暴 ~136 次换名）");
                断言(_空窗帧 == 0, $"零空窗帧（实测 {_空窗帧}/{_检查帧数}）——纹理全预载，无 WPF 式异步加载空窗");
                GD.Print($"[BUF] ===== 失败数 = {_失败} =====");
                GD.Print(_失败 == 0 ? "[BUF] PASS" : "[BUF] FAIL");
                GD.Print("[BUF] 结论：重构#4「双缓冲」判定为不需要——Godot SpriteFrames 预载架构已覆盖");
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                return;
        }
        if (_帧 < 10) return;

        // 每 3 帧切一个完全不同的动画（模拟段切换风暴）
        if ((_帧 - 10) % 3 == 0)
            CharAnim.PlayNamed(切换序列[(_帧 / 3) % 切换序列.Length]);

        // 每帧检查：当前动画必须在 SpriteFrames 里且帧数 ≥1（= 立即可渲染，无空窗）
        var 当前 = CharAnim.当前动画名_只读;
        _检查帧数++;
        if (string.IsNullOrEmpty(当前) || !CharAnim.有动画(当前) || CharAnim.帧数_只读(当前) < 1)
        {
            _空窗帧++;
            if (_空窗帧 <= 3) GD.PrintErr($"[BUF] 空窗帧 @{_帧}: 当前动画={当前 ?? "(空)"}");
        }
    }
}
