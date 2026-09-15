using Godot;

namespace desktop.tests;

/// <summary>
/// 登场动画取证探针（**需非 headless**）：在启动的头几秒多次抓取主视口画面存成 PNG，
/// 用于人工/视觉复核「登场动画到底有没有播、有没有被抢断」。
/// 用法：Godot_..._console.exe --path D:/Games/Github/AIPet res://tests/EnterProbe.tscn
/// </summary>
public partial class EnterProbe : Node
{
    private int _帧;
    private int _序号;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== EnterProbe: 场景已实例化 ===");
    }

    private static readonly int[] 抓帧点 = [15, 45, 90, 135, 400];

    public override void _Process(double delta)
    {
        _帧++;
        if (System.Array.IndexOf(抓帧点, _帧) < 0) return;
        var 图 = GetViewport()?.GetTexture()?.GetImage();
        if (图 == null) { GD.PrintErr("[EN] 抓帧失败：纹理为空"); return; }
        var 路径 = ProjectSettings.GlobalizePath($"user://enter_t{_帧:0000}.png");
        图.SavePng(路径);
        var 宠 = GetChild(0).GetNodeOrNull("AnimatedSprite2D") as AnimatedSprite2D;
        GD.Print($"[EN] t={_帧 / 60f:0.00}s 帧={_帧} 当前动画={宠?.Animation} 帧序号={宠?.Frame} 图={图.GetWidth()}x{图.GetHeight()} → {路径}");
        _序号++;
        if (_序号 >= 抓帧点.Length)
        {
            GD.Print("[EN] DONE");
            GetTree().Quit(0);
        }
    }
}