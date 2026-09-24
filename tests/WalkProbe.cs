using System;
using System.Collections.Generic;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 自主移动实证探针（**必须非 headless**，headless 下屏幕/窗口尺寸为 0，移动触发条件全废）：
///   ① 把桌宠窗口挪到离鼠标最远的角落（绕开「鼠标悬停在桌宠身上 → 禁止主动行为」闸门）
///   ② 临时把节律调快（只改 user://behavior.json，末尾原样还原）
///   ③ 观察是否真的发生移动（walk/crawl/climb/fall 任一）、窗口位置是否真的变了（X 或 Y 任一）
/// 重构#4 起：移动 = MoveRunner（VPet Move 模型），段推进归它；本探针只验「真的动了 + 用的是走/趴资产」。
/// 用法：Godot_..._console.exe --path D:/Games/Github/AIPet res://tests/WalkProbe.tscn
/// </summary>
public partial class WalkProbe : Node
{
    private int _帧;
    private int _起始X;
    private int _起始Y;
    private int _失败;
    private bool _动过;
    private bool _停过;
    private readonly List<string> _移动中动画集 = new();
    private static string _原配置;

    public override void _Ready()
    {
        // 必须先写临时节律配置：StateMachine._Ready（场景实例化时）会读 user://behavior.json，
        // 之后再改内存里的值就晚了（_走动倒计时 已经用旧配置算好了）。
        写临时节律();
        MusicSense.启用 = false;   // 组③：隔离音乐反应（系统有声就跳舞会挡住断言）
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== WalkProbe: 场景已实例化 ===");
    }

    private static string 配置路径() => ProjectSettings.GlobalizePath("user://behavior.json");

    private static void 写临时节律()
    {
        var 路径 = 配置路径();
        try { if (System.IO.File.Exists(路径)) _原配置 = System.IO.File.ReadAllText(路径); } catch { _原配置 = null; }
        System.IO.File.WriteAllText(路径, """
        {
          "启用": true,
          "走动空闲秒": 3,
          "首次走动最小秒": 1,
          "首次走动最大秒": 2,
          "移动启用": true,
          "接力概率": 0.8,
          "睡眠空闲秒": 9999,
          "_备注": "WalkProbe 临时节律：只验「自主移动真的发生」"
        }
        """);
        GD.Print($"[WK] 已写临时节律配置: {路径}（原文件{(_原配置 == null ? "不存在" : "已备份，末尾还原")}）");
    }

    private static void 还原节律()
    {
        try
        {
            var 路径 = 配置路径();
            if (_原配置 != null) { System.IO.File.WriteAllText(路径, _原配置); GD.Print("[WK] 已还原原节律配置"); }
            else if (System.IO.File.Exists(路径)) { System.IO.File.Delete(路径); GD.Print("[WK] 已清理临时节律配置"); }
        }
        catch (System.Exception e) { GD.PrintErr($"[WK] 还原配置失败: {e.Message}"); }
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[WK] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[WK] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        // 移动期间收集所有播过的动画名（walk/crawl/climb/fall 都要露面才算真的在动），用于断言「用的是真资产而不是 drag 占位」
        if (StateMachine.CurrentState == StateMachine.MoveState)
        {
            _动过 = true;
            var 名 = CharAnim.当前动画名_只读;
            if (!string.IsNullOrEmpty(名) && !_移动中动画集.Contains(名)) _移动中动画集.Add(名);
        }
        else if (_动过 && StateMachine.CurrentState == StateMachine.Idle)
        {
            _停过 = true;
        }

        if (_帧 == 4) 准备();
        else if (_帧 == 60)   // ≈1s：入场动画应已结束
        {
            GD.Print($"[WK] 入场未完成={StateMachine.入场未完成_只读} 状态={StateMachine.CurrentState}");
        }
        else if (_帧 == 1800) // ≈30s
        {
            var 末位 = DisplayServer.WindowGetPosition();
            GD.Print($"[WK] 移动次数={StateMachine.移动次数_只读} 主动次数={StateMachine.主动次数_只读} 空闲={StateMachine.空闲秒_只读:0.0}s 状态={StateMachine.CurrentState}");
            GD.Print($"[WK] 窗口位置: 起始=({_起始X},{_起始Y}) 现在=({末位.X},{末位.Y})");
            GD.Print($"[WK] 移动期间动画集=[{string.Join(", ", _移动中动画集)}]");
            断言(StateMachine.移动次数_只读 >= 1, "发生了自主移动");
            // 2026-09-24 收口修（同 C 卡资产断言的道理）：角落起步时骰子可能命中垂直移动
            //（climb-*-down / fall-*——重力移动 X 恒定、只夹屏内，见 MoveRunner.推进）→ 合法移动下 X 本就不变，
            // 判据放宽为「X 或 Y 任一变化」；「真的动了」的原意不变（完全没动仍会挂）。
            断言(末位.X != _起始X || 末位.Y != _起始Y, "桌宠窗口真的移动了（X 或 Y 任一变化）");
            // 2026-09-24 修（动画组C 验收时发现）：断言原来只认 walk-/crawl-——但窗口起始靠近屏幕边时
            // 骰子完全可能命中爬边族（climb，触发近 ≤64px），那是合法移动资产不是失败。
            // 本断言的原意 = 「用的是移动表资产，不是 drag 占位」→ 收全四个移动族。
            断言(_移动中动画集.Exists(n => n.StartsWith("walk-", StringComparison.Ordinal) || n.StartsWith("crawl-", StringComparison.Ordinal)
                || n.StartsWith("climb", StringComparison.Ordinal) || n.StartsWith("fall-", StringComparison.Ordinal)),
                "移动用的是走/趴/爬/落资产（不是 drag 占位）");
            断言(_移动中动画集.Exists(n => n.EndsWith("-a", StringComparison.Ordinal)),
                "进入段 *-a 出现过（A→B 段推进生效）");
            if (_停过)
                断言(_移动中动画集.Exists(n => n.EndsWith("-c", StringComparison.Ordinal)), "收步段 *-c 出现过（停步回待机）");
            else
                GD.Print("[WK] 30s 内没停过（接力把路线接下去了）→ 跳过「收步段」断言");
            GD.Print($"[WK] ===== 失败数 = {_失败} =====");
            还原节律();
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
        _起始Y = 屏.Position.Y + 60;
        GD.Print($"[WK] 鼠标=({鼠.X},{鼠.Y}) 桌宠挪到=({新X},{屏.Position.Y + 60}) 角色尺寸={犬}");

        // 节律已在 _Ready 前用 user://behavior.json 注入（见 写临时节律），这里不再改内存
        GD.Print($"[WK] 节律: 空闲={StateMachine.设置.走动空闲秒}s 首次={StateMachine.设置.首次走动最小秒}~{StateMachine.设置.首次走动最大秒}s 移动启用={StateMachine.设置.移动启用}");
    }
}
