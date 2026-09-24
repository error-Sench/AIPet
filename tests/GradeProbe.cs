using System;
using System.Collections.Generic;
using System.Linq;
using desktop.script.Logic;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 档位降级链探针（headless，重构#6）：验证 VPet GraphCore.FindGraphs 式的择档降级——
/// ① 默认（三档关）钉 nomal 档：idle 不再整池随机串到 happy/poor（落实「默认普通」口径）；
/// ② 精确档命中（开心→think-happy、不良→think-poor）；
/// ③ 降级链：档位素材缺失 → 相邻档兜底（say 池无档位素材 → 无档基名；walk poor→nomal 方向）；
/// ④ 段排除：拆段池（fidget/sleep）择档不挑 -a/-c 过渡段；
/// ⑤ Ill 不引入：降级链里没有任何 ill 档。
/// ⑧ idle 变体加权（动画组D / Plan #24）：三档关全落 nomal；同档组内按「idle权重」加权随机
/// （大样本分布；0 权重排除、全名键、三档开档内加权与零串档）。
/// Godot_v4.7.2-stable_mono_win64_console.exe --headless --path D:/Games/Github/AIPet res://tests/GradeProbe.tscn
/// </summary>
public partial class GradeProbe : Node
{
    private int _帧;
    private int _失败;

    public override void _Ready()
    {
        Main.探针_禁首启提示 = true;
        MusicSense.启用 = false;
        StateMachine.设置.问候启用 = false;
        DailyRoutine.问候启用 = false;
        StateMachine.设置.磁盘提醒启用 = false;
        DailyRoutine.磁盘提醒启用 = false;
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== GradeProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[GRADE] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[GRADE] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 4:
                StateMachine.入场完成();
                break;

            // ── ① 默认（三档关）：idle 钉 nomal，不串档 ──
            case 8:
                StateMachine.设置.三档状态启用 = false;
                StateMachine.设置.状态档位 = "普通";
                var 串档 = 0;
                for (var i = 0; i < 200; i++)
                {
                    var 名 = StateMachine.挑主名("idle");
                    if (名 != null && (名.Contains("-happy-", StringComparison.Ordinal) || 名.Contains("-poor-", StringComparison.Ordinal)))
                        串档++;
                }
                断言(串档 == 0, $"三档关：idle 200 次择档零串档（全落 nomal 组；实际串档 {串档}）");
                break;

            // ── ② 精确档命中 ──
            case 12:
                StateMachine.设置.三档状态启用 = true;
                StateMachine.设置.状态档位 = "开心";
                断言(StateMachine.挑主名("think") == "think-happy", $"开心档 → think-happy（实际 {StateMachine.挑主名("think")}）");
                StateMachine.设置.状态档位 = "不良";
                断言(StateMachine.挑主名("think") == "think-poor", $"不良档 → think-poor（实际 {StateMachine.挑主名("think")}）");
                StateMachine.设置.状态档位 = "普通";
                断言(StateMachine.挑主名("think") == "think-nomal", $"普通档 → think-nomal（实际 {StateMachine.挑主名("think")}）");
                break;

            // ── ③ 降级：say 池没有档位素材 → 无档基名（self/serious/shy/smile）兜底 ──
            case 16:
                StateMachine.设置.状态档位 = "开心";   // 要 happy，但 say 只有无档名
                var say名 = StateMachine.挑主名("say");
                断言(say名 != null && !say名.EndsWith("-a") && !say名.EndsWith("-c")
                        && !say名.Contains("-happy", StringComparison.Ordinal),
                    $"say 无档位素材 → 降级到无档基名（实际 {say名}）");
                // sleep：有 loop（无档）与 happy 组——普通档应落 sleep-loop 而不是升到 sleep-happy
                StateMachine.设置.状态档位 = "普通";
                StateMachine.设置.三档状态启用 = false;
                var sleep名 = StateMachine.挑主名("sleep");
                断言(sleep名 == "sleep-loop", $"sleep 普通档 → 无档基名 sleep-loop（实际 {sleep名}）");
                break;

            // ── ④ 段排除：sleep/fidget 择档不挑 -a/-c ──
            case 20:
                var 段漏 = 0;
                for (var i = 0; i < 200; i++)
                {
                    foreach (var 池 in new[] { "sleep", "fidget", "walk", "work" })
                    {
                        var 名 = StateMachine.挑主名(池);
                        if (名 != null && (名.EndsWith("-a", StringComparison.Ordinal) || 名.EndsWith("-c", StringComparison.Ordinal)))
                            段漏++;
                    }
                }
                断言(段漏 == 0, $"拆段池 800 次择档零段漏（-a/-c 全排除；实际 {段漏}）");
                break;

            // ── ⑤ 降级链无 Ill（VPet 有第 4 档，我们刻意不引入） ──
            case 24:
                var 有ill = false;
                foreach (var 档 in new[] { "happy", "nomal", "poor" })
                    foreach (var 降 in StateMachine.降级链(档))
                        if (降.Contains("ill", StringComparison.OrdinalIgnoreCase)) 有ill = true;
                断言(!有ill, "降级链不含 ill 档（无生病玩法，Ill 素材未导入——决策记录见 VPet分析 §7.6）");
                // 链顺序对齐 VPet ModeType 序号（Happy0↔Nomal1↔Poor2 相邻降级）
                断言(StateMachine.降级链("happy")[1] == "nomal" && StateMachine.降级链("poor")[1] == "nomal"
                        && StateMachine.降级链("nomal")[1] == "poor" && StateMachine.降级链("nomal")[2] == "happy",
                    "降级链顺序 = VPet 相邻档（happy→nomal、poor→nomal、nomal→poor→happy）");
                break;

            // ── ⑥ WORK 档位素材（重构#7）+ 段名同类降级 ──
            case 26:
                StateMachine.设置.三档状态启用 = true;
                StateMachine.设置.状态档位 = "开心";
                var w名 = StateMachine.挑主名("work");
                断言(w名 != null && w名.StartsWith("work-happy-", StringComparison.Ordinal),
                    $"开心档 work → work-happy-*（实际 {w名}）");
                StateMachine.设置.状态档位 = "不良";
                var w名2 = StateMachine.挑主名("work");
                断言(w名2 != null && w名2.StartsWith("work-poor-", StringComparison.Ordinal),
                    $"不良档 work → work-poor-*（实际 {w名2}）");
                StateMachine.设置.三档状态启用 = false;
                var w名3 = StateMachine.挑主名("work");
                断言(w名3 != null && !w名3.Contains("-happy-", StringComparison.Ordinal)
                        && !w名3.Contains("-poor-", StringComparison.Ordinal),
                    $"普通档 work → 无档基名（实际 {w名3}）");
                断言(StateMachine.探针_段名("work-happy-calligraphy", "a") == "work-happy-calligraphy-a",
                    "档位段精确命中（calligraphy Happy A）");
                断言(StateMachine.探针_段名("work-happy-study2", "c") == "work-study2-c",
                    "档位段缺失 → 同类无档降级（study2 Happy 无 C 源 → Nomal C）");
                断言(StateMachine.探针_段名("sleep-happy", "a") == "sleep-happy-a",
                    "既有段解析不受新降级影响（sleep-happy-a）");
                break;

            // ── ⑦ greet 池三档（2026-09-24 动画组A：整池换 Meow 9 变体 `{档}-{n}`，与 idle 同口径）──
            case 28:
                StateMachine.设置.三档状态启用 = false;   // 默认 = 普通档
                var g串档 = 0;
                for (var i = 0; i < 100; i++)
                {
                    var 名 = StateMachine.挑主名("greet");
                    if (名 == null || !名.StartsWith("greet-nomal-", StringComparison.Ordinal)) g串档++;
                }
                断言(g串档 == 0, $"三档关：greet 100 次择档全落 nomal 组（实际串档 {g串档}）");
                StateMachine.设置.三档状态启用 = true;
                StateMachine.设置.状态档位 = "开心";
                var g名 = StateMachine.挑主名("greet");
                断言(g名 != null && g名.StartsWith("greet-happy-", StringComparison.Ordinal),
                    $"开心档 greet → greet-happy-*（实际 {g名}）");
                StateMachine.设置.状态档位 = "不良";
                var g名2 = StateMachine.挑主名("greet");
                断言(g名2 != null && g名2.StartsWith("greet-poor-", StringComparison.Ordinal),
                    $"不良档 greet → greet-poor-*（实际 {g名2}）");
                StateMachine.设置.三档状态启用 = false;
                StateMachine.设置.状态档位 = "普通";
                break;

            // ── ⑧ idle 变体加权（2026-09-24 动画组D / Plan #24：同档内加权随机，不再均匀概览）──
            //   口径：主人口径「idle 变体不要均匀随机——要有主次/权重（比如 nomal 晃头为主，其他低频）」；
            //   审视野「我们默认为普通级，切换要留」——档的选择不动（降级链），权重只在同档组内生效。
            case 30:
                StateMachine.设置.三档状态启用 = false;   // 关 = nomal 绝对为主
                StateMachine.设置.状态档位 = "普通";
                const int 样本 = 3000;
                var 计数 = new Dictionary<string, int>();
                for (var i = 0; i < 样本; i++)
                {
                    var 名 = StateMachine.挑主名("idle") ?? "(null)";
                    计数[名] = 计数.GetValueOrDefault(名) + 1;
                }
                var nomal数 = 计数.Where(kv => kv.Key.Contains("-nomal-", StringComparison.Ordinal)).Sum(kv => kv.Value);
                断言(nomal数 * 2 > 样本, $"三档关：idle {样本} 次选择 nomal 系占比 > 50%（实际 {nomal数 * 100.0 / 样本:F1}%）");
                断言(nomal数 == 样本, $"三档关：idle 全落 nomal 组、零串档（实际落别组 {样本 - nomal数} 次）");
                var 主次 = 计数.Where(kv => kv.Key.Contains("-nomal-", StringComparison.Ordinal))
                    .OrderByDescending(kv => kv.Value).ToList();
                var 主名0 = 主次.Count > 0 ? 主次[0].Key : "无";
                var 主数0 = 主次.Count > 0 ? 主次[0].Value : 0;
                断言(主数0 > 0.4 * 样本, $"idle 加权「主次」生效：最高占比变体 > 40%（出货表 nomal-1 ≈ 50%；均匀随机约 33%）——实际 {主名0} {主数0 * 100.0 / 样本:F1}%");

                // 机制自证（不依赖出货权重值）：临时换已知表 → 0 权重排除 + 全名键 + 段内分布
                var 旧表 = StateMachine.设置.idle权重;
                StateMachine.设置.idle权重 = new Dictionary<string, int> { ["nomal-1"] = 0, ["nomal-2"] = 1, ["nomal-3"] = 1 };
                var 计数2 = new Dictionary<string, int>();
                for (var i = 0; i < 样本; i++)
                {
                    var 名 = StateMachine.挑主名("idle") ?? "(null)";
                    计数2[名] = 计数2.GetValueOrDefault(名) + 1;
                }
                var n1 = 计数2.GetValueOrDefault("idle-nomal-1");
                var n2 = 计数2.GetValueOrDefault("idle-nomal-2");
                var n3 = 计数2.GetValueOrDefault("idle-nomal-3");
                断言(n1 == 0, $"权重 0 = 该变体不参与（nomal-1 置 0 → {样本} 次 0 出现；实际 {n1}）");
                断言(n2 + n3 == 样本 && n2 > 0.4 * 样本 && n3 > 0.4 * 样本,
                    $"权重 1:1 组内≈均匀且无串档（nomal-2 {n2} / nomal-3 {n3}）");
                StateMachine.设置.idle权重 = new Dictionary<string, int> { ["idle-nomal-3"] = 9 };   // 全名键 + 其余缺省 1
                var 计数3 = new Dictionary<string, int>();
                for (var i = 0; i < 样本; i++)
                {
                    var 名 = StateMachine.挑主名("idle") ?? "(null)";
                    计数3[名] = 计数3.GetValueOrDefault(名) + 1;
                }
                var m3 = 计数3.GetValueOrDefault("idle-nomal-3");
                断言(m3 > 0.7 * 样本, $"全名键 + 缺省权重 1：idle-nomal-3=9 占比 ≈ 82%（实际 {m3 * 100.0 / 样本:F1}%）");
                StateMachine.设置.idle权重 = 旧表;   // 还原（后续/实机口径以配置为准）

                StateMachine.设置.三档状态启用 = true;   // 开 = 按心情档内加权（同样零串档）
                StateMachine.设置.状态档位 = "开心";
                var 串 = 0;
                for (var i = 0; i < 样本; i++)
                {
                    var 名 = StateMachine.挑主名("idle") ?? "";
                    if (!名.StartsWith("idle-happy-", StringComparison.Ordinal)) 串++;
                }
                断言(串 == 0, $"三档开（开心）：idle {样本} 次全落 happy 组（按心情档内加权；实际串档 {串}）");
                StateMachine.设置.三档状态启用 = false;
                StateMachine.设置.状态档位 = "普通";
                break;

            case 32:
                GD.Print($"[GRADE] ===== 失败数 = {_失败} =====");
                GD.Print(_失败 == 0 ? "[GRADE] PASS" : "[GRADE] FAIL");
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                break;
        }
    }
}
