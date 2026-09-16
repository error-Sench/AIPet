using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 贴边隐藏素材探针（headless，P2 剩余）：12 段是否都真的载入且可播（对方是否镜像、帧数是否正确）。
/// 注意：本探针只验**素材层**；行为接线（何时贴边、窗口怎么移）见 `script/State/README.md`。
/// 用法：Godot_..._console.exe --headless --path D:/Games/Github/AIPet res://tests/EdgeHideProbe.tscn
/// </summary>
public partial class EdgeHideProbe : Node
{
    private int _帧;
    private int _失败;
    private string _待查;

    private static readonly (string 名, string 说明)[] 用例 =
    {
        ("edge_hide-left-in", "左·缩进边缘（Main/A 9帧）"),
        ("edge_hide-left-keep", "左·稳定保持（Main/B_1 4帧）"),
        ("edge_hide-left-hold", "左·长保持（Main/B_2 单帧 500ms）"),
        ("edge_hide-left-out", "左·退出（Main/C 7帧）"),
        ("edge_hide-left-peek", "左·探出（Rise/A 4帧，源名「左藏鼠标近普通A」）"),
        ("edge_hide-left-unpeek", "左·缩回（Rise/C 3帧）"),
        ("edge_hide-right-in", "右·缩进边缘（Main/A 14帧，含 A/A01 合并）"),
        ("edge_hide-right-keep", "右·稳定保持"),
        ("edge_hide-right-hold", "右·长保持"),
        ("edge_hide-right-out", "右·退出"),
        ("edge_hide-right-peek", "右·探出"),
        ("edge_hide-right-unpeek", "右·缩回"),
    };

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print($"=== EdgeHideProbe: 场景已实例化（已登记池={CharAnim.池已注册("edge_hide")}）===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[EH] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[EH] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;

        if (_帧 == 10)
        {
            断言(CharAnim.池已注册("edge_hide"), "edge_hide 已登记进 CharAnim.内置动画组（否则不预载）");
            foreach (var (名, 说明) in 用例)
                断言(CharAnim.有动画(名), $"素材载入：{名} —— {说明}");
            // 真的播一段（不是只 HasAnimation）
            CharAnim.PlayNamed("edge_hide-left-in");
            _待查 = "edge_hide-left-in";
        }
        else if (_帧 == 16)
        {
            断言(CharAnim.当前动画名_只读 == _待查,
                $"能真的播放（实际 {CharAnim.当前动画名_只读}）");
            CharAnim.PlayNamed("edge_hide-right-peek");
            _待查 = "edge_hide-right-peek";
        }
        else if (_帧 == 22)
        {
            断言(CharAnim.当前动画名_只读 == _待查,
                $"右侧镜像也能播（实际 {CharAnim.当前动画名_只读}）");
            GD.Print($"[EH] ===== 失败数 = {_失败} =====");
            GD.Print(_失败 == 0 ? "[EH] PASS" : "[EH] FAIL");
            GetTree().Quit(_失败 == 0 ? 0 : 1);
        }
        else if (_帧 > 600)
        {
            GD.PrintErr("[EH] 超时 FAIL");
            GetTree().Quit(2);
        }
    }
}