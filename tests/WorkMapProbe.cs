using System;
using System.Collections.Generic;
using System.Linq;
using desktop.script.Logic;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// WORK 语义映射探针（headless，动画组I）：验证「Agent/用户指定工作类型 → 干活会话固定播该类型素材」——
/// ① 映射表（10 个中文类型名；13 种 work 素材里除 water 外全覆盖；每个键都能挑出主名）；
/// ② 指定类型挑名（三档关确定性：创作 → work-calligraphy；多素材类型 remove/write、read/study2 两种都要出得来）；
/// ③ 三档降级（开心 → work-happy-calligraphy；声音 = WorkTWO 无 Happy 源 → 退无档基名 work-pc）；
/// ④ 包裹段流程：开始干活(创作) → WorkIn(起身) → working 先播 work-calligraphy-a → 主段钉死 work-calligraphy
///    → 收工播 work-calligraphy-c → 延迟落地；
/// ⑤ 回退随机：没给类型 / 没映射类型 → 各 12 轮会话里观察到 ≥3 种类型（= 没被钉死）。
/// 用法：Godot_v4.7.2-stable_mono_win64_console.exe --headless --path D:/Games/Github/AIPet res://tests/WorkMapProbe.tscn
/// </summary>
public partial class WorkMapProbe : Node
{
    private const int 每模式轮数 = 12;
    private int _帧;
    private int _失败;
    private int _轮;            // 回退随机：每模式 12 轮
    private int _模式;          // 0 = 没给类型；1 = 没映射类型
    private int _子步;          // 每轮 4 子步：发起 → 推进到 working 读 A → 收工验 C → 落地
    private readonly List<string> _未指定样本 = new();
    private readonly List<string> _无映射样本 = new();

    /// <summary>从包裹段名里取「类型段」：work-calligraphy-a → calligraphy；work-happy-calligraphy-c → happy-calligraphy。</summary>
    private static string 类型段(string 动画名, string 段)
    {
        const string 头 = "work-";
        var 尾 = "-" + 段;
        if (!动画名.StartsWith(头, StringComparison.Ordinal) || !动画名.EndsWith(尾, StringComparison.Ordinal)) return "";
        return 动画名[头.Length..^尾.Length];
    }

    public override void _Ready()
    {
        Main.探针_禁首启提示 = true;                       // 与首启提示互不打扰
        // 隔离「时间驱动」（问候/磁盘/音乐）——与其它探针同规矩（共享存档下问候气泡会抢状态；
        // 本探针要反复在 idle ↔ work 之间走，问候中途插进来会把落地断言顶掉）
        StateMachine.设置.探针_冻结时间驱动开关 = true;
        MusicSense.启用 = false;
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== WorkMapProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[WMAP] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[WMAP] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        var 动画 = CharAnim.当前动画名_只读;
        switch (_帧)
        {
            case 4:
                StateMachine.入场完成();
                断言(!StateMachine.入场未完成_只读, "入场门已解除");
                break;

            case 6: 映射表组(); break;
            case 8: 挑名组(); break;
            case 10: 三档降级组(); break;

            // ── ④ 包裹段流程：指定类型（创作）→ 起身 → A 段 → 主段钉死 → C 段 → 落地 ──
            case 12:
                StateMachine.开始干活("创作");
                break;
            case 14:
                断言(StateMachine.CurrentState == StateMachine.WorkIn, $"指定类型开工先走起身（当前 {StateMachine.CurrentState}）");
                断言(动画 == "switch-up", $"起身动画 = switch-up（实际 {动画}）");
                StateMachine.探针_推进时间(2f);   // 起身到点 → 落 working（包裹入场同步挑名）
                break;
            case 16:
                断言(StateMachine.CurrentState == StateMachine.Working, $"起身到点落 working（当前 {StateMachine.CurrentState}）");
                断言(StateMachine.包裹中, "干活进入开包裹会话");
                断言(动画 == "work-calligraphy-a", $"进入先播指定类型的 A 段（实际 {动画}）");
                StateMachine.探针_推进时间(0.1f); StateMachine.探针_动画播完回调();   // A 播完 → 接主段
                break;
            case 18:
                断言(动画 == "work-calligraphy", $"主段钉死指定类型 = work-calligraphy（实际 {动画}）");
                StateMachine.探针_推进时间(0.1f); StateMachine.探针_动画播完回调();   // 主段循环播完 → 不换变体
                break;
            case 20:
                断言(动画 == "work-calligraphy", $"主段循环不换变体（实际 {动画}）");
                StateMachine.SetState(StateMachine.Idle);   // 收工 → 应播指定类型的 -c 段
                break;
            case 22:
                断言(StateMachine.CurrentState == StateMachine.Working, $"收工先播 -c、状态延迟落地（当前 {StateMachine.CurrentState}）");
                断言(动画 == "work-calligraphy-c", $"-c 段 = 指定类型主名 + c（实际 {动画}）");
                StateMachine.探针_推进时间(0.1f); StateMachine.探针_动画播完回调();
                break;
            case 24:
                断言(StateMachine.CurrentState == StateMachine.Idle && !StateMachine.包裹中,
                    $"C 播完落地 idle（当前 {StateMachine.CurrentState}）");
                break;

            default:
                if (_帧 >= 26) 回退轮();
                break;
        }
    }

    // ── ① 映射表：10 个中文类型名；13 种素材里除 water 外全覆盖；每个键都能挑出主名 ──
    private void 映射表组()
    {
        var 期望键 = new[] { "创作", "计算机", "美食", "游戏", "写作", "其他", "资料", "绘图", "清理", "声音" };
        var 表 = StateMachine.设置.工作类型映射;
        断言(期望键.All(表.ContainsKey) && 表.Count == 期望键.Length,
            $"映射表键 = 10 个中文工作类型名（实际 {表.Count}：{string.Join("/", 表.Keys)}）");

        var 素材 = 表.Values.SelectMany(v => v).Distinct().ToList();
        断言(素材.Count == 12, $"映射素材共 12 种（实际 {素材.Count}：{string.Join("/", 素材)}）");
        断言(!素材.Contains("water"), "PlayWater（water）不在映射里（无合适场景）");
        断言(素材.All(s => CharAnim.有动画($"work-{s}")), $"映射的素材在 work 池里都能找到（{string.Join("/", 素材)}）");

        StateMachine.设置.三档状态启用 = false;   // 三档关：每个类型都能挑出主名
        var 挑不出 = 期望键.Where(k => StateMachine.挑干活主名(k) == null).ToList();
        断言(挑不出.Count == 0, $"每个类型名都能挑出主名（挑不出的：{(挑不出.Count == 0 ? "无" : string.Join("/", 挑不出))}）");
        断言(CharAnim.有动画("work-water"), "对照：water 素材本身在池里（只是不映射）");
    }

    // ── ② 指定类型挑名（三档关：确定性；多素材类型两种都要出得来） ──
    private void 挑名组()
    {
        StateMachine.设置.三档状态启用 = false;
        var 期望 = new Dictionary<string, string>
        {
            ["创作"] = "work-calligraphy", ["计算机"] = "work-fixmenu", ["美食"] = "work-sausage",
            ["游戏"] = "work-game", ["其他"] = "work-rope", ["绘图"] = "work-paint",
            ["清理"] = "work-clean", ["声音"] = "work-pc",
        };
        foreach (var (键, 全名) in 期望)
        {
            var 全同 = Enumerable.Range(0, 30).All(_ => StateMachine.挑干活主名(键) == 全名);
            断言(全同, $"{键} → {全名}（30 次全同）");
        }

        var 写 = Enumerable.Range(0, 80).Select(_ => StateMachine.挑干活主名("写作")).ToList();
        断言(写.All(x => x is "work-remove" or "work-write") && 写.Distinct().Count() == 2,
            $"写作 → remove/write 两种都出得来（{string.Join("/", 写.Distinct())}）");
        var 资 = Enumerable.Range(0, 80).Select(_ => StateMachine.挑干活主名("资料")).ToList();
        断言(资.All(x => x is "work-read" or "work-study2") && 资.Distinct().Count() == 2,
            $"资料 → read/study2 两种都出得来（{string.Join("/", 资.Distinct())}）");

        断言(StateMachine.挑干活主名(null) == null && StateMachine.挑干活主名("") == null && StateMachine.挑干活主名("  ") == null,
            "没给类型 → null（回退随机）");
        断言(StateMachine.挑干活主名("不存在的类型") == null, "没映射的类型 → null（回退随机）");
        断言(StateMachine.挑干活主名("water") == null, "素材名不是类型名：water → null（映射只认中文类型名）");
    }

    // ── ③ 三档降级：开心 → work-happy-*；无 Happy 源的素材退无档基名 ──
    private void 三档降级组()
    {
        StateMachine.设置.三档状态启用 = true;
        StateMachine.设置.状态档位 = "开心";
        断言(StateMachine.挑干活主名("创作") == "work-happy-calligraphy",
            $"开心档 创作 → work-happy-calligraphy（实际 {StateMachine.挑干活主名("创作")}）");
        断言(StateMachine.挑干活主名("声音") == "work-pc",
            $"开心档 声音 → 退无档基名 work-pc（WorkTWO 无 Happy 源；实际 {StateMachine.挑干活主名("声音")}）");
        var 资 = Enumerable.Range(0, 80).Select(_ => StateMachine.挑干活主名("资料")).ToList();
        断言(资.All(x => x is "work-happy-study2" or "work-read") && 资.Distinct().Count() == 2,
            $"开心档 资料 → study2 有 Happy 档、read 退无档基名（{string.Join("/", 资.Distinct())}）");

        StateMachine.设置.状态档位 = "不良";
        断言(StateMachine.挑干活主名("创作") == "work-poor-calligraphy",
            $"不良档 创作 → work-poor-calligraphy（实际 {StateMachine.挑干活主名("创作")}）");
        断言(StateMachine.挑干活主名("声音") == "work-poor-pc",
            $"不良档 声音 → work-poor-pc（pc 有 Poor 档、无 Happy 档；实际 {StateMachine.挑干活主名("声音")}）");

        StateMachine.设置.状态档位 = "普通";
        StateMachine.设置.三档状态启用 = false;
    }

    // ── ⑤ 回退随机：每模式 12 轮会话，看 A 段类型段 ≥3 种（= 没被钉死） ──
    // 注意：① PlayNamed 走 CallDeferred（下一帧才可见）——读 A/C 段名必须比触发晚一帧；
    //      ② 包裹退出只在**主段相位**播 C（A 段还在播时退出 = 硬切、不播 C）——先推进到主段再收工。
    private void 回退轮()
    {
        switch (_子步)
        {
            case 0:
                StateMachine.开始干活(_模式 == 0 ? null : "不存在的类型");
                _子步 = 1;
                break;
            case 1:
                StateMachine.探针_推进时间(2f);   // 起身 → working（包裹入场同步挑名，播放延迟一帧）
                _子步 = 2;
                break;
            case 2:
                (_模式 == 0 ? _未指定样本 : _无映射样本).Add(类型段(CharAnim.当前动画名_只读, "a"));
                StateMachine.探针_推进时间(0.1f); StateMachine.探针_动画播完回调();   // A 播完 → 主段（相位离开「进入段中」）
                _子步 = 3;
                break;
            case 3:
                StateMachine.SetState(StateMachine.Idle);   // 收工 → 播 -c（同样延迟一帧）
                _子步 = 4;
                break;
            case 4:
            {
                var a = (_模式 == 0 ? _未指定样本 : _无映射样本)[^1];
                var c = 类型段(CharAnim.当前动画名_只读, "c");
                if (c != a) 断言(false, $"{(_模式 == 0 ? "没给类型" : "没映射类型")}第 {_轮 + 1} 轮：-c 段类型段跟随 A（A「{a}」/ C「{c}」）");
                StateMachine.探针_推进时间(0.1f); StateMachine.探针_动画播完回调();
                _子步 = 5;
                break;
            }
            default:
            {
                if (StateMachine.CurrentState != StateMachine.Idle || StateMachine.包裹中)
                    断言(false, $"{(_模式 == 0 ? "没给类型" : "没映射类型")}第 {_轮 + 1} 轮：C 播完应落 idle（当前 {StateMachine.CurrentState}）");
                _轮++;
                _子步 = 0;
                if (_轮 < 每模式轮数) break;
                检查随机样本();
                _模式++;
                _轮 = 0;
                if (_模式 >= 2) 收尾();
                break;
            }
        }
    }

    private void 检查随机样本()
    {
        var 名称 = _模式 == 0 ? "没给类型" : "没映射类型";
        var 样本 = _模式 == 0 ? _未指定样本 : _无映射样本;
        var 种类 = 样本.Where(t => t != "").Distinct().Count();
        断言(样本.All(t => t != ""), $"{名称}：12 轮的 A 段都能解析出类型段（{string.Join("/", 样本.Distinct())}）");
        断言(种类 >= 3, $"{名称}：12 轮里出现 ≥3 种类型（实际 {种类} 种：{string.Join("/", 样本.Distinct())}）");
    }

    private void 收尾()
    {
        GD.Print($"[WMAP] ===== 失败数 = {_失败} =====（未指定样本 {string.Join("/", _未指定样本.Distinct())}；无映射样本 {string.Join("/", _无映射样本.Distinct())}）");
        GD.Print(_失败 == 0 ? "[WMAP] PASS" : "[WMAP] FAIL");
        GetTree().Quit(_失败 == 0 ? 0 : 1);
    }
}
