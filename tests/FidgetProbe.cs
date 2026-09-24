using System;
using desktop.script.Logic;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// fidget 会话探针（headless，重构#2 + 动画组B「循环节奏修正」）：
/// ① 三段结构变体（squat 等）：A 播完接 B、B 播完圈数递增、骰子命中播 C、C 播完回 idle；
/// ② 首圈恒不过线（Next(1)=0）：会话至少播一圈 B；
/// ③ 单段变体（spin 等 Single 型）：不开会话，播完直接回 idle；
/// ④ fidget循环L 大样本：平均圈数收敛（VPet DisplayBLoopingToNomal 分布，经变体有效 L 查表）；
/// ⑤ 动画组B：amuse 单段循环——每次触发重播 N∈[2,5] 遍（纯函数大样本 + 端到端实跑）；其他单段仍一次过；
/// ⑥ 动画组B：squat 的「fidget循环L覆盖」在运行时生效（覆盖/全局两向确定性验证 + 平均圈数 > 全局）。
/// 用 探针_模拟播完() 驱动（不靠实时）。用法：
/// Godot_v4.7.2-stable_mono_win64_console.exe --headless --path D:/Games/Github/AIPet res://tests/FidgetProbe.tscn
/// </summary>
public partial class FidgetProbe : Node
{
    private int _帧;
    private int _失败;
    private int _squat覆盖快照;      // 开场记录「fidget循环L覆盖.squat」与全局 L（探针中途会改，结尾/中途还原）
    private int _全局L快照;
    private int _amuse最小;          // 「fidget单段循环.amuse」区间（case 44 读，case 48 用）
    private int _amuse最大;
    private bool _有用户覆盖;        // user:// 有 behavior.json 覆盖 → 配置值断言 SKIP（踩坑#27）

    public override void _Ready()
    {
        Main.探针_禁首启提示 = true;
        MusicSense.启用 = false;
        // 探针隔离：冻结时间驱动（问候/音乐）——否则共享存档下被每日问候抢状态（全量回归顺序相关抖动，实测）
        StateMachine.设置.探针_冻结时间驱动开关 = true;
        _有用户覆盖 = Godot.FileAccess.FileExists("user://behavior.json") || Godot.FileAccess.FileExists("user://config/behavior.json");
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

    /// <summary>配置值断言：存在 user:// 覆盖时 SKIP（覆盖优先是设计行为，不是故障——踩坑#27）。</summary>
    private void 断言配置(bool 条件, string 描述)
    {
        if (_有用户覆盖) { GD.Print($"[FDG] SKIP  {描述}（user:// 有 behavior.json 覆盖）"); return; }
        断言(条件, 描述);
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
                _squat覆盖快照 = StateMachine.设置.fidgetL("squat");
                _全局L快照 = StateMachine.设置.fidget循环L;
                GD.Print($"[FDG] 配置：全局 fidget循环L={_全局L快照}，squat 覆盖={_squat覆盖快照}");
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
                // 覆盖 L 调到 0：第 2 圈掷 Next(2)∈{0,1} > 0 半概率退；反复模拟播完直到进 C（最多 50 圈防爆）。
                // 动画组B：squat 在 behavior.json 里有覆盖 L，所以这里改的是**覆盖值**（全局值留给 case 40/56 用）。
                StateMachine.设置.fidget循环L覆盖["squat"] = 0;
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
                StateMachine.设置.fidget循环L覆盖["squat"] = _squat覆盖快照;   // 还原覆盖（后面 case 44 要读配置口径）
                break;

            // ── ② 单段变体（Single 型）：不开会话，播完直接回 idle ──
            case 28:
                // spin 是原项目单段素材（无 -a）：手动播它并模拟会话外的 fidget 完成路径
                CharAnim.探针_fidget单段("fidget-spin");
                断言(CharAnim.当前动画名_只读 == "fidget-spin", $"单段变体直接播（实际 {CharAnim.当前动画名_只读}）");
                断言(CharAnim.fidget单段会话_只读 == null, "无「单段循环」配置 → 不开会话（一次过）");
                break;
            case 32:
                断言(CharAnim.fidget会话_只读 == null, "单段变体不开三段会话");
                CharAnim.探针_模拟播完();   // type=fidget、无会话 → 直接回 idle
                断言(CharAnim.当前动画名_只读.StartsWith("idle"), $"单段播完直接回 idle（实际 {CharAnim.当前动画名_只读}）");
                break;

            // ── ③ 素材结构核对：三段变体的 A/C 都在；amuse 是单段（无 -a/-c）──
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
                断言(CharAnim.有动画("fidget-amuse") && !CharAnim.有动画("fidget-amuse-a") && !CharAnim.有动画("fidget-amuse-c"),
                    "amuse 是单段变体（有主段、无 -a/-c）");
                break;

            // ── ④ 大样本：L=2 平均圈数收敛 ~5.58（VPet 分布；经变体有效 L 查表——tennis 无覆盖走全局）──
            case 40:
                StateMachine.设置.fidget循环L = 2;
                var 随机 = new Random(12345);
                long 总圈 = 0;
                const int 样本 = 100000;
                for (var i = 0; i < 样本; i++) 总圈 += CharAnim.探针_模拟fidget圈数("fidget-tennis", 随机);
                var 平均 = (double)总圈 / 样本;
                断言(平均 > 5.0 && 平均 < 6.2, $"L=2 大样本平均圈数 = {平均:F2}（理论 ~5.58）");
                break;

            // ── ⑤a 动画组B：amuse 单段循环——区间口径 + 纯函数大样本 ──
            case 44:
                (_amuse最小, _amuse最大) = StateMachine.设置.fidget单段区间("amuse");
                GD.Print($"[FDG] 「fidget单段循环」amuse = [{_amuse最小},{_amuse最大}]；「fidget循环L覆盖」squat={StateMachine.设置.fidgetL("squat")} vs 全局={StateMachine.设置.fidget循环L}");
                断言配置(_amuse最小 == 2 && _amuse最大 == 5, $"口径：amuse 循环次数 = [2,5]（实际 [{_amuse最小},{_amuse最大}]）");
                断言配置(StateMachine.设置.fidgetL("squat") > StateMachine.设置.fidget循环L,
                    $"口径：squat 覆盖 L > 全局（主人「蹲下 B 段循环做久一点」；squat={StateMachine.设置.fidgetL("squat")} 全局={StateMachine.设置.fidget循环L}）");
                var 随机2 = new Random(20260924);
                var 最小观 = int.MaxValue;
                var 最大观 = 0;
                long 总次 = 0;
                const int 样本2 = 100000;
                for (var i = 0; i < 样本2; i++)
                {
                    var n = CharAnim.探针_掷单段次数("fidget-amuse", 随机2);
                    最小观 = Math.Min(最小观, n);
                    最大观 = Math.Max(最大观, n);
                    总次 += n;
                }
                var 均次 = (double)总次 / 样本2;
                var 期望均 = (_amuse最小 + _amuse最大) / 2.0;
                断言(最小观 == _amuse最小 && 最大观 == _amuse最大, $"amuse 掷次数大样本落在配置区间（实测 {最小观}~{最大观}，配置 [{_amuse最小},{_amuse最大}]）");
                断言(Math.Abs(均次 - 期望均) < 0.2, $"amuse 平均次数 ≈ 区间中点（实测 {均次:F2}，期望 {期望均:F1}）");
                break;

            // ── ⑤b 动画组B：amuse 端到端——真会话重播 N 遍后回 idle（300 次触发统计）──
            case 48:
                var 最少遍 = int.MaxValue;
                var 最多遍 = 0;
                long 总遍 = 0;
                const int 触发 = 300;
                for (var i = 0; i < 触发; i++)
                {
                    CharAnim.探针_fidget单段("fidget-amuse");
                    var 遍 = 0;
                    while (CharAnim.当前动画名_只读 == "fidget-amuse" && 遍 < 20)
                    {
                        CharAnim.探针_模拟播完();
                        遍++;
                    }
                    最少遍 = Math.Min(最少遍, 遍);
                    最多遍 = Math.Max(最多遍, 遍);
                    总遍 += 遍;
                }
                断言(最少遍 >= _amuse最小 && 最多遍 <= _amuse最大, $"amuse 端到端 300 次触发：每次遍数 ∈[{_amuse最小},{_amuse最大}]（实测 {最少遍}~{最多遍}，平均 {(double)总遍 / 触发:F2}）");
                断言(最少遍 == _amuse最小 && 最多遍 == _amuse最大, $"amuse 端到端覆盖区间两端（实测 {最少遍}~{最多遍}）");
                断言(CharAnim.当前动画名_只读.StartsWith("idle") && CharAnim.fidget单段会话_只读 == null,
                    $"amuse 播完回 idle、会话清空（实际 {CharAnim.当前动画名_只读}）");
                break;

            // ── ⑤c 动画组B：其他单段变体仍一次过（有主段、无配置条目）──
            case 52:
                string[] 单段 = ["fidget-bubble", "fidget-doze", "fidget-happy520", "fidget-meowlook", "fidget-spin", "fidget-yawning"];
                var 坏 = new System.Collections.Generic.List<string>();
                foreach (var 名 in 单段)
                {
                    CharAnim.探针_fidget单段(名);
                    if (CharAnim.当前动画名_只读 != 名 || CharAnim.fidget单段会话_只读 != null) { 坏.Add($"{名}(播={CharAnim.当前动画名_只读}/会话={CharAnim.fidget单段会话_只读 ?? "无"})"); continue; }
                    CharAnim.探针_模拟播完();
                    if (!CharAnim.当前动画名_只读.StartsWith("idle")) 坏.Add($"{名}(播完={CharAnim.当前动画名_只读})");
                }
                断言(坏.Count == 0, $"6 个单段变体仍一次过{(坏.Count > 0 ? "，坏：" + string.Join(",", 坏) : "")}");
                break;

            // ── ⑥a 动画组B：覆盖 L 优先级（确定性前后界——探针自设值，不依赖具体配置）──
            case 56:
                // 覆盖=20、全局=0：退出需 圈数 ≥ L+2 = 22 → 前 8 圈不可能退（若被全局 0 顶掉会当场退，必红）
                StateMachine.设置.fidget循环L覆盖["squat"] = 20;
                StateMachine.设置.fidget循环L = 0;
                CharAnim.探针_fidget会话("fidget-squat");
                var 推 = 0;
                while (CharAnim.fidget圈数_只读 < 8 && 推 < 40) { CharAnim.探针_模拟播完(); 推++; }
                断言(CharAnim.fidget圈数_只读 == 8 && CharAnim.当前动画名_只读 == "fidget-squat",
                    $"覆盖 L=20 > 全局 0：8 圈仍在 B（实际 圈={CharAnim.fidget圈数_只读} 现={CharAnim.当前动画名_只读}）");
                // 覆盖=0、全局=20：第 2 圈起必退（P(15 圈内不退)=1/15!≈0；若被全局 20 顶掉要 22 圈才可能退，必红）
                StateMachine.设置.fidget循环L覆盖["squat"] = 0;
                StateMachine.设置.fidget循环L = 20;
                CharAnim.探针_fidget会话("fidget-squat");
                var 掷 = 0;
                while (CharAnim.当前动画名_只读 != "fidget-squat-c" && 掷 < 30)
                {
                    CharAnim.探针_模拟播完();
                    掷++;
                }
                断言(CharAnim.当前动画名_只读 == "fidget-squat-c" && CharAnim.fidget圈数_只读 <= 15,
                    $"覆盖 L=0 < 全局 20：15 圈内必退（实际 圈={CharAnim.fidget圈数_只读} 现={CharAnim.当前动画名_只读}）");
                StateMachine.设置.fidget循环L = _全局L快照;
                StateMachine.设置.fidget循环L覆盖["squat"] = _squat覆盖快照;
                break;

            // ── ⑥b 动画组B：平均圈数——覆盖 L 生效（探针自设 覆盖=全局+2，大样本；不依赖具体配置值）──
            case 60:
                var 覆盖原 = StateMachine.设置.fidget循环L覆盖.TryGetValue("squat", out var 原值) ? 原值 : (int?)null;
                StateMachine.设置.fidget循环L覆盖["squat"] = StateMachine.设置.fidget循环L + 2;
                var 随机A = new Random(20260924);
                var 随机B = new Random(20260924);
                long 总覆盖 = 0, 总全局 = 0;
                const int 样本3 = 100000;
                for (var i = 0; i < 样本3; i++)
                {
                    总覆盖 += CharAnim.探针_模拟fidget圈数("fidget-squat", 随机A);
                    总全局 += CharAnim.探针_模拟fidget圈数("fidget-tennis", 随机B);
                }
                var 均覆盖 = (double)总覆盖 / 样本3;
                var 均全局 = (double)总全局 / 样本3;
                断言(均覆盖 > 均全局 + 1.0, $"覆盖 L（全局+2）平均圈数 > 全局：squat {均覆盖:F2} vs tennis {均全局:F2}");
                if (覆盖原.HasValue) StateMachine.设置.fidget循环L覆盖["squat"] = 覆盖原.Value;
                else StateMachine.设置.fidget循环L覆盖.Remove("squat");
                // ⑥c 无覆盖变体走全局 L（全局路径也在用）：全局=0 → tennis 会话 15 圈内必退
                StateMachine.设置.fidget循环L = 0;
                CharAnim.探针_fidget会话("fidget-tennis");
                var 掷2 = 0;
                while (CharAnim.当前动画名_只读 != "fidget-tennis-c" && 掷2 < 30)
                {
                    CharAnim.探针_模拟播完();
                    掷2++;
                }
                断言(CharAnim.当前动画名_只读 == "fidget-tennis-c" && CharAnim.fidget圈数_只读 <= 15,
                    $"无覆盖变体用全局 L=0：15 圈内必退（实际 圈={CharAnim.fidget圈数_只读} 现={CharAnim.当前动画名_只读}）");
                StateMachine.设置.fidget循环L = _全局L快照;
                GD.Print($"[FDG] ===== 失败数 = {_失败} =====");
                GD.Print(_失败 == 0 ? "[FDG] PASS" : "[FDG] FAIL");
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                break;
        }
    }
}
