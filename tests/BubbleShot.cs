using System;
using System.IO;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// BubbleShot（**非 headless**，手动跑）：气泡**视觉与位置**的实证探针 —— 气泡是独立窗，headless 里没有渲染，
/// 所以这里跑真实渲染：把气泡窗自己的贴图存成 PNG（含 alpha），并**数值断言位置**（头顶居中 / 顶部放不下自动翻下方）。
/// <para>跑法：<c>Godot_v4.7.2-stable_mono_win64_console.exe --path D:/Games/Github/AIPet res://tests/BubbleShot.tscn</c></para>
/// 产物：<c>%LOCALAPPDATA%/Temp/aipet-bubble/*.png</c>（短气泡 / 长气泡 / 深色底）。
/// </summary>
public partial class BubbleShot : Node
{
    private int _帧;
    private int _失败;
    private static readonly string 输出目录 =
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Temp", "aipet-bubble");

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        Directory.CreateDirectory(输出目录);
        GD.Print($"=== BubbleShot: 输出目录 {输出目录} ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[BS] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[BS] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 20: 短气泡(); break;
            case 26: 截图("01-短气泡"); break;
            case 30: 长气泡(); break;
            case 36: 截图("02-长气泡"); break;
            case 40: 英文短句(); break;
            case 46: 截图("03-英文短句"); break;
            case 50: 位置断言(); break;
            case 56: 翻下方(); break;
            case 62: 翻下方_断言(); break;
            case 66: 收尾(); break;
        }
        if (_帧 > 400) { GD.PrintErr("[BS] 超时"); GetTree().Quit(2); }
    }

    private void 短气泡() => BubbleWindow.显示("嗯，在的～", 0f);
    private void 长气泡() => BubbleWindow.显示("主人今天过得怎么样啦？这句话我特意写长一点，看看气泡会不会自己折行、自己长高——要还是像以前那样被固定在窗口里裁掉，这一句的后半截就看不见了。", 0f);
    private void 英文短句() => BubbleWindow.显示("Ping 12ms · 45 FPS · battery 78%", 0f);

    private void 截图(string 名字)
    {
        try
        {
            var 图 = BubbleWindow.探针_截图();
            if (图 == null) { GD.PrintErr($"[BS] 截图失败（拿不到贴图）：{名字}"); return; }
            var 路径 = Path.Combine(输出目录, 名字 + ".png");
            图.SavePng(路径);
            GD.Print($"[BS] 已存 {路径}（{图.GetWidth()}×{图.GetHeight()}）");
        }
        catch (Exception e) { GD.PrintErr($"[BS] 截图异常: {e.Message}"); }
    }

    /// <summary>位置：水平居中于桌宠、气泡底边在桌宠顶边之上 `距头顶像素`；且四边在屏幕内。</summary>
    private void 位置断言()
    {
        var 宠位 = DisplayServer.WindowGetPosition();
        var 宠尺 = DisplayServer.WindowGetSize();
        var 屏 = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
        var 气泡 = BubbleWindow.探针_位置;
        var 尺 = BubbleWindow.探针_当前尺寸;
        GD.Print($"[BS] 桌宠 {宠位} {宠尺} · 屏 {屏} · 气泡 {气泡} {尺}");
        if (屏.Size.X <= 0) { GD.Print("[BS] 无屏幕信息 → 跳过位置断言"); return; }
        断言(Math.Abs((气泡.X + 尺.X / 2) - (宠位.X + 宠尺.X / 2)) <= 2, "水平居中于桌宠");
        断言(气泡.Y + 尺.Y <= 宠位.Y + 4, $"在桌宠上方（气泡底 {气泡.Y + 尺.Y} ≤ 桌宠顶 {宠位.Y}）");
        断言(气泡.X >= 屏.Position.X && 气泡.Y >= 屏.Position.Y
             && 气泡.X + 尺.X <= 屏.End.X && 气泡.Y + 尺.Y <= 屏.End.Y, "四边都在屏幕可用区内");
    }

    private static Vector2I _原宠位;

    /// <summary>把桌宠挪到屏幕最顶 → 上方放不下 → 气泡应翻到桌宠下方。</summary>
    private void 翻下方()
    {
        _原宠位 = DisplayServer.WindowGetPosition();
        var 屏 = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
        DisplayServer.WindowSetPosition(new Vector2I(_原宠位.X, 屏.Position.Y), (int)DisplayServer.MainWindowId);
        GD.Print("[BS] 已把桌宠挪到屏幕最顶");
    }

    private void 翻下方_断言()
    {
        var 宠位 = DisplayServer.WindowGetPosition();
        var 宠尺 = DisplayServer.WindowGetSize();
        var 气泡 = BubbleWindow.探针_位置;
        var 尺 = BubbleWindow.探针_当前尺寸;
        GD.Print($"[BS] 翻转后：桌宠 {宠位} · 气泡 {气泡}");
        断言(气泡.Y >= 宠位.Y + 宠尺.Y - 4, $"上方放不下 → 已翻到桌宠下方（气泡顶 {气泡.Y} ≥ 桌宠底 {宠位.Y + 宠尺.Y}）");
        DisplayServer.WindowSetPosition(_原宠位, (int)DisplayServer.MainWindowId);   // 还原，别乱动主人桌面
    }

    private void 收尾()
    {
        BubbleWindow.隐藏();
        GD.Print($"[BS] ===== 失败数 = {_失败} =====");
        GD.Print(_失败 == 0 ? "[BS] PASS" : "[BS] FAIL");
        GetTree().Quit(_失败 == 0 ? 0 : 1);
    }
}
