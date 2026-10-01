using System;
using desktop.script.Logic;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 音乐反应探针（headless，组③）：纯函数阈值 + 检测→起跳（A）→舞蹈循环→静音收场（C）全流程，
/// 嗨档换 Single 舞；用 `MusicSense.探针_峰值覆写` 注入假音量（不碰真实音频设备）。
/// 用法：Godot_v4.7.2-stable_mono_win64_console.exe --headless --path D:/Games/Github/AIPet res://tests/MusicProbe.tscn
/// </summary>
public partial class MusicProbe : Node
{
    private int _帧;
    private int _失败;
    private int _步;
    private int _步帧;                 // 当前步起始帧（超时判定用）
    private string _圈前动画 = "";      // 上一圈观察到的 B 段动画名
    private int _采舞段圈;             // 已采到的「换段」次数
    private readonly System.Collections.Generic.List<string> _舞段样本 = new();

    public override void _Ready()
    {
        Main.探针_禁首启提示 = true;
        StateMachine.设置.问候启用 = false;
        DailyRoutine.问候启用 = false;
        // 探针口径：识别/静音 0.3s、采样 0.05s（真实默认 3s / 6s / 0.5s）
        MusicSense.识别秒 = 0.3f;
        MusicSense.静音秒 = 0.3f;
        MusicSense.采样间隔 = 0.05f;
        // 2026-10-01 两级阈值改造：复评周期压到 2 次采样（0.1s）——headless 里换档不用等 2 秒
        MusicSense.复评秒 = 0.1f;
        MusicSense.探针_忽略闸门 = true;
        MusicSense.探针_峰值覆写 = 0f;   // 起始静音
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== MusicProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[MU] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[MU] FAIL  {描述}"); }
    }

    private void 结束()
    {
        MusicSense.探针_重置();
        GD.Print($"[MU] ===== 失败数 = {_失败} =====（步 {_步}/帧 {_帧}）");
        GD.Print(_失败 == 0 ? "[MU] PASS" : "[MU] FAIL");
        GetTree().Quit(_失败 == 0 ? 0 : 1);
    }

    public override void _Process(double delta)
    {
        _帧++;
        // 2026-10-01：B 段一圈 2.75~4 秒（nomal 组 rate=8），采 4 圈换段要 ~13 秒 → 预算放到 3000 帧
        if (_帧 > 3000) { 断言(false, $"超时（步 {_步} 卡住，状态 {StateMachine.CurrentState}，动画 {CharAnim.当前动画名_只读}）"); 结束(); return; }
        var 动画 = CharAnim.当前动画名_只读;

        switch (_步)
        {
            case 0 when _帧 >= 4:
                StateMachine.入场完成();
                // 纯函数：阈值判定（> 音量阈值 0.02 才算）
                断言(MusicSense.有声音(0.05f) && MusicSense.有声音(0.021f), "有声音：0.05 / 0.021 为真");
                断言(!MusicSense.有声音(0.02f) && !MusicSense.有声音(0.001f) && !MusicSense.有声音(-1f),
                    "有声音：0.02（等于阈值）/ 0.001 / -1（读失败）为假");
                断言(MusicSense.当前峰值 == -1f || MusicSense.当前峰值 <= 1f, "峰值读数在合法范围（初始化前 -1）");
                MusicSense.探针_峰值覆写 = 0.05f;   // 常规音量 → 舞蹈档
                _步 = 1;
                break;

            case 1 when StateMachine.CurrentState == StateMachine.Music:
                断言(StateMachine.包裹中, "起跳后包裹会话建立");
                _步 = 2;
                break;

            case 2 when 动画 == "music-a":
                断言(true, $"起跳先播 A 段（{动画}）");
                StateMachine.重播当前状态();   // 模拟 A 播完
                _步 = 3;
                break;

            case 3 when 动画.StartsWith("music-") && !动画.EndsWith("-a") && !动画.EndsWith("-c"):
                断言(!动画.Contains("-single-"), $"常规音量 → 舞蹈档随机（{动画}）");
                断言(动画.StartsWith("music-nomal-"), $"按档挑（三档关=nomal 组，不串档；实际 {动画}）");
                // 2026-10-01 两级阈值改造：B 段每圈**重掷变体**（官方 Display_Music 每圈重新 FindGraph）
                // ——连播多圈应出现 ≥2 种变体（nomal 组有 5 条，10 圈全同一段的概率 5^-9 ≈ 0）
                _舞段样本.Clear();
                _采舞段圈 = 0;
                _圈前动画 = 动画;
                _步帧 = _帧;
                _步 = 31;
                break;

            // ── 31 组：常规档连播多圈 → 变体轮换（每圈重掷）──
            // 被动观察：music B 段非循环，每圈自然播完 → OnAnimationFinished → 重播当前状态 → 重掷变体。
            // （不手动催 重播当前状态：它有 0.05s 冷却，headless 连催会被吞。自然换圈正是真实运行路径。）
            case 31 when 动画 != _圈前动画 && 动画.StartsWith("music-nomal-"):
                _圈前动画 = 动画;
                _舞段样本.Add(动画);
                // 采 4 圈（= 3 次换段）：nomal 组 5 条变体，全撞同一段的概率 (1/5)^3 = 0.8%
                if (++_采舞段圈 >= 4)
                {
                    var 种数 = new System.Collections.Generic.HashSet<string>(_舞段样本).Count;
                    断言(种数 >= 2, $"B 段每圈重掷变体：{_采舞段圈} 圈出现 {种数} 种（官方 _1/_2 混播防单调）");
                    _步 = 33;
                }
                break;

            case 31 when _帧 > _步帧 + 1500:
                断言(false, $"B 段没轮换（卡在 {_圈前动画}，采到 {_采舞段圈} 圈）——变体每圈重掷失效");
                _步 = 33;
                break;

            // ── 33 组：音量冲上二级阈值 → **中途立刻换 Single**（不等这一圈播完） ──
            case 33:
                断言(!MusicSense.嗨档, "常规音量下 嗨档=false");
                MusicSense.探针_峰值覆写 = 0.9f;   // 冲高音量
                _步帧 = _帧;
                _步 = 34;
                break;

            case 34 when MusicSense.嗨档 && 动画.StartsWith("music-single-"):
                断言(true, $"跳舞中途换档 → Single 立刻换舞（{动画}；官方档位一变就 Display_Music）");
                _步 = 35;
                break;

            case 34 when _帧 > _步帧 + 200:
                断言(false, $"音量冲高后没换 Single（嗨档={MusicSense.嗨档}，动画={动画}）");
                _步 = 35;
                break;

            // ── 35 组：音量回落 → 换回常规 B 段 ──
            case 35:
                MusicSense.探针_峰值覆写 = 0.05f;   // 回落到一级（有声但不嗨）
                _步帧 = _帧;
                _步 = 36;
                break;

            case 36 when !MusicSense.嗨档 && 动画.StartsWith("music-nomal-"):
                断言(true, $"音量回落 → 换回常规舞（{动画}）");
                MusicSense.探针_峰值覆写 = 0f;   // 转静音 → 应收场
                _步 = 4;
                break;

            case 36 when _帧 > _步帧 + 200:
                断言(false, $"音量回落后没换回常规（嗨档={MusicSense.嗨档}，动画={动画}）");
                MusicSense.探针_峰值覆写 = 0f;
                _步 = 4;
                break;

            case 4 when 动画.EndsWith("-c", StringComparison.Ordinal) && 动画.StartsWith("music-"):
                断言(StateMachine.CurrentState == StateMachine.Music, "收场：先播 C、状态延迟落地");
                StateMachine.重播当前状态();   // 模拟 C 播完
                _步 = 5;
                break;

            case 5 when StateMachine.CurrentState == StateMachine.Idle:
                断言(!StateMachine.包裹中, "收场回 idle、包裹会话结束");
                MusicSense.探针_峰值覆写 = 0.6f;   // 嗨音量 → Single 舞
                _步 = 6;
                break;

            case 6 when StateMachine.CurrentState == StateMachine.Music && 动画 == "music-a":
                StateMachine.重播当前状态();
                _步 = 7;
                break;

            case 7 when 动画.StartsWith("music-single-"):
                断言(true, $"嗨档 → Single 舞（{动画}）");
                断言(StateMachine.包裹中, "Single 会话钉死");
                _步 = 8;
                break;

            case 8:
                结束();
                break;
        }
    }
}
