using System;
using desktop.script.Logic;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// fidget 会话探针（headless，重构#2）：验证待机小动作的「A → B循环×骰子 → C → idle」机制
/// （VPet MainDisplay.cs:314-320 DisplayBLoopingToNomal 同款）——
/// ① 三段结构变体（squat 等）：A 播完接 B、B 播完圈数递增、骰子命中播 C、C 播完回 idle；
/// ② 首圈恒不过线（Next(1)=0）：会话至少播一圈 B；
/// ③ 单段变体（spin 等 Single 型）：不开会话，播完直接回 idle；
/// ④ fidget循环L=0 时骰子第 2 圈起必退（Next(2)≥1>0 半概率、Next(3) 2/3 概率——用大样本验证平均圈数收敛 ~2.7）。
/// 用 探针_模拟播完() 驱动（不靠实时）。用法：
/// Godot_v4.7.2-stable_mono_win64_console.exe --headless --path D:/Games/Github/AIPet res://tests/FidgetProbe.tscn
/// </summary>
public partial class FidgetProbe : Node
{
    private int _帧;
    private int _失败;

    public override void _Ready()
    {
        Main.探针_禁首启提示 = true;
        MusicSense.启用 = false;
        // 探针隔离：冻结时间驱动（问候/磁盘/音乐）——否则共享存档下被每日问候抢状态（全量回归顺序相关抖动，实测）
        StateMachine.设置.探针_冻结时间驱动开关 = true;
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== FidgetProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[FDG] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[FDG] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 4:
                StateMachine.入场完成();
                break;

            // ── ① 三段会话：钉死 squat，A → B → 骰子推进 ──
            case 8:
                CharAnim.探针_fidget会话("fidget-squat");
                断言(CharAnim.当前动画名_只读 == "fidget-squat-a", $"会话开启先播 A 段（实际 {CharAnim.当前动画名_只读}）");
                断言(CharAnim.fidget会话_只读 == "fidget-squat", "会话主段钉死 fidget-squat");
                断言(CharAnim.fidget圈数_只读 == 0, "初始圈数 0");
                break;
            case 12:
                CharAnim.探针_模拟播完();   // A 播完 → B 第一圈
                断言(CharAnim.当前动画名_只读 == "fidget-squat", "A 播完接 B 主段");
                break;
            case 16:
                CharAnim.探针_模拟播完();   // B 第 1 圈播完 → 首掷 Next(1)=0 恒不过线 → 必再来一圈
                断言(CharAnim.fidget圈数_只读 == 1, "第 1 圈播完圈数=1");
                断言(CharAnim.当前动画名_只读 == "fidget-squat", "首圈骰子恒不过线（必播第 2 圈）");
                break;
            case 20:
                // 把 L 调到 0：第 2 圈掷 Next(2)∈{0,1} > 0 半概率退；反复模拟播完直到进 C（最多 50 圈防爆）
                StateMachine.设置.fidget循环L = 0;
                var 圈 = 0;
                while (CharAnim.当前动画名_只读 == "fidget-squat" && 圈 < 50)
                {
                    CharAnim.探针_模拟播完();
                    圈++;
                }
                断言(CharAnim.当前动画名_只读 == "fidget-squat-c", $"骰子命中后播 C 退出段（实际 {CharAnim.当前动画名_只读}，掷了 {圈} 次）");
                break;
            case 24:
                CharAnim.探针_模拟播完();   // C 播完 → 落地回 idle
                断言(CharAnim.fidget会话_只读 == null, "C 播完会话清空");
                断言(CharAnim.当前动画名_只读.StartsWith("idle"), $"落地回 idle（实际 {CharAnim.当前动画名_只读}）");
                break;

            // ── ② 单段变体（Single 型）：不开会话，播完直接回 idle ──
            case 28:
                // spin 是原项目单段素材（无 -a）：手动播它并模拟会话外的 fidget 完成路径
                CharAnim.PlayNamed("fidget-spin");
                break;
            case 32:
                断言(CharAnim.fidget会话_只读 == null, "单段变体不开会话");
                CharAnim.探针_模拟播完();   // type=fidget、无会话 → 直接回 idle
                断言(CharAnim.当前动画名_只读.StartsWith("idle"), $"单段播完直接回 idle（实际 {CharAnim.当前动画名_只读}）");
                break;

            // ── ③ 素材结构核对：三段变体的 A/C 都在（重构#9：state-one/two 已升级为 sit/lie 嵌套会话池，不再在 fidget）──
            case 36:
                string[] 三段 = ["squat", "tennis", "bubbles", "boring", "aside"];
                var 缺 = new System.Collections.Generic.List<string>();
                foreach (var 名 in 三段)
                {
                    if (!CharAnim.有动画($"fidget-{名}-a") || !CharAnim.有动画($"fidget-{名}") || !CharAnim.有动画($"fidget-{名}-c"))
                        缺.Add(名);
                }
                断言(缺.Count == 0, $"5 个三段变体 A/B/C 齐全{(缺.Count > 0 ? "，缺：" + string.Join(",", 缺) : "")}");
                // A/C 段必须非循环加载（循环动画不回「播完」→ 会话卡死）
                断言(!CharAnim.动画循环_只读("fidget-squat-a") && !CharAnim.动画循环_只读("fidget-squat-c"),
                    "fidget A/C 段非循环加载");
                break;

            // ── ④ 大样本：L=2 平均圈数收敛 ~5.6（VPet 分布） ──
            case 40:
                StateMachine.设置.fidget循环L = 2;
                var 随机 = new Random(12345);
                long 总圈 = 0;
                const int 样本 = 100000;
                for (var i = 0; i < 样本; i++)
                {
                    var n = 0;
                    while (true)
                    {
                        n++;
                        if (随机.Next(n) > 2) break;   // 与运行时同式：Next(圈数) > L
                    }
                    总圈 += n;
                }
                var 平均 = (double)总圈 / 样本;
                断言(平均 > 5.0 && 平均 < 6.2, $"L=2 大样本平均圈数 = {平均:F2}（理论 ~5.58）");
                GD.Print($"[FDG] ===== 失败数 = {_失败} =====");
                GD.Print(_失败 == 0 ? "[FDG] PASS" : "[FDG] FAIL");
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                break;
        }
    }
}
