using desktop.script.Soul;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 状态窗探针（**非 headless**：要真实窗口几何与截图）：
/// ① 命令栏里出现「状态」按钮；② 点它真的弹出状态窗；③ 窗口内容与数值一致（截图供视觉复核）。
/// 用法：Godot_..._console.exe --path D:/Games/Github/AIPet res://tests/StatsWindowProbe.tscn
/// </summary>
public partial class StatsWindowProbe : Node
{
    private int _帧;
    private int _失败;
    private float _原心情, _原精力, _原亲密;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        _原心情 = StatsTable.当前心情; _原精力 = StatsTable.当前精力; _原亲密 = StatsTable.当前亲密;
        GD.Print("=== StatsWindowProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[SW] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[SW] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;

        if (_帧 == 10)
        {
            断言(StatsWindow.存在, "状态窗节点已在场景里（game.tscn）");
            断言(!StatsWindow.可见_探针, "默认不显示（要靠按钮唤出）");
        }
        else if (_帧 == 20)
        {
            // 造一个好看的数值组合，便于视觉复核
            StatsTable.探针_设值(72f, 88f, 156f);
            ChatBox.显示();
            GD.Print("[SW] 已打开聊天面板（命令栏）");
        }
        else if (_帧 == 40)
        {
            var 有状态按钮 = ChatBox.探针_命令栏有按钮("状态");
            断言(有状态按钮, "命令栏里找到「状态」按钮");
            StatsWindow.显示();
        }
        else if (_帧 == 55)
        {
            断言(StatsWindow.可见_探针, "点「状态」后窗口真的弹出");
            var 位 = StatsWindow.探针_窗口位置;
            var 尺 = StatsWindow.探针_窗口尺寸;
            GD.Print($"[SW] 状态窗位置={位} 尺寸={尺}");
            断言(尺.X > 200 && 尺.Y > 120, $"窗口尺寸合理（{尺}）");
            断言(StatsWindow.探针_数值文本.Contains("72") && StatsWindow.探针_数值文本.Contains("88") &&
                 StatsWindow.探针_数值文本.Contains("156"),
                $"窗口里显示的数值与 StatsTable 一致（{StatsWindow.探针_数值文本}）");
        }
        else if (_帧 == 70)
        {
            // 数值变化后窗口应跟着变（每帧刷新）
            StatsTable.探针_设值(30f, 12f, 156f);
        }
        else if (_帧 == 85)
        {
            断言(StatsWindow.探针_数值文本.Contains("30") && StatsWindow.探针_数值文本.Contains("12"),
                $"数值变化后窗口自动刷新（{StatsWindow.探针_数值文本}）");
            断言(StatsWindow.探针_概览文本.Contains("困了"),
                $"概览随状态切换（精力 12 → 「{StatsWindow.探针_概览文本}」）");
            StatsWindow.探针_截图("stats_window.png");
            GD.Print("[SW] 截图已存，保持 8 秒供视觉复核");
        }
        else if (_帧 == 90 + 60 * 8)
        {
            StatsTable.探针_设值(_原心情, _原精力, _原亲密);
            StatsWindow.隐藏();
            GD.Print($"[SW] 已恢复数值 → {StatsTable.概述}");
            GD.Print($"[SW] ===== 失败数 = {_失败} =====");
            GD.Print(_失败 == 0 ? "[SW] PASS" : "[SW] FAIL");
            GetTree().Quit(_失败 == 0 ? 0 : 1);
        }
        else if (_帧 > 60 * 60)
        {
            GD.PrintErr("[SW] 超时 FAIL");
            GetTree().Quit(2);
        }
    }
}