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
    private float _原心情;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        _原心情 = StatsTable.当前心情;
        GD.Print("=== StatsWindowProbe: 场景已实例化 ===");
    }

    /// <summary>摆位纯函数：四种情形（下方放得下 / 下方放不下翻上方 / 左越界 / 右越界）。</summary>
    private void 摆位纯函数组()
    {
        GD.Print("--- 摆位（纯函数）---");
        var 屏 = new Rect2I(0, 0, 1920, 1040);
        var 宠尺 = new Vector2I(282, 282);
        var 面 = new Vector2I(360, 180);

        var 下 = PanelPlacement.计算(new Vector2I(900, 500), 宠尺, 面, 屏);
        断言(下.Y == 500 + 282 + 8 && 下.X == 900 + (282 - 360) / 2,
            $"A 桌宠下方放得下 → 摆下方、水平居中（{下}）");

        var 上 = PanelPlacement.计算(new Vector2I(900, 900), 宠尺, 面, 屏);
        断言(上.Y == 900 - 180 - 8, $"B 下方放不下 → 翻到上方（{上}）");

        var 左 = PanelPlacement.计算(new Vector2I(0, 0), 宠尺, 面, 屏);
        断言(左.X == PanelPlacement.贴边间距, $"C 桌宠贴左边缘 → X 被夹进屏内（{左}）");

        var 右 = PanelPlacement.计算(new Vector2I(1900, 500), 宠尺, 面, 屏);
        断言(右.X == 1920 - 360 - PanelPlacement.贴边间距, $"D 桌宠贴右边缘 → X 被夹进屏内（{右}）");
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
            StatsTable.探针_设值(72f);
            ChatBox.显示();
            GD.Print("[SW] 已打开聊天面板（命令栏）");
        }
        else if (_帧 == 40)
        {
            摆位纯函数组();
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
            // 首版只有上面那条尺寸断言 → 摆位缺失（窗口落 (0,0)）没拦住。位置必须断言。
            var 宠位 = DisplayServer.WindowGetPosition();
            var 宠尺 = DisplayServer.WindowGetSize();
            var 屏 = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
            GD.Print($"[SW] 状态窗={位} {尺}；桌宠={宠位} {宠尺}；可用屏={屏}");
            断言(位 != Vector2I.Zero, "位置不是 (0,0)（摆位生效，不再跑屏幕左上角）");
            断言(位.X >= 屏.Position.X && 位.X + 尺.X <= 屏.End.X &&
                位.Y >= 屏.Position.Y && 位.Y + 尺.Y <= 屏.End.Y,
                $"窗口完整落在可用屏幕区内（{位}+{尺} ⊂ {屏}）");
            var 竖直距离 = Mathf.Abs(位.Y - 宠位.Y);
            断言(竖直距离 <= 宠尺.Y + 尺.Y + 40,
                $"窗口紧邻桌宠（竖直距离 {竖直距离} ≤ 桌宠高 {宠尺.Y} + 面板高 {尺.Y} + 40）");
            断言(!System.Text.RegularExpressions.Regex.IsMatch(StatsWindow.探针_全部文本, "[0-9]"),
                $"主人指定：**一个数字都不出现**（实际「{StatsWindow.探针_全部文本}」）");
            断言(StatsWindow.探针_数值文本.Contains("心情不错") && StatsWindow.探针_数值文本.Contains("——"),
                $"心情 72 → 文字状态「心情不错」，另两行是占位（{StatsWindow.探针_数值文本}）");
        }
        else if (_帧 == 70)
        {
            // 数值变化后窗口应跟着变（每帧刷新）—— 只动 mood，让文字状态跟着换
            StatsTable.探针_设值(29f);
        }
        else if (_帧 == 85)
        {
            断言(StatsWindow.探针_数值文本.Contains("不太开心"),
                $"mood 29 → 文字状态跟着变（{StatsWindow.探针_数值文本}）");
            断言(StatsWindow.探针_概览文本.Contains("有点"),
                $"概览随状态切换（{StatsWindow.探针_概览文本}）");
            StatsWindow.探针_截图("stats_window.png");
            GD.Print("[SW] 截图已存，保持 8 秒供视觉复核");
        }
        else if (_帧 == 90 + 60 * 8)
        {
            StatsTable.探针_设值(_原心情);
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