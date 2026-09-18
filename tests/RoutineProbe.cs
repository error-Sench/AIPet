using System;
using System.IO;
using System.Linq;
using Godot;
using desktop.script.State;
using desktop.script.UX;

namespace desktop.tests;

/// <summary>
/// RoutineProbe（headless）：验证 **时间驱动的主动行为**（Plan #11）—— 问候 / 喝水提醒 / 磁盘空间低。
/// <para>
/// 关键手法：**注入时钟与磁盘余量**（`DailyRoutine.时钟` / `探针_磁盘剩余字节`），
/// 这样「一天一次」「到点提醒」都能在几帧内测完，不用等真实时间；事件池走临时路径，不碰真日志。
/// </para>
/// <para>场景：`tests/RoutineProbe.tscn`。</para>
/// </summary>
public partial class RoutineProbe : Node
{
    private int _帧;
    private int _失败;
    private readonly string _临时池 = Path.Combine(Path.GetTempPath(), "aipet_routine_probe.jsonl");
    private int _原上限;
    private bool _原问候;
    private float _原走动, _原睡眠, _原深夜;
    private bool _原磁盘提醒;

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[RT] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[RT] FAIL  {描述}"); }
    }

    public override void _Ready()
    {
        EventPool.探针_路径覆写 = _临时池;
        EventPool.探针_清空();
        GD.Print("=== RoutineProbe: 场景已实例化 ===");
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 10: 准备(); break;
            case 15: A组_配置与纯函数(); break;
            case 25: B组_当天问候(); break;
            case 35: C组_不打断会话(); break;
            case 45: D组_预算约束(); break;
            case 70: F组_磁盘空间(); break;
            case 85: 收尾(); break;
        }
    }

    /// <summary>解除三道早退门 + 把别的自主行为顶到天上（隔离被测行为）。</summary>
    private void 准备()
    {
        StateMachine.入场完成();                                  // 入场门
        DisplayServer.WindowSetPosition(new Vector2I(600, 300));   // headless 光标在 (0,0)，挪开避免「悬停桌宠」早退
        EnvironmentSense.启用 = false;                             // 本组不测环境感知（隐私开关默认关）
        _原上限 = StateMachine.设置.每小时主动上限;
        StateMachine.设置.每小时主动上限 = 100;                    // 探针要连做好几次主动行为
        _原问候 = StateMachine.设置.问候启用;
        _原走动 = StateMachine.设置.走动空闲秒;
        _原睡眠 = StateMachine.设置.睡眠空闲秒;
        _原深夜 = StateMachine.设置.深夜睡眠秒;
        StateMachine.设置.走动空闲秒 = 1e6f;
        StateMachine.设置.睡眠空闲秒 = 1e6f;
        StateMachine.设置.深夜睡眠秒 = 1e6f;
        // 隔离被测行为：磁盘提醒留到 F 组单独测（否则真磁盘状态会掺进来）
        _原磁盘提醒 = StateMachine.设置.磁盘提醒启用;
        StateMachine.设置.磁盘提醒启用 = false;
        DailyRoutine.磁盘提醒启用 = false;
    }

    /// <summary>推进时间并压回 idle —— 让「距上次交互 ≥ 60s」这条不被误踩（探针不等真实时间）。</summary>
    private static void 过一会儿(float 秒 = 120f)
    {
        DailyRoutine.推进(秒);
        StateMachine.SetState(StateMachine.Idle);
    }

    /// <summary>推进一拍心跳 —— 延迟兑现的问候就是靠它落地的。</summary>
    private static void 推一拍() => DailyRoutine.推进(1f);

    private void A组_配置与纯函数()
    {
        GD.Print("--- A 组：配置读取 + 纯函数（时段 / 磁盘判定）---");
        StateMachine.设置.加载();                                   // 显式走一遍配置加载（顺带验证注入 DailyRoutine）
        断言(StateMachine.设置.问候启用, "config/behavior.json 的 问候启用 读到了");
        断言(StateMachine.设置.磁盘剩余下限GB == 10, $"磁盘剩余下限GB = {StateMachine.设置.磁盘剩余下限GB}（配置值）");
        // 设计回归守卫：管家式提醒（喝水/吃饭/该睡了）是**明确不做**的 —— 只提醒主人自己不容易察觉的事
        GD.Print("[RT] 设计原则：只提醒「主人自己不容易察觉的事」（磁盘/久坐）；喝水这类生活管家提醒不做");
        断言(DailyRoutine.问候启用 == StateMachine.设置.问候启用, "配置已注入行为层（DailyRoutine）");

        断言(DailyRoutine.问候语(new DateTime(2026, 9, 19, 8, 0, 0)).时段 == "早上", "08:00 → 早上");
        断言(DailyRoutine.问候语(new DateTime(2026, 9, 19, 12, 0, 0)).时段 == "中午", "12:00 → 中午");
        断言(DailyRoutine.问候语(new DateTime(2026, 9, 19, 15, 0, 0)).时段 == "下午", "15:00 → 下午");
        断言(DailyRoutine.问候语(new DateTime(2026, 9, 19, 20, 0, 0)).时段 == "晚上", "20:00 → 晚上");
        断言(DailyRoutine.问候语(new DateTime(2026, 9, 19, 2, 0, 0)).时段 == "深夜", "02:00 → 深夜（＝提醒早点休息）");
        var 深夜样例 = Enumerable.Range(0, 20).Select(_ => DailyRoutine.问候语(new DateTime(2026, 9, 19, 2, 0, 0)).语句).ToList();
        断言(深夜样例.All(s => s.Contains("陪") || s.Contains("睡") || s.Contains("休息") || s.Contains("夜深") || s.Contains("这么晚")),
            $"深夜语句都是「我注意到你还在」的陪伴口吻（样例「{深夜样例[0]}」；共 {深夜样例.Distinct().Count()} 种）");

        断言(DailyRoutine.磁盘算低(5L * 1024 * 1024 * 1024), "5 GB < 10 GB → 算低");
        断言(!DailyRoutine.磁盘算低(50L * 1024 * 1024 * 1024), "50 GB → 不算低");
        断言(!DailyRoutine.磁盘算低(10L * 1024 * 1024 * 1024), "正好 10 GB → 不算低（边界用 <）");
    }

    private void B组_当天问候()
    {
        GD.Print("--- B 组：当天首次见面 → 问候（一天一次）---");
        EventPool.探针_清空();
        DailyRoutine.探针_重置();
        StateMachine.设置.问候启用 = true;
        DailyRoutine.问候启用 = true;
        DailyRoutine.时钟 = () => new DateTime(2026, 9, 19, 8, 30, 0);
        StateMachine.SetState(StateMachine.Idle);

        DailyRoutine.交互();
        断言(DailyRoutine.探针_待问候, "首次交互 → 只是**排上**问候（不抢这次互动的反应）");
        断言(DailyRoutine.探针_问候次数 == 0, "此刻还没冒泡（延迟到互动反应播完）");

        推一拍();
        断言(DailyRoutine.探针_问候次数 == 1, $"回到 idle 后兑现（{DailyRoutine.探针_问候次数}）");
        断言(DailyRoutine.探针_最近语句.Contains("早"), $"早上时段用语（「{DailyRoutine.探针_最近语句}」）");
        断言(EventPool.读().Count > 0 && EventPool.读()[^1]["kind"] == "问候", "事件池记了一条「问候」");
        断言(StateMachine.CurrentState == StateMachine.Greet, "用打招呼姿态勾注意力");
        断言(Dialogue.探针_最近请求文本.Length > 0, $"冒了气泡（「{Dialogue.探针_最近请求文本}」）");

        StateMachine.SetState(StateMachine.Idle);
        过一会儿();                                   // 让「不再问候」的原因是「当天已问候」，而不是 60s 门槛
        DailyRoutine.交互();
        推一拍();
        断言(DailyRoutine.探针_问候次数 == 1, "同一天再来 → 不再问候（一天一次）");

        // 同一天、换个时段 → 依然不再问候
        过一会儿();
        DailyRoutine.时钟 = () => new DateTime(2026, 9, 19, 21, 0, 0);
        DailyRoutine.交互();
        推一拍();
        断言(DailyRoutine.探针_问候次数 == 1, "同一天换时段也不重复（一天一次是按日期算）");

        // 次日 → 又可以问候（先过一会儿，让「距上次交互」跳过 60s 门槛）
        过一会儿();
        DailyRoutine.时钟 = () => new DateTime(2026, 9, 20, 21, 0, 0);
        DailyRoutine.交互();
        推一拍();
        断言(DailyRoutine.探针_问候次数 == 2, $"次日恢复问候（{DailyRoutine.探针_问候次数}）");
        断言(DailyRoutine.探针_最近语句.Contains("晚"), $"晚上时段用语（「{DailyRoutine.探针_最近语句}」）");
    }

    private void C组_不打断会话()
    {
        GD.Print("--- C 组：不打断进行中的操作（距上次交互 < 60s 不问候）---");
        DailyRoutine.探针_重置();
        DailyRoutine.时钟 = () => new DateTime(2026, 9, 21, 9, 0, 0);
        StateMachine.SetState(StateMachine.Idle);
        DailyRoutine.交互();
        推一拍();                                     // 第一次兑现
        var 首次 = DailyRoutine.探针_问候次数;
        断言(首次 == 1, $"久违后的首次交互会问候（{首次}）");

        StateMachine.SetState(StateMachine.Idle);
        DailyRoutine.交互();                          // 紧接着又交互（距上次 ≈ 0s）
        推一拍();
        断言(DailyRoutine.探针_问候次数 == 1, $"紧接着的交互不问候（不会打断连续操作；{DailyRoutine.探针_问候次数}）");

        // 忙态不插话（连排队都不排）
        DailyRoutine.探针_重置();
        DailyRoutine.时钟 = () => new DateTime(2026, 9, 22, 9, 0, 0);
        StateMachine.SetState(StateMachine.Think);
        DailyRoutine.交互();
        断言(!DailyRoutine.探针_待问候, "忙态（think）里不排问候");
        StateMachine.SetState(StateMachine.Idle);
    }

    private void D组_预算约束()
    {
        GD.Print("--- D 组：每小时主动预算约束 ---");
        DailyRoutine.探针_重置();
        DailyRoutine.时钟 = () => new DateTime(2026, 9, 23, 9, 0, 0);
        StateMachine.SetState(StateMachine.Idle);
        StateMachine.设置.每小时主动上限 = 0;                 // 预算耗尽
        DailyRoutine.交互();
        断言(!DailyRoutine.探针_待问候, "预算 0 → 连问候都不排（主动行为统一受每小时上限约束）");
        推一拍();
        断言(DailyRoutine.探针_问候次数 == 0, "预算 0 时也不会冒泡");
        StateMachine.设置.每小时主动上限 = 100;
        过一会儿();                                   // 跳过 60s 门槛（否则上一行的交互会把它挡住）
        DailyRoutine.交互();
        推一拍();
        断言(DailyRoutine.探针_问候次数 == 1, "预算恢复 → 正常问候");
    }

    private void F组_磁盘空间()
    {
        GD.Print("--- F 组：磁盘空间低（每天一次；闸门关着先记下、开了再补报）---");
        EventPool.探针_清空();
        DailyRoutine.探针_重置();
        StateMachine.设置.磁盘提醒启用 = true; DailyRoutine.磁盘提醒启用 = true;
        StateMachine.设置.磁盘剩余下限GB = 10; DailyRoutine.磁盘剩余下限GB = 10;
        DailyRoutine.时钟 = () => new DateTime(2026, 9, 25, 10, 0, 0);
        DailyRoutine.探针_磁盘剩余字节 = () => 5L * 1024 * 1024 * 1024;      // 5 GB（低于 10 GB）
        StateMachine.SetState(StateMachine.Idle);

        DailyRoutine.推进(1f);
        断言(DailyRoutine.探针_磁盘提醒次数 == 1, $"低余量 → 提醒一次（{DailyRoutine.探针_磁盘提醒次数}）");
        断言(DailyRoutine.探针_最近语句.Contains("只剩"), $"语句带上了余量（「{DailyRoutine.探针_最近语句}」）");
        断言(EventPool.读().Count > 0 && EventPool.读()[^1]["kind"] == "磁盘空间低", "事件池记了一条「磁盘空间低」");

        StateMachine.SetState(StateMachine.Idle);
        DailyRoutine.推进(1f);
        断言(DailyRoutine.探针_磁盘提醒次数 == 1, "同一天不重复（每天最多一次）");

        // 闸门关着（忙态）→ 只记下，不硬闯；闸门开了补报
        DailyRoutine.时钟 = () => new DateTime(2026, 9, 26, 10, 0, 0);        // 换一天
        StateMachine.SetState(StateMachine.Think);
        DailyRoutine.推进(1f);
        断言(DailyRoutine.探针_磁盘提醒次数 == 1, "闸门关着（忙态）时不硬闯");
        断言(DailyRoutine.探针_磁盘待提醒, "但已经记下「该提醒」，等窗口");
        StateMachine.SetState(StateMachine.Idle);
        DailyRoutine.推进(1f);
        断言(DailyRoutine.探针_磁盘提醒次数 == 2, $"闸门开了 → 补报（{DailyRoutine.探针_磁盘提醒次数}）");
        断言(!DailyRoutine.探针_磁盘待提醒, "补报后清掉待办标记");

        // 余量正常 → 不打扰
        DailyRoutine.探针_磁盘剩余字节 = () => 100L * 1024 * 1024 * 1024;     // 100 GB
        DailyRoutine.时钟 = () => new DateTime(2026, 9, 27, 10, 0, 0);
        StateMachine.SetState(StateMachine.Idle);
        DailyRoutine.推进(1f);
        断言(DailyRoutine.探针_磁盘提醒次数 == 2, "余量充足 → 不提醒");
    }

    private void 收尾()
    {
        StateMachine.设置.每小时主动上限 = _原上限;
        StateMachine.设置.问候启用 = _原问候;
        StateMachine.设置.走动空闲秒 = _原走动;
        StateMachine.设置.睡眠空闲秒 = _原睡眠;
        StateMachine.设置.深夜睡眠秒 = _原深夜;
        StateMachine.设置.磁盘提醒启用 = _原磁盘提醒;
        DailyRoutine.磁盘提醒启用 = _原磁盘提醒;
        DailyRoutine.问候启用 = _原问候;
        DailyRoutine.探针_重置();
        EventPool.探针_清空();
        EventPool.探针_路径覆写 = "";
        try { if (File.Exists(_临时池)) File.Delete(_临时池); } catch { /* 忽略 */ }
        StateMachine.SetState(StateMachine.Idle);
        GD.Print($"[RT] ===== 失败数 = {_失败} =====");
        GetTree().Quit(_失败 == 0 ? 0 : 1);
    }
}
