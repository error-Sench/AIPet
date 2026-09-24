using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using desktop.script.State;
using desktop.script.Soul;
using desktop.script.UX;

namespace desktop.tests;

/// <summary>
/// EventProbe（headless）：验证行为事件系统 —— 事件池读写/ack/保留策略/隐私边界 + 久坐提醒（程序侧）+ 升级为 Agent 事件。
/// <para>场景：`tests/EventProbe.tscn`。事件池用**临时覆写路径**，不碰真日志。</para>
/// </summary>
public partial class EventProbe : Node
{
    private int _帧;
    private int _失败;
    private readonly string _临时池 = Path.Combine(Path.GetTempPath(), "aipet_events_probe.jsonl");

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[EV] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[EV] FAIL  {描述}"); }
    }

    public override void _Ready()
    {
        EventPool.探针_路径覆写 = _临时池;
        EventPool.探针_清空();
        // 隔离 Plan #11 的时间驱动行为：本探针只测事件池与久坐（问候在入场后由 探针_重置 清掉；
        // 原「磁盘提醒」按 Plan #16 删除，不再需要隔离）
        MusicSense.启用 = false;   // 组③：隔离音乐反应（系统有声就跳舞会顶状态）
        GD.Print("=== EventProbe: 场景已实例化 ===");
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 10: A组_读写与ack(); break;
            case 20: B组_保留策略(); break;
            case 30: C组_隐私边界(); break;
            case 40: D组_久坐提醒(); break;
            case 60: E组_进上下文接口(); break;
            case 70:
                EventPool.探针_清空();
                EventPool.探针_路径覆写 = "";
                EnvironmentSense.启用 = false;
                GD.Print($"[EV] ===== 失败数 = {_失败} =====");
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                break;
        }
    }

    private void A组_读写与ack()
    {
        GD.Print("--- A 组：事件池读写 + ack ---");
        EventPool.探针_清空();
        EventPool.记("久坐提醒", EventPool.归属.程序, "连续活跃 90 分钟");
        EventPool.记("回来", EventPool.归属.程序, "主人回来了");
        EventPool.记("久坐超长", EventPool.归属.Agent, "这一坐太久了，要不要关心一下？");

        断言(EventPool.读().Count == 3, $"三条事件都写进去了（{EventPool.读().Count}）");
        断言(EventPool.未处理数 == 1, $"只有归属 Agent 的那条是待处理（{EventPool.未处理数}）");
        var 待办 = EventPool.未处理();
        断言(待办[0]["kind"] == "久坐超长" && 待办[0]["text"].Contains("关心"), "待处理事件的类型与文本正确");

        // Agent 追加 ack 行 → 不再待处理（这就是 Agent 侧的「处理完」约定）
        EventPool.确认程序侧(待办[0]["t"], "已提醒");
        断言(EventPool.未处理数 == 0, "ack 之后不再待处理（Agent 追加一行即算清账）");
        断言(EventPool.读().Count == 4, "ack 是**追加**行，原始日志不被改写");
    }

    private void B组_保留策略()
    {
        GD.Print("--- B 组：保留策略 ---");
        EventPool.探针_清空();
        var 原上限 = EventPool.上限条数;
        EventPool.上限条数 = 10;
        for (var i = 0; i < 15; i++) EventPool.记("测试", EventPool.归属.程序, $"第 {i} 条");
        断言(EventPool.读().Count == 10, $"超出上限自动裁掉最旧的（上限 10 → 实际 {EventPool.读().Count}）");
        断言(EventPool.读()[^1]["text"].Contains("第 14 条"), "留下的是最新的那批");
        EventPool.上限条数 = 原上限;
    }

    private void C组_隐私边界()
    {
        GD.Print("--- C 组：隐私边界（只记自己的观察，不记窗口/进程/键鼠内容）---");
        EventPool.探针_清空();
        EventPool.记("久坐提醒", EventPool.归属.程序, "连续活跃 90 分钟");
        var 行 = File.ReadAllText(_临时池);
        断言(!行.Contains("title") && !行.Contains("exe") && !行.Contains("process") && !行.Contains("窗口标题"),
            "事件行里没有窗口标题/进程名类字段");
        断言(行.Contains("\"kind\"") && 行.Contains("\"owner\"") && 行.Contains("\"t\""),
            "只有我们自己的字段（时间/类型/归属/文本）");
    }

    private void D组_久坐提醒()
    {
        GD.Print("--- D 组：久坐提醒（程序侧简单判断，受「不打扰」约束）---");
        EventPool.探针_清空();
        StateMachine.入场完成();                       // 解除入场门（否则心跳整段早退）
        DailyRoutine.探针_重置();                      // 入场会排上「启动问候」，清掉它（否则问候的 greet 姿态占住状态，久坐被「不打扰」拦下）
        DisplayServer.WindowSetPosition(new Vector2I(600, 300)); // 把窗口挪开鼠标（headless 光标在 (0,0)，会判定「悬停桌宠」→ 心跳早退）
        EnvironmentSense.启用 = true;
        EnvironmentSense.探针_注入(10f, false);          // 主人在活跃（空闲 10s）
        StateMachine.探针_心跳一次();                    // 先让 P6 边沿跑完（第一次会触发「回来」打招呼，占住状态 2.5s）
        StateMachine.SetState(StateMachine.Idle);        // 探针不等实时 2.5s，直接压回 idle 再测久坐
        StateMachine.探针_重置久坐();
        // 隔离被测行为：本组只测久坐，把走动/睡眠的调度阈值顶到天上（否则 45s 就先走起来了）
        var 原走动 = StateMachine.设置.走动空闲秒; var 原睡眠 = StateMachine.设置.睡眠空闲秒; var 原深夜 = StateMachine.设置.深夜睡眠秒;
        StateMachine.设置.走动空闲秒 = 1e6f; StateMachine.设置.睡眠空闲秒 = 1e6f; StateMachine.设置.深夜睡眠秒 = 1e6f;
        var 原分钟 = StateMachine.设置.久坐提醒分钟;
        var 原冷却 = StateMachine.设置.久坐提醒冷却分钟;
        StateMachine.设置.久坐提醒分钟 = 1f;             // 探针里 1 分钟就触发
        StateMachine.设置.久坐提醒冷却分钟 = 100f;       // 冷却很长，验证「同一段只触发一次」

        for (var i = 0; i < 61; i++) StateMachine.探针_心跳一次();

        GD.Print($"[EV] 档位检查: 活跃累计={StateMachine.探针_活跃累计秒:0}s 状态={StateMachine.CurrentState}");
        断言(StateMachine.探针_久坐提醒次数 == 1, $"连续活跃 61 次心跳 → 触发 1 次提醒（实际 {StateMachine.探针_久坐提醒次数}）");
        断言(EventPool.读().Count(r => r["kind"] == "久坐提醒") == 1, "事件池里记了一条「久坐提醒」");
        断言(Dialogue.探针_最近请求文本.Length > 0, $"冒了气泡（「{Dialogue.探针_最近请求文本}」）");
        断言(StateMachine.CurrentState == StateMachine.Greet, "提醒时用「打招呼」姿态勾注意力（2.5s 后回 idle）");

        // 再坐满一段（冷却未到）→ 不再打扰
        StateMachine.SetState(StateMachine.Idle);   // 探针不等实时 2.5s 的 greet
        for (var i = 0; i < 61; i++) StateMachine.探针_心跳一次();
        断言(StateMachine.探针_久坐提醒次数 == 1, "冷却期内不重复提醒（不打扰）");

        // 提醒满 2 次 → 升级为**归属 Agent** 的事件（Agent 自己决定怎么关心）
        StateMachine.设置.久坐提醒冷却分钟 = 0f;
        StateMachine.探针_清久坐冷却();          // 配置改小不会立刻生效（计时器已在跑）→ 探针直接清
        StateMachine.SetState(StateMachine.Idle);
        for (var i = 0; i < 61; i++) StateMachine.探针_心跳一次();
        断言(StateMachine.探针_久坐提醒次数 == 2, $"第二次提醒（实际 {StateMachine.探针_久坐提醒次数}）");
        断言(EventPool.读().Any(r => r["kind"] == "久坐超长" && r["owner"] == "agent"),
            "升级事件归属 Agent（写进池子等它自己来读，不做推送）");
        断言(EventPool.未处理数 == 1, "升级事件处于待处理状态");

        StateMachine.设置.久坐提醒分钟 = 原分钟;
        StateMachine.设置.久坐提醒冷却分钟 = 原冷却;
        StateMachine.设置.走动空闲秒 = 原走动; StateMachine.设置.睡眠空闲秒 = 原睡眠; StateMachine.设置.深夜睡眠秒 = 原深夜;
    }

    private void E组_进上下文接口()
    {
        GD.Print("--- E 组：事件池进上下文接口（Agent 读 context.md 就知道有活要干）---");
        var 文本 = ContextTable.组装();
        断言(文本.Contains("events.jsonl"), "上下文里给了事件池路径");
        断言(文本.Contains("## 待你处理的事件") && 文本.Contains("久坐超长"),
            "上下文里列出了待 Agent 处理的事件（pull：它读了才知道）");
    }
}
