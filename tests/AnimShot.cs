using System;
using System.IO;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// AnimShot（**非 headless**，手动跑）：把 P10 新导入的动画**逐个播一遍并渲成 PNG** —— 素材导入的
/// 缩放/落地线只靠数值核对不够（每个源图的姿势与道具都不一样），得看得见才算验过。
/// <para>跑法：<c>Godot_..._console.exe --path D:/Games/Github/AIPet res://tests/AnimShot.tscn</c></para>
/// 产物：<c>%LOCALAPPDATA%/Temp/aipet-anim/*.png</c>（每个动画一张中间帧；看完可删）。
/// </summary>
public partial class AnimShot : Node
{
    private static readonly string 输出目录 =
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Temp", "aipet-anim");

    /// <summary>要截的动画（P10 新素材的代表：摸身体 / 转身 / 干活过渡 / 新干活种类 / 新待机 / 快慢走 / 害羞 / 高兴档摸头）。</summary>
    private static readonly string[] 名单 =
    [
        "interact_body-a", "interact_body-b", "interact_body-c",
        "turn-a", "turn-b", "turn-c",
        "switch-up", "switch-down",
        "work-calligraphy", "work-paint", "work-rope", "work-water", "work-clean", "work-sausage",
        "fidget-squat", "fidget-tennis", "fidget-bubbles", "fidget-boring", "fidget-aside",
        "walk-left-fast", "walk-left-slow",
        "say-shy", "interact-happy-a",
    ];

    private int _序号;
    private int _帧;
    private int _失败;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        Directory.CreateDirectory(输出目录);
        // 关掉动画之外的动作干扰：钉在 idle，别让状态机自己乱切
        StateMachine.入场完成();
        DailyRoutine.探针_重置();
        GD.Print($"=== AnimShot: 输出目录 {输出目录}（共 {名单.Length} 张） ===");
    }

    public override void _Process(double delta)
    {
        _帧++;
        if (_序号 >= 名单.Length)
        {
            if (_帧 % 60 != 0) return;
            GD.Print($"[AS] ===== 失败数 = {_失败} =====");
            GD.Print(_失败 == 0 ? "[AS] PASS" : "[AS] FAIL");
            GetTree().Quit(_失败 == 0 ? 0 : 1);
            return;
        }

        var 名 = 名单[_序号];
        // 每个动画：播 → 等 8 帧（跳到动作中段）→ 截图 → 下一个
        if (_帧 % 12 == 1)
        {
            StateMachine.SetState(StateMachine.Idle);
            CharAnim.PlayNamed(名);
        }
        else if (_帧 % 12 == 9)
        {
            var 图 = GetViewport()?.GetTexture()?.GetImage();
            if (图 == null) { GD.PrintErr($"[AS] 截图失败：{名}"); _失败++; }
            else
            {
                if (图.IsCompressed()) 图.Decompress();
                var 路径 = Path.Combine(输出目录, $"{_序号:00}-{名}.png");
                图.SavePng(路径);
                var 实际 = CharAnim.当前动画名_只读;
                var 一致 = 实际 == 名;
                if (!一致) _失败++;
                GD.Print($"[AS] {(一致 ? "✓" : "✗")} {名} → 实际 {实际}，图 {图.GetWidth()}x{图.GetHeight()}");
            }
            _序号++;
        }
    }
}
