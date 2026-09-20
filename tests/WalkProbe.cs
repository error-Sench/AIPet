using System;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 自主走动实证探针（**必须非 headless**，headless 下屏幕/窗口尺寸为 0，`尝试走动` 会直接放弃）：
///   ① 把桌宠窗口挪到离鼠标最远的角落（绕开「鼠标悬停在桌宠身上 → 禁止主动行为」闸门）
///   ② 临时把节律调快（只改内存，不动 config/behavior.json）
///   ③ 观察是否真的发生走动、窗口 X 是否真的变了
/// 用法：Godot_..._console.exe --path D:/Games/Github/AIPet res://tests/WalkProbe.tscn
/// </summary>
public partial class WalkProbe : Node
{
    private int _帧;
    private int _起始X;
    private int _失败;
    private string _走动中动画 = "";

    public override void _Ready()
    {
        // 必须先写临时节律配置：StateMachine._Ready（场景实例化时）会读 user://behavior.json，
        // 之后再改内存里的值就晚了（_走动倒计时 已经用旧配置算好了）。
        写临时节律();
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== WalkProbe: 场景已实例化 ===");
    }

    private static void 写临时节律()
    {
        var 路径 = ProjectSettings.GlobalizePath("user://behavior.json");
        System.IO.File.WriteAllText(路径, """
        {
          "启用": true,
          "走动空闲秒": 3,
          "首次走动最小秒": 1,
          "首次走动最大秒": 2,
          "走动间隔最小秒": 3,
          "走动间隔最大秒": 5,
          "走动距离最小像素": 80,
          "走动距离最大像素": 120,
          "走动速度像素每秒": 120,
          "睡眠空闲秒": 9999,
          "_组②隔离": "本探针只验「走动」：爬边/趴行关掉（爬边是独立行为、趴行会改动画名）",
          "爬边概率": 0,
          "爬行概率": 0
        }
        """);
        GD.Print($"[WK] 已写临时节律配置: {路径}");
    }

    private static void 清临时节律()
    {
        try
        {
            var 路径 = ProjectSettings.GlobalizePath("user://behavior.json");
            if (System.IO.File.Exists(路径)) System.IO.File.Delete(路径);
            GD.Print("[WK] 已清理临时节律配置");
        }
        catch (System.Exception e) { GD.PrintErr($"[WK] 清理临时配置失败: {e.Message}"); }
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[WK] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[WK] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        // 走动期间抓一下正在播的动画名，用于断言「走的是 walk 资产而不是 drag 占位」
        if (StateMachine.CurrentState.StartsWith("walk", System.StringComparison.Ordinal))
            _走动中动画 = CharAnim.当前动画名_只读;

        if (_帧 == 4) 准备();
        else if (_帧 == 60)   // ≈1s：入场动画应已结束
        {
            GD.Print($"[WK] 入场未完成={StateMachine.入场未完成_只读} 状态={StateMachine.CurrentState}");
        }
        else if (_帧 == 1200) // ≈20s
        {
            var 末X = DisplayServer.WindowGetPosition().X;
            GD.Print($"[WK] 走动次数={StateMachine.走动次数_只读} 主动次数={StateMachine.主动次数_只读} 空闲={StateMachine.空闲秒_只读:0.0}s 状态={StateMachine.CurrentState}");
            GD.Print($"[WK] 窗口 X: 起始={_起始X} 现在={末X}");
            GD.Print($"[WK] 走动期间动画={_走动中动画}（walk-left 已载入={CharAnim.有动画("walk-left")}, walk-right 已载入={CharAnim.有动画("walk-right")}）");
            断言(StateMachine.走动次数_只读 >= 1, "发生了自主走动");
            断言(末X != _起始X, "桌宠窗口 X 真的移动了");
            断言(_走动中动画 is "walk-left" or "walk-right", "走动用的是 walk 资产（不是 drag 占位）");
            GD.Print($"[WK] ===== 失败数 = {_失败} =====");
            清临时节律();
            GetTree().Quit(_失败 == 0 ? 0 : 1);
        }
    }

    private void 准备()
    {
        // 绕开鼠标悬停闸门：把窗口放到鼠标的对角
        var 屏 = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
        var 鼠 = DisplayServer.MouseGetPosition();
        var 犬 = desktop.script.UX.PetWindow.S;
        var 左半 = 鼠.X > (屏.Position.X + 屏.End.X) / 2;
        var 新X = 左半 ? 屏.Position.X + 40 : Math.Max(屏.Position.X + 40, 屏.End.X - 犬 - 40);
        DisplayServer.WindowSetPosition(new Vector2I(新X, 屏.Position.Y + 60));
        _起始X = 新X;
        GD.Print($"[WK] 鼠标=({鼠.X},{鼠.Y}) 桌宠挪到=({新X},{屏.Position.Y + 60}) 角色尺寸={犬}");

        // 节律已在 _Ready 前用 user://behavior.json 注入（见 写临时节律），这里不再改内存
        GD.Print($"[WK] 节律: 空闲={StateMachine.设置.走动空闲秒}s 首次={StateMachine.设置.首次走动最小秒}~{StateMachine.设置.首次走动最大秒}s");
    }
}