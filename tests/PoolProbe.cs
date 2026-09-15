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
    ];

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== PoolProbe: 场景已实例化 ===");
    }

    public override void _Process(double delta)
    {
        _帧++;
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