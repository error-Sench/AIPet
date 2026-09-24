using System;
using System.Collections.Generic;
using System.Text.Json;
using desktop.script.Logic;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 坐卧嵌套会话探针（headless，重构#9）：验证 VPet StateONE/StateTWO「嵌套待机场」机制——
/// ① 档位主名（sit-nomal / lie-nomal）与 B 变体列表（每圈随机换一个，取自主名-bN）；
/// ② sit：A → B 循环（圈数递增；退出骰不中继续）→ 命中后 1/(2+已躺次数) 进 lie 或收场；
/// ③ lie：A → B 循环 → 命中 → C 起身 → **回 sit 的 B 判定**（VPet 嵌套的照抄点，不是直接收场）；
/// ④ sit 收场：C 播完 → 会话清空、回 idle；
/// ⑤ 别的状态接管 → 会话作废；
/// ⑥ 爬坡槽位：纯函数分布（移动 3 / 坐卧 2 / 其余无效）+ 与心跳同一条分派路径（探针_掷爬坡一次）；
/// ⑦ behavior.json 坐卧四键加载到位（**读配置比对**——主人会调值：动画组B 已把 坐卧循环L 2→4，
///    硬编码数字会随调值假红；存在 user:// 覆盖时 SKIP，覆盖优先是设计行为，见 tests/README.md 踩坑#27）。
/// 两个覆盖钩子（探针_坐卧退出覆盖 / 探针_坐卧躺下覆盖）让「退/不退」「躺/不躺」全流程可复现。
/// 注意：断言一律**实时**读 当前动画名_只读（帧初快照在 模拟播完 之后就是过期值）。用法：
/// Godot_v4.7.2-stable_mono_win64_console.exe --headless --path D:/Games/Github/AIPet res://tests/SitProbe.tscn
/// </summary>
public partial class SitProbe : Node
{
    private int _帧;
    private int _失败;
    private readonly List<string> _播过的B = new();
    private bool _有用户覆盖;   // user:// 有 behavior.json 覆盖 → 配置值断言 SKIP（踩坑#27）

    /// <summary>实时读当前动画名（不要缓存到帧初）。</summary>
    private static string 现 => CharAnim.当前动画名_只读;

    /// <summary>读生效的 behavior.json（候选顺序同 StateMachine.设置.加载：「behavior.json」→「config/behavior.json」，
    /// 每档按 user:// → exe 同目录 → res:// 找第一个存在的）。返回 (路径, 根元素)；找不到/坏 JSON 返回 (null, default)。</summary>
    private static (string 路径, JsonElement 根) 读生效配置()
    {
        var 候选 = new List<string>();
        候选.AddRange(desktop.script.Util.ConfigFile.候选("behavior.json"));
        候选.AddRange(desktop.script.Util.ConfigFile.候选("config/behavior.json"));
        foreach (var 路径 in 候选)
        {
            if (!System.IO.File.Exists(路径)) continue;
            try
            {
                var 文本 = System.IO.File.ReadAllText(路径);
                if (string.IsNullOrWhiteSpace(文本)) continue;
                using var 文档 = JsonDocument.Parse(文本);
                return (路径, 文档.RootElement.Clone());
            }
            catch { }
        }
        return (null, default);
    }

    public override void _Ready()
    {
        Main.探针_禁首启提示 = true;
        MusicSense.启用 = false;
        // 探针隔离：冻结时间驱动（问候/音乐），避免全量回归里被别的日程抢状态（顺序相关抖动）
        StateMachine.设置.探针_冻结时间驱动开关 = true;
        _有用户覆盖 = Godot.FileAccess.FileExists("user://behavior.json") || Godot.FileAccess.FileExists("user://config/behavior.json");
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== SitProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[SIT] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[SIT] FAIL  {描述}"); }
    }

    private void 结束()
    {
        CharAnim.探针_坐卧退出覆盖 = null;
        CharAnim.探针_坐卧躺下覆盖 = null;
        GD.Print($"[SIT] ===== 失败数 = {_失败} =====（帧 {_帧}）");
        GD.Print(_失败 == 0 ? "[SIT] PASS" : "[SIT] FAIL");
        GetTree().Quit(_失败 == 0 ? 0 : 1);
    }

    public override void _Process(double delta)
    {
        _帧++;
        if (_帧 > 900) { 断言(false, $"超时（帧 {_帧}）"); 结束(); return; }

        switch (_帧)
        {
            case 4:
                StateMachine.入场完成();
                break;

            // ── ① 档位主名 + B 变体 + 设置加载 ──
            case 8:
                断言(StateMachine.档名("sit") == "sit-nomal", $"档名(sit) → sit-nomal（实际 {StateMachine.档名("sit")}）");
                断言(StateMachine.档名("lie") == "lie-nomal", $"档名(lie) → lie-nomal（实际 {StateMachine.档名("lie")}）");
                var 变体 = CharAnim.探针_坐卧B变体列表("sit-nomal");
                断言(变体.Count == 2 && 变体.Contains("sit-nomal-b1") && 变体.Contains("sit-nomal-b2"),
                    $"sit-nomal 两个 B 变体（实际 [{string.Join(",", 变体)}]）");
                var 单变 = CharAnim.探针_坐卧B变体列表("lie-nomal");
                断言(单变.Count == 1 && 单变[0] == "lie-nomal-b1", $"lie-nomal 单变体列表退化为 b1（实际 [{string.Join(",", 单变)}]）");
                // ⑦ 坐卧四键加载：**读配置比对**（动画组B：主人会调值——坐卧循环L 已 2→4；硬编码数字会随调值假红）。
                //    存在 user:// 覆盖时 SKIP（覆盖优先是设计行为，不是故障——tests/README.md 踩坑#27）。
                if (_有用户覆盖)
                {
                    GD.Print("[SIT] SKIP  behavior.json 坐卧四键断言：存在 user:// 覆盖（覆盖优先是设计行为，不是故障）");
                }
                else
                {
                    // 小工具：读键并取值（键缺失/类型不对 → false，值保持兜底）
                    static bool 取整(JsonElement 根, string 键, out int 值)
                    {
                        值 = 0;
                        return 根.TryGetProperty(键, out var e) && e.TryGetInt32(out 值);
                    }
                    static bool 取真值(JsonElement 根, string 键, out bool 值)
                    {
                        值 = false;
                        if (!根.TryGetProperty(键, out var e) || e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                        值 = e.GetBoolean();
                        return true;
                    }
                    var (配置路径, 配置根) = 读生效配置();
                    var 启用值 = false;
                    var 槽 = 0;
                    var L = 0;
                    var 基数 = 0;
                    var 有键 = 配置路径 != null
                        && 取真值(配置根, "坐卧启用", out 启用值)
                        && 取整(配置根, "坐卧槽", out 槽)
                        && 取整(配置根, "坐卧循环L", out L)
                        && 取整(配置根, "躺下基数", out 基数);
                    if (!有键) 断言(false, $"behavior.json 坐卧四键缺失/类型不对（{配置路径 ?? "找不到生效配置"}）");
                    else 断言(StateMachine.设置.坐卧启用 == 启用值 && StateMachine.设置.坐卧槽 == 槽
                        && StateMachine.设置.坐卧循环L == L && StateMachine.设置.躺下基数 == 基数,
                        $"behavior.json 坐卧四键加载 = 文件值（文件 {配置路径}：启用={启用值} 槽={槽} L={L} 基数={基数}；内存：启用={StateMachine.设置.坐卧启用} 槽={StateMachine.设置.坐卧槽} L={StateMachine.设置.坐卧循环L} 基数={StateMachine.设置.躺下基数}）");
                }
                断言(!CharAnim.动画循环_只读("sit-nomal-b1") && !CharAnim.动画循环_只读("lie-nomal-a"),
                    "sit/lie 段非循环加载（循环动画不回「播完」→ 会话卡死）");
                break;

            // ── ② 开局：sit A ──
            case 12:
                CharAnim.探针_开始坐卧("sit-nomal");
                断言(现 == "sit-nomal-a", $"会话开启先播 A 段（实际 {现}）");
                断言(CharAnim.坐卧会话_只读 == "sit-nomal", "会话主名钉死 sit-nomal");
                断言(CharAnim.坐卧场_只读 == "sit" && CharAnim.坐卧圈数_只读 == 0 && CharAnim.坐卧次数_只读 == 0,
                    "开局：场=sit、圈数 0、已躺 0");
                break;

            // ── ③ A 播完 → B 第一圈 ──
            case 16:
                CharAnim.探针_模拟播完();
                _播过的B.Add(现);
                断言(CharAnim.坐卧圈数_只读 == 1, $"A 播完圈数=1（实际 {CharAnim.坐卧圈数_只读}）");
                断言(现 is "sit-nomal-b1" or "sit-nomal-b2", $"A 播完接 B 变体（实际 {现}）");
                break;

            // ── ④ 退出骰覆盖=false → 继续蹲（圈数递增，B 循环不走样）──
            case 20:
                CharAnim.探针_坐卧退出覆盖 = false;
                for (var i = 0; i < 5; i++) { CharAnim.探针_模拟播完(); _播过的B.Add(现); }
                断言(CharAnim.坐卧圈数_只读 == 6, $"续 5 圈后圈数=6（实际 {CharAnim.坐卧圈数_只读}）");
                断言(CharAnim.坐卧场_只读 == "sit" && 现.StartsWith("sit-nomal-b"), $"续圈期间仍在 sit 的 B 段（实际 {现}）");
                断言(_播过的B.TrueForAll(n => n is "sit-nomal-b1" or "sit-nomal-b2"), $"B 圈播的全在变体列表里（实际 [{string.Join(",", _播过的B)}]）");
                break;

            // ── ⑤ 退+躺 → 进 lie（次数 +1、圈数清零）──
            case 24:
                CharAnim.探针_坐卧退出覆盖 = true;
                CharAnim.探针_坐卧躺下覆盖 = true;
                CharAnim.探针_模拟播完();
                断言(CharAnim.坐卧场_只读 == "lie", $"命中躺下 → 场=lie（实际 {CharAnim.坐卧场_只读}）");
                断言(CharAnim.坐卧次数_只读 == 1, $"已躺次数 +1（实际 {CharAnim.坐卧次数_只读}）");
                断言(现 == "lie-nomal-a", $"进 lie 播 A 段（实际 {现}）");
                断言(CharAnim.坐卧圈数_只读 == 0, $"进 lie 圈数清零（实际 {CharAnim.坐卧圈数_只读}）");
                break;

            // ── ⑥ lie A → B ──
            case 28:
                CharAnim.探针_模拟播完();
                断言(CharAnim.坐卧圈数_只读 == 1 && 现 == "lie-nomal-b1", $"lie A 播完接 B（实际 {现}，圈 {CharAnim.坐卧圈数_只读}）");
                break;

            // ── ⑦ lie 命中 → C 起身 ──
            case 32:
                CharAnim.探针_模拟播完();
                断言(现 == "lie-nomal-c", $"lie 命中后播 C 起身（实际 {现}）");
                断言(CharAnim.坐卧场_只读 == "lie", "C 起身时还在 lie 场（判场用）");
                break;

            // ── ⑧ 照抄点：lie C 播完 → 回 sit 的 B 判定（不是直接收场）──
            case 36:
                CharAnim.探针_坐卧退出覆盖 = null;
                CharAnim.探针_模拟播完();
                断言(CharAnim.坐卧场_只读 == "sit", $"lie C 播完回坐（实际场 {CharAnim.坐卧场_只读}）");
                断言(CharAnim.坐卧圈数_只读 == 1, $"回坐后圈数=1（B 判定重置，实际 {CharAnim.坐卧圈数_只读}）");
                断言(现.StartsWith("sit-nomal-b"), $"回坐后直接进 B 循环（实际 {现}）");
                break;

            // ── ⑨ 退+不躺 → sit 收场 C ──
            case 40:
                CharAnim.探针_坐卧退出覆盖 = true;
                CharAnim.探针_坐卧躺下覆盖 = false;
                CharAnim.探针_模拟播完();
                断言(现 == "sit-nomal-c", $"不躺 → 播 C 收场（实际 {现}）");
                断言(CharAnim.坐卧场_只读 == "sit", "收场时场仍=sit");
                break;

            // ── ⑩ C 播完 → 会话清空回 idle ──
            case 44:
                CharAnim.探针_模拟播完();
                断言(CharAnim.坐卧会话_只读 == null && CharAnim.坐卧场_只读 == null, "收场后会话清空");
                断言(现.StartsWith("idle"), $"收场回 idle（实际 {现}）");
                break;

            // ── ⑪ 别的状态接管 → 会话作废 ──
            case 48:
                CharAnim.探针_开始坐卧("sit-happy");
                断言(CharAnim.坐卧会话_只读 == "sit-happy", $"直接开 happy 档会话（实际 {CharAnim.坐卧会话_只读}）");
                StateMachine.SetState(StateMachine.Think);
                断言(CharAnim.坐卧会话_只读 == null, "别人抢状态 → 会话作废");
                break;
            case 52:
                // 注：think 是包裹段，回 idle 要等「排队 → 段播完/排队兜底」——那是状态机自己的语义，
                // 本探针只关心坐卧会话不复活、不卡死（表现归状态机）。
                断言(!CharAnim.坐卧会话中, "接管几帧后会话仍是清空态（不会自己复活）");
                break;

            // ── ⑫ 分派集成：槽位配置成「必中坐卧」/「坐卧关闭」两条确定性路径 ──
            case 56:
                StateMachine.设置.爬坡移动槽 = 0;
                StateMachine.设置.坐卧槽 = 20;
                StateMachine.设置.爬坡下限 = 20;
                StateMachine.设置.爬坡周期 = 20;
                StateMachine.探针_掷爬坡一次();
                断言(CharAnim.坐卧会话中, "全槽坐卧配置 → 掷一次必开坐卧会话");
                break;
            case 60:
                CharAnim.作废坐卧会话();
                StateMachine.设置.爬坡移动槽 = 3;
                StateMachine.设置.坐卧槽 = 2;
                StateMachine.设置.爬坡下限 = 20;
                StateMachine.设置.爬坡周期 = 200;
                StateMachine.设置.坐卧启用 = false;
                StateMachine.探针_掷爬坡一次();
                断言(!CharAnim.坐卧会话中, "坐卧启用=false → 掷多少次都不开坐卧");
                StateMachine.设置.坐卧启用 = true;
                break;

            // ── ⑬ 纯函数：槽位分布（大样本收敛） + 爬坡掷骰 与 槽位 一致 ──
            case 64:
                var 随 = new Random(20260922);
                long 移动 = 0, 坐卧 = 0;
                const int 样本 = 200000;
                for (var i = 0; i < 样本; i++)
                {
                    var 槽 = StateMachine.爬坡掷槽(0, 随);
                    if (槽 < 3) 移动++;
                    else if (槽 < 5) 坐卧++;
                }
                var 移动率 = 移动 * 1000 / 样本;
                var 坐卧率 = 坐卧 * 1000 / 样本;
                断言(移动率 is >= 11 and <= 19, $"移动槽占比 ≈15‰（实际 {移动率}‰）");
                断言(坐卧率 is >= 6 and <= 14, $"坐卧槽占比 ≈10‰（实际 {坐卧率}‰）");
                // 同一颗种子：爬坡掷骰 必须恒等于（槽 < 移动槽）
                var a = new Random(7);
                var b = new Random(7);
                var 一致 = true;
                for (var i = 0; i < 500; i++)
                    if (StateMachine.爬坡掷骰(60, a) != (StateMachine.爬坡掷槽(60, b) < StateMachine.设置.爬坡移动槽)) 一致 = false;
                断言(一致, "爬坡掷骰 ≡（爬坡掷槽 < 爬坡移动槽）（同种子 500 次）");
                break;

            case 68:
                结束();
                break;
        }
    }
}
