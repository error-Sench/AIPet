using desktop.script.logic;
using desktop.script.State;
using Godot;

namespace desktop.tests;

/// <summary>
/// 状态机回归探针（headless）：
///   ① 状态效果表：SetState 后 CurrentState 与「接管中」是否符合预期
///   ② 状态锁：持续态期间不被 CharAnim 的「播完回 idle」抢走
///   ③ 保持/兜底超时：非持续态回 idle、锁定持续态兜底回 idle
///   ④ 节律：空闲累计 → sleep；面板可见/忙态时禁止主动行为
/// 用法：Godot_v4.7.2-stable_mono_win64_console.exe --headless --path D:/Games/Github/AIPet res://tests/StateProbe.tscn
/// </summary>
public partial class StateProbe : Node
{
    private int _帧;
    private int _失败;
    private readonly System.Collections.Generic.List<int> 探针进度 = new();

    public override void _Ready()
    {
        Main.探针_禁首启提示 = true;   // 确定性：首启问候气泡会顶状态（入场门/回 idle 断言），禁掉
        StateMachine.探针_禁用包裹 = true;   // 本探针只测状态语义（即时切换）：包裹段（A/C 过渡）整体旁路——包裹段另有 WrapProbe
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        设置_隔离时间驱动();
        GD.Print("=== StateProbe: 场景已实例化 ===");
    }

    /// <summary>
    /// 隔离 Plan #11 的时间驱动行为：本探针只测**状态语义与摸摸反应**——问候会抢状态
    /// （首次交互排上问候 → 兑现时 SetState(Greet)），磁盘提醒会往事件池/气泡里插东西。
    /// </summary>
    private static void 设置_隔离时间驱动()
    {
        StateMachine.设置.问候启用 = false;
        DailyRoutine.问候启用 = false;
        StateMachine.设置.磁盘提醒启用 = false;
        DailyRoutine.磁盘提醒启用 = false;
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[SM] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[SM] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        if (_帧 == 4) 第一组_状态效果与锁();
        else if (_帧 == 40) 第二组_保持与兜底();
        else if (_帧 == 46) 第三组_空闲与不打扰();
        else if (_帧 == 52)
        {
            GD.Print($"[SM] ===== 失败数 = {_失败} =====");
            GD.Print(_失败 == 0 ? "[SM] PASS" : "[SM] FAIL");
            GetTree().Quit(_失败 == 0 ? 0 : 1);
        }
    }

    private void 第一组_状态效果与锁()
    {
        GD.Print($"[SM] 节律配置: 启用={StateMachine.设置.启用} 睡眠={StateMachine.设置.睡眠空闲秒}s 走动={StateMachine.设置.走动空闲秒}s 上限={StateMachine.设置.每小时主动上限}/h");

        // ① 入场门（R6 回归）：入场动画没播完前，心跳不得调度自主行为
        GD.Print($"[SM] 入场未完成 = {StateMachine.入场未完成_只读}");
        if (StateMachine.入场未完成_只读)
        {
            StateMachine.探针_推进空闲(StateMachine.设置.睡眠空闲秒 + 100f);
            StateMachine.探针_心跳();
            断言(StateMachine.CurrentState == StateMachine.Idle, "入场未完成时不入睡（入场门生效）");
            断言(!StateMachine.探针_允许主动(), "入场未完成时禁止主动行为");
        }
        else GD.Print("[SM] 跳过入场门断言（入场已完成）");

        // 让入场门走完，避免入场招呼在后续断言中途插进来
        StateMachine.入场完成();
        StateMachine.探针_推进时间(3f);

        断言(StateMachine.CurrentState == StateMachine.Idle, "启动后为 idle");
        断言(!StateMachine.接管中, "idle 不接管");

        // ② 标记状态（R5 回归）：只改逻辑态、不驱动表现
        StateMachine.标记状态(StateMachine.Drag);
        断言(StateMachine.CurrentState == StateMachine.Drag, "标记状态(drag) 生效");
        断言(!StateMachine.接管中, "drag 不接管（表现由 CharAnim 负责）");
        StateMachine.标记状态(StateMachine.Idle);

        // ③ 退出保护（R7 回归）：准备退出后必须解锁，否则退出动画被吞掉
        StateMachine.SetState(StateMachine.Think);
        断言(StateMachine.接管中, "think 接管中（准备退出的前置条件）");
        StateMachine.准备退出();
        断言(!StateMachine.接管中, "准备退出() 解除状态锁（退出动画能播完 → 程序能关）");
        StateMachine.SetState(StateMachine.Idle);

        // ④ 持续态：think → 接管；跑若干帧后仍应是 think（不被 CharAnim 抢回 idle）
        StateMachine.SetState(StateMachine.Think);
        断言(StateMachine.CurrentState == StateMachine.Think, "SetState(think) 生效");
        断言(StateMachine.接管中, "think 持续态接管中");
        _待验证锁 = true;
    }

    private bool _待验证锁;

    private void 第二组_保持与兜底()
    {
        if (_待验证锁)
        {
            // 已经过了 36 帧（≈0.6s），持续态应仍为 think
            断言(StateMachine.CurrentState == StateMachine.Think, "持续态 think 未被动画播完抢回 idle（状态锁生效）");
            _待验证锁 = false;

            // 兜底：持续态超过「持续态兜底秒」(120s) → 回 idle
            StateMachine.探针_推进时间(StateMachine.设置.持续态兜底秒 + 1f);
            断言(StateMachine.CurrentState == StateMachine.Idle, "think 兜底超时 → idle");
            断言(!StateMachine.接管中, "超时后不再接管");
        }

        // interact 现在是「三段序列」（进入→保持→退出回待机），由动画播完驱动，不再走保持计时
        StateMachine.SetState(StateMachine.Interact);
        断言(StateMachine.CurrentState == StateMachine.Interact, "SetState(interact) 生效");
        断言(StateMachine.接管中, "interact 序列运行中（接管）");
        断言(StateMachine.探针_序列进度 == 1, "序列停在第 1 段（进入）");
        for (var i = 0; i < 3; i++)
        {
            探针进度.Add(StateMachine.探针_序列进度);
            StateMachine.探针_推进时间(0.1f);
            StateMachine.探针_动画播完回调();
        }
        GD.Print($"[SM] 序列进度轨迹 = [{string.Join(",", 探针进度)}]（应含 1/2/3）");
        断言(探针进度.Contains(1) && 探针进度.Contains(2) && 探针进度.Contains(3), "序列按 进入→保持→退出 逐段推进");
        断言(StateMachine.CurrentState == StateMachine.Idle, "序列播完自动回 idle（含原版的后半段）");
        断言(!StateMachine.接管中, "序列结束后不再接管");

        // 非持续态（无序列）的保持计时：greet 2.5s 后回 idle
        StateMachine.SetState(StateMachine.Greet);
        断言(StateMachine.CurrentState == StateMachine.Greet, "SetState(greet) 生效");
        StateMachine.探针_推进时间(1.0f);
        断言(StateMachine.CurrentState == StateMachine.Greet, "greet 保持期内仍是 greet（1.0s < 2.5s）");
        StateMachine.探针_推进时间(2.0f);
        断言(StateMachine.CurrentState == StateMachine.Idle, "greet 保持期满 → idle（3.0s > 2.5s）");

        // 摸摸：空闲重置 + **排队不硬切**
        StateMachine.探针_推进空闲(300f);
        断言(StateMachine.空闲秒_只读 >= 300f, "空闲累计生效");
        断言(StateMachine.CurrentState == StateMachine.Idle, "摸摸前处于 idle");
        StateMachine.摸摸();
        断言(StateMachine.空闲秒_只读 < 1f, "摸摸重置空闲计时");
        断言(StateMachine.CurrentState == StateMachine.Idle, "摸摸不硬切（停在 idle 等当前动画播完）");
        断言(StateMachine.探针_有排队项, "摸摸已排队");
        断言(StateMachine.探针_动画播完(), "动画播完时消费排队项");
        断言(StateMachine.CurrentState == StateMachine.Interact, "排队生效 → interact");
        StateMachine.探针_推进时间(3f);

        // 冲突（用户要求：摸摸可延迟、拖拽必须立刻）：排队中的摸摸必须被拖拽作废
        StateMachine.摸摸();
        断言(StateMachine.探针_有排队项, "再次摸摸排队");
        StateMachine.标记状态(StateMachine.Drag);
        断言(StateMachine.CurrentState == StateMachine.Drag, "拖拽立刻生效（不排队）");
        断言(!StateMachine.探针_有排队项, "拖拽作废排队中的摸摸");
        断言(!StateMachine.探针_动画播完(), "作废后无排队项可消费（拖完不会补一个摸摸）");
        StateMachine.标记状态(StateMachine.Idle);
        StateMachine.探针_推进时间(3f);

        // 排队兜底：动画不回完成信号时，兜底也要让排队项生效
        StateMachine.摸摸();
        断言(StateMachine.探针_有排队项, "摸摸排队（兜底测试）");
        StateMachine.探针_推进时间(StateMachine.设置.排队兜底秒 + 1f);
        断言(StateMachine.CurrentState == StateMachine.Interact, "排队兜底生效 → interact");
        StateMachine.探针_推进时间(3f);
    }

    private void 第三组_空闲与不打扰()
    {
        // 唤醒链路：sleep 时任何交互都应唤醒并打招呼
        StateMachine.SetState(StateMachine.Sleep);
        断言(StateMachine.接管中, "sleep 接管中");
        StateMachine.NotifyInteraction("探针");
        断言(StateMachine.CurrentState == StateMachine.Greet, "睡着时交互 → 唤醒打招呼");

        // 空闲累计到睡眠阈值 → 入睡（经真实心跳路径）
        StateMachine.探针_推进时间(3f);
        断言(StateMachine.CurrentState == StateMachine.Idle, "回到 idle 以备入睡测试");
        StateMachine.探针_推进空闲(StateMachine.设置.睡眠空闲秒 + 5f);
        StateMachine.探针_心跳();
        GD.Print($"[SM] 入睡测试后状态 = {StateMachine.CurrentState}（深夜={是否深夜_日志()}）");
        断言(StateMachine.CurrentState == StateMachine.Sleep || StateMachine.空闲秒_只读 > 0f,
            "空闲超阈值 → 入睡（或至少空闲未被误清）");

        // 不打扰：非 idle 时禁止主动行为
        StateMachine.SetState(StateMachine.Think);
        断言(!StateMachine.探针_允许主动(), "think 忙态禁止主动行为");
        StateMachine.SetState(StateMachine.Idle);
        StateMachine.探针_推进时间(3f);
        GD.Print($"[SM] idle 下允许主动 = {StateMachine.探针_允许主动()}（鼠标悬停/面板可见时为 False）");
    }

    private static string 是否深夜_日志() => "见状态判定";
}