using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using desktop.script.logic;
using desktop.script.UX;
using Godot;

namespace desktop.script.State;

/// <summary>
/// 身体层：状态机 + 行为链 + **自主行为节律**。
///
/// 定位（见 AGENTS.md §3）：本类是身体层的**唯一状态权威**——向上接收交互/Agent 事件，
/// 向下驱动 CharAnim；CharAnim 自身「动画播完回 idle」的旧逻辑在持续态期间让位（见 `接管中`）。
///
/// 状态效果表见 `_效果表`：每个状态 → 动画池（目标池 / 当前兼容池）+ 是否持续态 + 秒数。
/// 节律参数全部来自 `settings/behavior.json`（改完重启生效，无需重编译）。
/// 约定：标识符英文，注释中文（见 AGENTS.md §8）。
/// </summary>
public partial class StateMachine : Node
{
    // —— 状态常量 ——
    public const string Idle = "idle";
    public const string Drag = "drag";
    public const string Interact = "interact";
    public const string Think = "think";
    public const string Speak = "speak";
    public const string Listen = "listen";
    public const string Working = "working";
    public const string Sleep = "sleep";
    public const string Greet = "greet";

    /// <summary>贴边隐藏（P2 行为层）：表现由 EdgeHide 自管，状态机只当「锁定持续态」占位。见 script/State/README.md。</summary>
    public const string EdgeHideState = "edge_hide";

    // —— 行为链状态（不映射固定动画，语义化） ——
    public const string WalkStart = "walk_start";
    public const string WalkLoop = "walk_loop";
    public const string WalkEnd = "walk_end";

    public static StateMachine Instance { get; private set; }
    public static string CurrentState { get; private set; } = Idle;

    /// <summary>状态变更事件（参数为新状态；行为链完成时额外抛 "chain_done"）。</summary>
    public static event Action<string> StateChanged;

    /// <summary>
    /// 持续态（think/speak/working/listen/sleep）期间为 true：CharAnim 的动画播完回调应让位给状态机，
    /// 不要自行回落到 idle/fidget。这是新旧两套状态逻辑的**唯一交汇点**。
    /// </summary>
    public static bool 接管中 { get; private set; }

    /// <summary>状态是否有效（指令通道校验用：拒绝 Agent 传来的任意字符串）。</summary>
    public static bool 状态有效(string 状态) => !string.IsNullOrEmpty(状态) && _效果表.ContainsKey(状态);

    // ================= 状态效果表 =================

    /// <summary>单个状态的表现定义。目标池是 P2 导入专属动画后使用的池名；兼容池是当前已有资产。</summary>
    private sealed class 状态效果
    {
        public string 目标池;
        public string 兼容池;
        public bool 持续;   // true = 保持到显式切换（配合锁定/兜底）；false = 定时回 idle
        public bool 锁定;   // true = 接管中（CharAnim 让位）
        public float 秒;    // 持续态 = 兜底超时（0 = 不超时）；非持续态 = 保持时长
        public bool 免兜底; // true = 真·无限期持续态（贴边隐藏用）：不吃「持续态兜底」，否则 2 分钟被踢回 idle（实测 bug）
    }

    private static readonly Dictionary<string, 状态效果> _效果表 = new()
    {
        [Idle] = new 状态效果 { 目标池 = "idle", 兼容池 = "idle", 持续 = true, 锁定 = false, 秒 = 0 },
        [Interact] = new 状态效果 { 目标池 = "interact", 兼容池 = "fidget", 持续 = false, 锁定 = false, 秒 = 2.0f },
        [Drag] = new 状态效果 { 目标池 = "drag", 兼容池 = "drag", 持续 = true, 锁定 = false, 秒 = 0 },
        [Think] = new 状态效果 { 目标池 = "think", 兼容池 = "fidget", 持续 = true, 锁定 = true, 秒 = 0 },
        [Speak] = new 状态效果 { 目标池 = "say", 兼容池 = "fidget", 持续 = true, 锁定 = true, 秒 = 0 },
        [Listen] = new 状态效果 { 目标池 = "listen", 兼容池 = "fidget", 持续 = true, 锁定 = true, 秒 = 60f },
        [Working] = new 状态效果 { 目标池 = "work", 兼容池 = "fidget", 持续 = true, 锁定 = true, 秒 = 0 },
        [Sleep] = new 状态效果 { 目标池 = "sleep", 兼容池 = "idle", 持续 = true, 锁定 = true, 秒 = 0 },
        [Greet] = new 状态效果 { 目标池 = "greet", 兼容池 = "celerate", 持续 = false, 锁定 = false, 秒 = 2.5f },
        // 贴边隐藏：持续 + 锁定；表现不走「池内随机」，由 EdgeHide.应用表现() 按阶段精确播（见应用表现）
        // 免兜底：贴边是**无限期**待着，不能吃 2 分钟的持续态兜底（否则会自己变 idle 挪回屏内 —— 实测 bug）
        [EdgeHideState] = new 状态效果 { 目标池 = "edge_hide", 兼容池 = "idle", 持续 = true, 锁定 = true, 免兜底 = true, 秒 = 0 },
    };

    /// <summary>
    /// 多段交互序列：状态 → 动画名序列。有些互动在素材里本身就是「进入 → 保持 → 退出(回待机)」多段
    /// （VPet 摸头 = Touch_Head/A + B + C），逐段播放、末段播完自动回 idle，避免在姿势上硬切。
    /// 动画名 = `{池}-{变体}`（见 §3.1.2 导入器）；序列不存在时自动回退到效果表的单池路径。
    /// </summary>
    private static readonly Dictionary<string, string[]> _序列表 = new()
    {
        [Interact] = ["interact-a", "interact-b", "interact-c"],
    };

    private static string[] _当前序列;
    private static int _序列序;

    // ================= 行为链（原有） =================

    private static readonly List<ChainStep> _chainQueue = new();
    private static bool _chainRunning;
    private static float _stepRemaining;
    private static Action _activeCallback;

    // ================= 节律器内部状态 =================

    private static double _心跳累加;
    private static float _空闲秒;
    private static float _保持剩余;      // 非持续态：剩余保持时间
    private static float _兜底剩余;      // 持续态：剩余兜底时间
    private static float _走动倒计时;    // 空闲达标后，距下一次自主走动的秒数
    private static float _重播冷却;      // 防止动画极短时疯狂重播
    private static string _排队状态;     // 排队等待「当前动画播完」再切的交互反应
    private static float _排队秒 = -1f;
    private static float _排队兜底剩余;  // 防止动画不回完成信号导致排队状态永远不生效
    private static bool _已报可走动;     // 「空闲达标」日志只报一次，避免刷屏
    private static readonly List<double> _主动时间戳 = new();
    private static double _运行秒;

    // 走动执行状态
    private static bool _走动中;
    private static int _走动目标X;
    private static float _走动速度;

    // ================= 生命周期 =================

    public override void _Ready()
    {
        Instance = this;
        设置.加载();
        CurrentState = Idle;
        _走动倒计时 = 首次间隔(); // 首次走动用短间隔，否则要等「走动空闲秒 + 120~300s」才看得到
        // 注意：这里**不播放表现**。入场动画由 CharAnim.载入人物动画() 负责，
        // 本节点是 game.tscn 里 Main 的兄弟且顺序在后，若在此 PlayState("idle")（deferred）
        // 会抢掉入场动画，导致 case "enter" 永不触发。入场门由 CharAnim 回调解除。
        GD.Print($"[StateMachine] 就绪: 启用={设置.启用} 睡眠={设置.睡眠空闲秒}s 走动={设置.走动空闲秒}s 上限={设置.每小时主动上限}/h");
    }

    /// <summary>CharAnim 入场动画播完时调用：解除入场门并打一次招呼（启动招呼）。</summary>
    public static void 入场完成()
    {
        if (!_入场未完成) return;
        _入场未完成 = false;
        GD.Print("[StateMachine] 入场完成 → 启动招呼");
        启动招呼();
    }

    public override void _Process(double delta)
    {
        // —— 行为链推进（原有逻辑） ——
        if (_chainRunning)
        {
            _stepRemaining -= (float)delta;
            if (_stepRemaining <= 0f) 完成当前链节();
        }

        // —— 走动位移 ——
        if (_走动中) 推进走动((float)delta);

        // —— 计时器（每帧，精度足够） ——
        _运行秒 += delta;
        if (_重播冷却 > 0f) _重播冷却 -= (float)delta;
        推进保持与兜底((float)delta);

        // —— 数值层（P5）：心情/精力随时间漂移（睡眠中回充），每 30s 自动存盘 ——
        Soul.StatsTable.心跳((float)delta, CurrentState == Sleep);

        // —— 贴边隐藏（P2 行为层）：滑行到位 + 悬停探出/缩回 ——
        EdgeHide.每帧((float)delta);

        // —— 心跳（默认 1s，可配置） ——
        _心跳累加 += delta;
        if (_心跳累加 >= 设置.心跳秒)
        {
            _心跳累加 = 0;
            心跳();
        }
    }

    // ================= 状态切换（对外） =================

    /// <summary>直接切换状态（打断当前行为链）。Agent 的 set_state 与交互层都走这里。</summary>
    public static void SetState(string state) => SetState(state, -1f);

    /// <summary>切换状态并可覆盖保持/兜底时长（秒，&lt;0 = 用效果表默认）。</summary>
    public static void SetState(string state, float 秒)
    {
        _chainRunning = false;
        _chainQueue.Clear();
        _activeCallback = null;
        _走动中 = false;
        _排队状态 = null;      // 直接切换意味着有人接管了，排队项作废
        _排队兜底剩余 = 0f;

        if (!_效果表.TryGetValue(state, out var 效果))
        {
            GD.PrintErr($"[StateMachine] 未知状态: {state}（回退 idle）");
            state = Idle;
            效果 = _效果表[Idle];
        }

        // 别人抢状态 → 贴边隐藏让位（窗口先回原位；复位/缩进时自己会切到 edge_hide，不受影响）
        if (state != EdgeHideState && EdgeHide.占用中) EdgeHide.让位();

        var 变化 = CurrentState != state;
        CurrentState = state;

        // 交互序列：有些互动在素材里本身就是「进入 → 保持 → 退出(回待机)」多段
        // （VPet 摸头 = Touch_Head/A + B + C）。只播其中一段会在姿势上硬切到待机 —— 用户反馈「很突兀」。
        // 序列由「动画播完」回调逐段推进，不做时长猜测。
        _当前序列 = null;
        _序列序 = 0;
        string[] 序列 = null;
        if (_序列表.TryGetValue(state, out var 候选) && 候选.Length > 0 && CharAnim.有动画(候选[0])) 序列 = 候选;

        接管中 = 效果.锁定 || 序列 != null;

        if (序列 != null)
        {
            _保持剩余 = 0f; // 序列自己会收尾，不走保持计时
            _兜底剩余 = 0f;
        }
        else if (效果.持续)
        {
            _保持剩余 = 0f;
            var 上限 = 秒 >= 0f ? 秒 : 设置.持续态兜底秒;
            _兜底剩余 = 效果.锁定 && !效果.免兜底 && 上限 > 0f ? 上限 : 0f; // 只在锁定的持续态上设兜底（免兜底者除外）
        }
        else
        {
            _保持剩余 = 秒 >= 0f ? 秒 : 效果.秒;
            _兜底剩余 = 0f;
        }

        if (序列 != null)
        {
            _当前序列 = 序列;
            播放序列段();
        }
        else
        {
            应用表现(state);
        }
        if (变化) StateChanged?.Invoke(state);
    }

    /// <summary>CharAnim 在动画播完时调用：持续态期间重播当前状态，保持表现不断档。</summary>
    public static void 重播当前状态()
    {
        if (_重播冷却 > 0f) return;
        _重播冷却 = 0.05f;
        // 交互序列：播完一段就推进到下一段（最后一段播完 → 回 idle）
        if (_当前序列 != null) { 推进序列(); return; }
        // 贴边隐藏：阶段推进由 EdgeHide 自管（缩进→静止→探出→缩回→退出）
        if (CurrentState == EdgeHideState) { EdgeHide.动画播完(); return; }
        if (!_效果表.TryGetValue(CurrentState, out var 效果)) return;
        CharAnim.PlayState(选择池(效果));
    }

    /// <summary>播放交互序列的当前段。</summary>
    private static void 播放序列段()
    {
        if (_当前序列 == null) return;
        var 名 = _当前序列[_序列序];
        GD.Print($"[StateMachine] 交互序列 {_序列序 + 1}/{_当前序列.Length}: {名}");
        CharAnim.PlayNamed(名);
    }

    /// <summary>推进交互序列；已到末段则收尾回 idle（这就是「原版的后半段」）。</summary>
    private static void 推进序列()
    {
        if (_当前序列 == null) return;
        _序列序++;
        if (_序列序 >= _当前序列.Length)
        {
            _当前序列 = null;
            GD.Print("[StateMachine] 交互序列播完 → idle");
            SetState(Idle);
            return;
        }
        播放序列段();
    }

    /// <summary>
    /// 排队等待「当前动画播完」后再执行的交互反应。用于点击等被动反应——
    /// 直接 SetState 会把正在播的动画硬切掉，观感割裂。实现见 `尝试应用排队状态`。
    /// </summary>
    public static void 排队状态(string state, float 秒 = -1f)
    {
        if (!_效果表.ContainsKey(state)) return;
        _排队状态 = state;
        _排队秒 = 秒;
        _排队兜底剩余 = 设置.排队兜底秒;
        GD.Print($"[StateMachine] 排队状态: {state}（等当前动画播完）");
    }

    /// <summary>
    /// CharAnim 在动画播完时调用：有排队状态就应用它并接管本次转换（返回 true，调用方应直接 return）。
    /// 顺序上优先于 CharAnim 自身的 idle→fidget 逻辑，避免「先回 idle 再切」的闪跳。
    /// </summary>
    public static bool 尝试应用排队状态()
    {
        if (_排队状态 == null) return false;
        var 状态 = _排队状态;
        var 秒 = _排队秒;
        _排队状态 = null;
        _排队秒 = -1f;
        _排队兜底剩余 = 0f;
        GD.Print($"[StateMachine] 应用排队状态: {状态}");
        SetState(状态, 秒);
        return true;
    }

    /// <summary>
    /// 只改逻辑状态、**不驱动表现**。用于 CharAnim 已自行处理表现的场景（拖拽的 dragup/dragdown），
    /// 避免同一帧两次 Play 互相覆盖。
    /// <para>
    /// 同时**作废排队项**——这是「摸摸可延迟、拖拽必须立刻」的关键：用户摸完又立刻开始拖拽时，
    /// 拖拽要立刻生效，排队中的摸摸反应必须被丢掉，否则拖完还会补一个摸摸动画。
    /// </para>
    /// </summary>
    public static void 标记状态(string state)
    {
        if (!_效果表.ContainsKey(state)) return;
        if (_排队状态 != null)
        {
            GD.Print($"[StateMachine] 作废排队项（{state} 立即生效）");
            _排队状态 = null;
            _排队秒 = -1f;
            _排队兜底剩余 = 0f;
        }
        // 交互序列也要一并作废：拖拽必须立刻生效，不能等摸摸的三段序列播完
        if (_当前序列 != null)
        {
            GD.Print($"[StateMachine] 打断交互序列（{state} 立即生效）");
            _当前序列 = null;
            _序列序 = 0;
        }
        CurrentState = state;
        接管中 = _效果表[state].锁定;
    }

    /// <summary>
    /// 退出前调用：解除状态锁，保证退出动画的播完回调（真正 Quit 的那一步）不被接管逻辑吞掉。
    /// </summary>
    public static void 准备退出()
    {
        接管中 = false;
        EdgeHide.让位();   // 退出前把宠从屏外拉回来，别让它烂在边上
        _chainRunning = false;
        _chainQueue.Clear();
        _activeCallback = null;
        _走动中 = false;
        _排队状态 = null;
        _排队兜底剩余 = 0f;
        _当前序列 = null;
        _序列序 = 0;
    }

    // ================= 交互入口（唯一重置空闲的口径） =================

    /// <summary>任何交互都应调用：重置空闲计时；若在睡觉则唤醒并打招呼。</summary>
    public static void NotifyInteraction(string 来源 = "")
    {
        _空闲秒 = 0f;
        _已报可走动 = false;
        if (CurrentState == Sleep) 唤醒(来源);
    }

    /// <summary>左键单击（摸摸）：按下未超拖动阈值即松手。</summary>
    public static void 摸摸()
    {
        NotifyInteraction("摸摸");
        Soul.StatsTable.事件_摸摸(); // 被碰到就是正面互动（即便当时忙、反应被推迟或跳过）
        if (CurrentState is Drag or Think or Speak or Working) return; // 忙时不当成互动
        if (CurrentState == Sleep) return; // 唤醒流程已接管（会走 greet），让招呼播完
        // 不硬切：等当前这次动画播完再进入 interact
        排队状态(Interact);
    }

    /// <summary>从休眠唤醒：先打招呼，再回待机。</summary>
    public static void 唤醒(string 来源 = "")
    {
        GD.Print($"[StateMachine] 唤醒（{来源}）");
        Soul.StatsTable.事件_打招呼();
        SetState(Greet);
    }

    /// <summary>启动完成后打一次招呼（进入 greet → 自动回 idle）。</summary>
    public static void 启动招呼()
    {
        _空闲秒 = 0f;
        SetState(Greet);
    }

    // ================= 心跳：自主行为 =================

    private static bool _入场未完成 = true;

    private static void 心跳()
    {
        if (_入场未完成) return; // 入场动画没播完前不调度任何自主行为

        // 主人在用面板/鼠标停在桌宠身上 → 视为「正在互动」，不累计空闲
        if (面板可见() || 鼠标悬停桌宠())
        {
            _空闲秒 = 0f;
            return;
        }

        if (!设置.启用) return;

        // P6 环境感知：心跳推进「闲→忙」边沿；主人刚回来 → 打个招呼（节流 60s，且受主动预算约束）
        EnvironmentSense.心跳(设置.心跳秒);
        if (EnvironmentSense.刚回来)
        {
            GD.Print($"[StateMachine] 主人回来了（{EnvironmentSense.概述}）→ 打招呼");
            EventPool.记("回来", EventPool.归属.程序, "主人回来了");
            if (CurrentState == Idle && 主动预算剩余() > 0) { 记一次主动(); SetState(Greet); return; }
        }

        // 「离开」边沿（真正离开 = 打断久坐计数）
        if (!_上次主人不在 && EnvironmentSense.主人不在)
        {
            EventPool.记("离开", EventPool.归属.程序, $"主人离开（空闲 {EnvironmentSense.空闲秒:0}s）");
            _上次主人不在 = true;
            _活跃累计秒 = 0f; _久坐提醒次数 = 0; _久坐升级已写 = false;
        }
        else if (_上次主人不在 && !EnvironmentSense.主人不在)
        {
            _上次主人不在 = false;
        }

        // —— 行为事件（#3）：久坐提醒（程序侧简单判断；复杂判断归 Agent，见 BehaviorEventTick） ——
        if (BehaviorEventTick()) return;

        // 忙状态与拖拽中不调度自主行为；**贴边隐藏中也不调度**（它就是「在边上待着」，不该被入睡/走动打回屏内 —— 实测 bug）
        if (CurrentState is Drag or Think or Speak or Working or Listen or Greet or Interact or EdgeHideState) return;

        if (CurrentState == Sleep) return; // 已在睡，等交互唤醒

        _空闲秒 += 设置.心跳秒;

        var 目标睡眠 = 是否深夜() ? 设置.深夜睡眠秒 : 有效睡眠空闲秒; // 精力不济时提前入睡（P5 表达耦合）
        if (_空闲秒 >= 目标睡眠)
        {
            入睡();
            return;
        }

        // 自主走动：空闲达标后按随机间隔触发
        if (_空闲秒 >= 设置.走动空闲秒 && 允许主动())
        {
            if (!_已报可走动)
            {
                _已报可走动 = true;
                GD.Print($"[StateMachine] 空闲达 {_空闲秒:0}s（阈值 {设置.走动空闲秒:0}s），距下次走动 {_走动倒计时:0.0}s");
            }
            _走动倒计时 -= 设置.心跳秒;
            if (_走动倒计时 <= 0f)
            {
                尝试走动();
                _走动倒计时 = 随机间隔();
            }
        }
    }

    private static void 入睡()
    {
        GD.Print("[StateMachine] 空闲超时 → sleep");
        SetState(Sleep);
    }

    /// <summary>主动行为闸门：任一约束命中即禁止（「不打扰」是设计红线，见 idea.md §7）。</summary>
    private static bool 允许主动()
    {
        if (!设置.启用) return false;
        if (_入场未完成) return false;
        if (面板可见()) return false;
        if (鼠标悬停桌宠()) return false;
        if (CurrentState != Idle) return false;
        if (EnvironmentSense.应当静默) return false; // P6：主人在全屏应用里（游戏/视频/演示）→ 彻底安静
        return 主动预算剩余() > 0;
    }

    private static int 主动预算剩余()
    {
        _主动时间戳.RemoveAll(t => _运行秒 - t > 3600);
        return 设置.每小时主动上限 - _主动时间戳.Count;
    }

    private static void 记一次主动() => _主动时间戳.Add(_运行秒);

    // ================= 行为事件（#3，主人决策：主动清单与被动触发器合并为「行为事件」）=================
    // 简单判断 → 程序侧直接触发；复杂判断 → 写进事件池（owner=agent）**等 Agent 自己来读**（不做推送）。

    private static bool _上次主人不在;
    private static float _活跃累计秒;      // 本段「连续活跃」累计（真正离开会打断）
    private static float _久坐冷却剩余;
    private static int _久坐提醒次数;
    private static bool _久坐升级已写;

    /// <summary>久坐提醒：主人在电脑前连续活跃 ≥ `久坐提醒分钟` → 冒泡提醒休息（受「不打扰」五闸门约束）。</summary>
    private static bool BehaviorEventTick()
    {
        if (!EnvironmentSense.启用) return false;            // 环境感知关着 → 不测活跃（隐私优先）
        if (_久坐冷却剩余 > 0f) _久坐冷却剩余 -= 设置.心跳秒;
        if (EnvironmentSense.空闲秒 >= 60f) return false;    // 近 1 分钟无输入 = 不在电脑前/在休息
        _活跃累计秒 += 设置.心跳秒;

        if (_活跃累计秒 < 设置.久坐提醒分钟 * 60f) return false;
        if (_久坐冷却剩余 > 0f) return false;
        if (!允许主动()) return false;                        // 面板/悬停/忙态/全屏静默/每小时预算

        久坐提醒();
        return true;
    }

    private static void 久坐提醒()
    {
        var 分钟 = (int)(_活跃累计秒 / 60f);
        记一次主动();
        _久坐冷却剩余 = 设置.久坐提醒冷却分钟 * 60f;
        _久坐提醒次数++;
        EventPool.记("久坐提醒", EventPool.归属.程序, $"主人连续活跃 {分钟} 分钟 → 已提醒休息");
        Dialogue.显示临时标题(久坐语句(), 6000);
        SetState(Greet);   // 用「打招呼」的姿态把注意力勾过来，2.5s 后回 idle

        // 提醒满 2 次 = 这次坐得太久了 → 写一条**归属 Agent** 的事件（Agent 自己决定要不要更走心地说点什么）
        if (_久坐提醒次数 >= 2 && !_久坐升级已写)
        {
            _久坐升级已写 = true;
            EventPool.记("久坐超长", EventPool.归属.Agent,
                $"主人这一坐已经连续活跃 {分钟} 分钟（程序侧已提醒 {_久坐提醒次数} 次）。" +
                "要不要按你自己的方式关心一下？（读 user://context.md 能看到这条）");
        }
    }

    private static string 久坐语句()
    {
        var 候选 = new[]
        {
            "坐太久啦，起来伸个懒腰嘛～",
            "已经连着忙好久咯，喝口水再继续？",
            "腰要哭啦，站起来走两步吧～",
        };
        return 候选[Random.Shared.Next(候选.Length)];
    }

    // 探针专用
    public static void 探针_心跳一次() => 心跳();
    public static float 探针_活跃累计秒 => _活跃累计秒;
    public static int 探针_久坐提醒次数 => _久坐提醒次数;
    public static void 探针_清久坐冷却() => _久坐冷却剩余 = 0f;
    public static void 探针_重置久坐() { _活跃累计秒 = 0f; _久坐冷却剩余 = 0f; _久坐提醒次数 = 0; _久坐升级已写 = false; _上次主人不在 = false; }

    private static int _走动次数; // 累计走动次数（观测用）

    private static float 随机间隔() =>
        (float)GD.RandRange(设置.走动间隔最小秒, 设置.走动间隔最大秒) * 走动间隔倍率;

    /// <summary>
    /// 心情低落时少自己乱跑（走动间隔倍率）。数值只影响**频率与表现**，绝不改写人格与说话方式。
    /// 用「纯函数」暴露出来，探针可以直接断言，不用等真实时间流逝。
    /// </summary>
    public static float 走动间隔倍率 => Soul.StatsTable.心情低落 ? 1.6f : 1f;

    /// <summary>精力不济时更容易打瞌睡（提前入睡阈值，同样做成可断言的纯函数）。</summary>
    public static float 有效睡眠空闲秒 =>
        Soul.StatsTable.精力不济 ? MathF.Min(设置.睡眠空闲秒, 120f) : 设置.睡眠空闲秒;

    /// <summary>按数值挑情绪变体（`think-happy` / `think-poor` …）。返回 "" = 用池内随机（该池没有这个变体）。</summary>
    private static string 情绪变体(string 池)
    {
        if (池 is not ("think" or "say" or "interact")) return "";
        if (Soul.StatsTable.当前心情 >= 75f) return "happy";
        if (Soul.StatsTable.当前心情 < 35f) return "poor";
        return "";
    }

    private static float 首次间隔() =>
        (float)GD.RandRange(设置.首次走动最小秒, 设置.首次走动最大秒);

    /// <summary>自主走动：算目标位置 → 走行为链（链被 SetState 打断即中止）。</summary>
    private static void 尝试走动()
    {
        记一次主动();

        var 宠尺 = PetWindow.S;
        var 屏 = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
        var 当前X = DisplayServer.WindowGetPosition().X;

        // 朝屏幕中心方向走，减少贴边/出界
        var 中心X = (屏.Position.X + 屏.End.X) / 2 - 宠尺 / 2;
        var 方向 = 当前X < 中心X ? 1 : -1;
        var 距离 = GD.RandRange(设置.走动距离最小像素, 设置.走动距离最大像素);
        var 目标 = 当前X + 方向 * 距离;
        目标 = Math.Clamp(目标, 屏.Position.X + 8, Math.Max(屏.Position.X + 8, 屏.End.X - 宠尺 - 8));

        if (Math.Abs(目标 - 当前X) < 4) return; // 已在边界，没必要走

        _走动目标X = 目标;
        _走动速度 = Math.Max(1f, 设置.走动速度像素每秒);
        var 时长 = Math.Abs(目标 - 当前X) / _走动速度;

        GD.Print($"[StateMachine] 自主走动: {当前X} → {目标}（{时长:0.0}s）");
        _走动次数++;
        EnqueueChain(
            new ChainStep(WalkStart, 0.35f),
            new ChainStep(WalkLoop, 时长, () => { if (CurrentState == WalkLoop) SetState(Idle); }),
            new ChainStep(WalkEnd, 0.25f));
        _走动中 = true;
        Soul.StatsTable.事件_走动();
    }

    private static void 推进走动(float delta)
    {
        var 位置 = DisplayServer.WindowGetPosition();
        var 方向 = Math.Sign(_走动目标X - 位置.X);
        if (方向 == 0) { _走动中 = false; return; }
        var 新X = 位置.X + 方向 * (int)Math.Max(1, Math.Round(_走动速度 * delta));
        if (方向 > 0 && 新X > _走动目标X) 新X = _走动目标X;
        if (方向 < 0 && 新X < _走动目标X) 新X = _走动目标X;
        DisplayServer.WindowSetPosition(new Vector2I(新X, 位置.Y));
        if (新X == _走动目标X) _走动中 = false;
    }

    private static void 推进保持与兜底(float delta)
    {
        if (_保持剩余 > 0f)
        {
            _保持剩余 -= delta;
            if (_保持剩余 <= 0f)
            {
                _保持剩余 = 0f;
                if (_效果表.TryGetValue(CurrentState, out var 效果) && !效果.持续) SetState(Idle);
            }
        }

        if (_兜底剩余 > 0f)
        {
            _兜底剩余 -= delta;
            if (_兜底剩余 <= 0f)
            {
                _兜底剩余 = 0f;
                GD.Print($"[StateMachine] 兜底超时（{CurrentState} 未收到结束信号）→ idle");
                SetState(Idle);
            }
        }

        // 排队状态兜底：万一动画没回完成信号（池缺失/冻结），也要让排队项生效
        if (_排队兜底剩余 > 0f)
        {
            _排队兜底剩余 -= delta;
            if (_排队兜底剩余 <= 0f)
            {
                GD.Print("[StateMachine] 排队状态兜底生效（当前动画未回完成信号）");
                尝试应用排队状态();
            }
        }
    }

    // ================= 环境判定 =================

    private static bool 面板可见() =>
        ChatBox.可见 || ToolBar.可见 || SettingsWindow.可见;

    private static bool 鼠标悬停桌宠()
    {
        var 鼠 = DisplayServer.MouseGetPosition();
        var 位 = DisplayServer.WindowGetPosition();
        var 尺 = DisplayServer.WindowGetSize();
        return 鼠.X >= 位.X && 鼠.X < 位.X + 尺.X && 鼠.Y >= 位.Y && 鼠.Y < 位.Y + 尺.Y;
    }

    private static bool 是否深夜()
    {
        var 现 = DateTime.Now.TimeOfDay;
        var 起 = 解析时刻(设置.深夜起, new TimeSpan(23, 0, 0));
        var 止 = 解析时刻(设置.深夜止, new TimeSpan(7, 0, 0));
        return 起 <= 止 ? 现 >= 起 && 现 < 止 : 现 >= 起 || 现 < 止; // 跨零点
    }

    private static TimeSpan 解析时刻(string 文本, TimeSpan 兜底)
    {
        if (string.IsNullOrWhiteSpace(文本)) return 兜底;
        return TimeSpan.TryParseExact(文本.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var 结果)
            ? 结果
            : 兜底;
    }

    // ================= 行为链 API（原有，保持不变） =================

    /// <summary>链节：一个状态 + 持续时长(秒, 0=不限/等回调) + 完成回调。</summary>
    public struct ChainStep
    {
        public string State;
        public float Duration;
        public Action Callback;

        public ChainStep(string state, float duration = 0f, Action callback = null)
        {
            State = state;
            Duration = duration;
            Callback = callback;
        }
    }

    /// <summary>入队一条行为链并开始执行。示例：A→B = [walk_start, walk_loop, walk_end]。</summary>
    public static void EnqueueChain(params ChainStep[] steps)
    {
        _chainRunning = false;
        _chainQueue.Clear();
        _activeCallback = null;
        foreach (var step in steps) _chainQueue.Add(step);
        ExecuteNextStep();
    }

    /// <summary>用户打断当前链（任何直接交互调用）。</summary>
    public static void BreakChain()
    {
        _chainRunning = false;
        _chainQueue.Clear();
        _activeCallback = null;
        _走动中 = false;
        SetState(Idle);
    }

    private static void ExecuteNextStep()
    {
        if (_chainQueue.Count == 0)
        {
            _chainRunning = false;
            _activeCallback = null;
            _走动中 = false;
            SetState(Idle);
            StateChanged?.Invoke("chain_done");
            return;
        }
        var step = _chainQueue[0];
        _chainQueue.RemoveAt(0);
        // 链节状态直接落到 CurrentState（不走 SetState，避免清空链）
        CurrentState = step.State;
        接管中 = false;
        应用表现(step.State);
        _chainRunning = true;
        StateChanged?.Invoke(step.State);
        if (step.Duration <= 0f)
        {
            // 0 时长 = 立即完成，适合只表达一次状态切换的链节。
            step.Callback?.Invoke();
            ExecuteNextStep();
        }
        else
        {
            _stepRemaining = step.Duration;
            _activeCallback = step.Callback;
        }
    }

    private static void 完成当前链节()
    {
        _activeCallback?.Invoke();
        _activeCallback = null;
        ExecuteNextStep();
    }

    // ================= 表现映射 =================

    /// <summary>优先用专属池（P2 导入后），否则回退到当前已有资产的兼容池。</summary>
    private static string 选择池(状态效果 效果)
    {
        try
        {
            // 必须同时满足：① 池目录已被扫描到（动画池字典有非空列表）② 池已登记在 CharAnim.内置动画组
            // （未登记的池不会被预载进 SpriteFrames，播放会报 "Animation not found"）。
            if (CharAnim.池已注册(效果.目标池))
            {
                var 池 = Main.显示人物?.动画池字典;
                if (池 != null && 池.TryGetValue(效果.目标池, out var 列表) && 列表.Count > 0) return 效果.目标池;
            }
        }
        catch (Exception) { /* 人物数据未就绪：用兼容池 */ }
        return 效果.兼容池;
    }

    private static void 应用表现(string state)
    {
        if (state is WalkStart or WalkLoop or WalkEnd)
        {
            // 走动：按方向播 walk-left / walk-right（VPet 资产、已设为循环）。
            // 三个链节共用同一段动画 —— 已经是目标动画时不重播，否则会在链节边界重置相位、看起来一顿一顿。
            var 方向 = _走动目标X >= DisplayServer.WindowGetPosition().X ? "right" : "left";
            var 期望 = $"walk-{方向}";
            if (CharAnim.有动画(期望))
            {
                if (CharAnim.当前动画名_只读 != 期望) CharAnim.PlayNamed(期望);
            }
            else
            {
                CharAnim.PlayState("drag"); // 资产缺失时优雅降级（P1 的占位行为）
            }
            return;
        }
        if (!_效果表.TryGetValue(state, out var 效果)) 效果 = _效果表[Idle];
        // 贴边隐藏：表现由 EdgeHide 按阶段精确播（不走池内随机）
        if (state == EdgeHideState) { EdgeHide.应用表现(); return; }
        var 池 = 选择池(效果);
        // 情绪表达：心情好/糟时优先用该池的对应变体（如 think-happy / think-poor、摸头用 interact-happy）。
        // 该池没有这个变体就退回池内随机 —— 不硬造。
        var 变体 = 情绪变体(池);
        if (变体.Length > 0 && CharAnim.有动画($"{池}-{变体}")) { CharAnim.PlayNamed($"{池}-{变体}"); return; }
        CharAnim.PlayState(池);
    }

    // ================= 节律配置 =================

    /// <summary>节律参数（settings/behavior.json；缺失/损坏用内置默认值）。</summary>
    public static class 设置
    {
        public static bool 启用 = true;
        public static float 心跳秒 = 1f;
        public static float 睡眠空闲秒 = 600f;
        public static float 深夜睡眠秒 = 180f;
        public static string 深夜起 = "23:00";
        public static string 深夜止 = "07:00";
        public static float 走动空闲秒 = 30f;
        public static float 首次走动最小秒 = 10f;
        public static float 首次走动最大秒 = 25f;
        public static float 走动间隔最小秒 = 120f;
        public static float 走动间隔最大秒 = 300f;
        public static int 走动距离最小像素 = 60;
        public static int 走动距离最大像素 = 160;
        public static float 走动速度像素每秒 = 90f;
        public static int 每小时主动上限 = 8;
        public static float 持续态兜底秒 = 120f;
        public static float 排队兜底秒 = 3f;

        // —— P6 环境感知（**默认关**：主人不开，它就一次也不查） ——
        public static bool 环境感知启用 = false;
        public static float 离开阈值秒 = 300f;
        public static bool 全屏静默 = true;

        // —— P2 贴边隐藏（行为层；**默认开**：桌宠惯例行为，且让它更不打扰） ——
        public static bool 贴边隐藏启用 = true;
        public static int 贴边阈值像素 = 20;
        public static float 隐藏可见比例 = 0.55f;   // 对齐 VPet 官方观感：贴着边但看得见半个身子（原 0.30 太像「被推出屏外」）
        public static float 探出可见比例 = 0.65f;   // 原 0.82 → 实测角色露出 90%+（主人反馈「整个人飞出来了」）
        public static float 缩回延迟秒 = 1f;
        public static float 贴边滑行速度 = 360f;
        public static float 探出滑行速度 = 130f;   // 探出/缩回更慢，让动作看得见
        public static float 贴边循环间隔秒 = 2f;   // 循环动画（隐藏保持/探出）的重播间隔：主人要求「每 2 秒才播放一次」
        public static int 贴边左偏移像素 = 0;      // 微调：正=往屏内多推，负=往屏外多推（左右不对称就调这俩）
        public static int 贴边右偏移像素 = 0;
        public static float 久坐提醒分钟 = 90f;      // 连续活跃多久提醒休息（程序侧事件）；0 = 关
        public static float 久坐提醒冷却分钟 = 90f;  // 两次提醒的最小间隔

        public static void 加载()
        {
            foreach (var 路径 in new[]
                     {
                         ProjectSettings.GlobalizePath("user://behavior.json"),
                         ProjectSettings.GlobalizePath("res://settings/behavior.json"),
                     })
            {
                try
                {
                    if (!File.Exists(路径)) continue;
                    var 文本 = File.ReadAllText(路径);
                    if (string.IsNullOrWhiteSpace(文本)) continue;
                    using var 文档 = JsonDocument.Parse(文本);
                    var 根 = 文档.RootElement;
                    启用 = 取布尔(根, "启用", 启用);
                    心跳秒 = Math.Max(0.2f, 取浮点(根, "心跳秒", 心跳秒));
                    睡眠空闲秒 = 取浮点(根, "睡眠空闲秒", 睡眠空闲秒);
                    深夜睡眠秒 = 取浮点(根, "深夜睡眠秒", 深夜睡眠秒);
                    深夜起 = 取文本(根, "深夜起", 深夜起);
                    深夜止 = 取文本(根, "深夜止", 深夜止);
                    走动空闲秒 = 取浮点(根, "走动空闲秒", 走动空闲秒);
                    首次走动最小秒 = 取浮点(根, "首次走动最小秒", 首次走动最小秒);
                    首次走动最大秒 = 取浮点(根, "首次走动最大秒", 首次走动最大秒);
                    走动间隔最小秒 = 取浮点(根, "走动间隔最小秒", 走动间隔最小秒);
                    走动间隔最大秒 = 取浮点(根, "走动间隔最大秒", 走动间隔最大秒);
                    走动距离最小像素 = 取整数(根, "走动距离最小像素", 走动距离最小像素);
                    走动距离最大像素 = 取整数(根, "走动距离最大像素", 走动距离最大像素);
                    走动速度像素每秒 = Math.Max(1f, 取浮点(根, "走动速度像素每秒", 走动速度像素每秒));
                    每小时主动上限 = 取整数(根, "每小时主动上限", 每小时主动上限);
                    持续态兜底秒 = 取浮点(根, "持续态兜底秒", 持续态兜底秒);
                    环境感知启用 = 取布尔(根, "环境感知启用", 环境感知启用);
                    离开阈值秒 = 取浮点(根, "离开阈值秒", 离开阈值秒);
                    全屏静默 = 取布尔(根, "全屏静默", 全屏静默);
                    贴边隐藏启用 = 取布尔(根, "贴边隐藏启用", 贴边隐藏启用);
                    贴边阈值像素 = 取整数(根, "贴边阈值像素", 贴边阈值像素);
                    隐藏可见比例 = 取浮点(根, "隐藏可见比例", 隐藏可见比例);
                    探出可见比例 = 取浮点(根, "探出可见比例", 探出可见比例);
                    缩回延迟秒 = 取浮点(根, "缩回延迟秒", 缩回延迟秒);
                    贴边滑行速度 = 取浮点(根, "贴边滑行速度", 贴边滑行速度);
                    探出滑行速度 = 取浮点(根, "探出滑行速度", 探出滑行速度);
                    贴边循环间隔秒 = 取浮点(根, "贴边循环间隔秒", 贴边循环间隔秒);
                    贴边左偏移像素 = 取整数(根, "贴边左偏移像素", 贴边左偏移像素);
                    贴边右偏移像素 = 取整数(根, "贴边右偏移像素", 贴边右偏移像素);
                    久坐提醒分钟 = 取浮点(根, "久坐提醒分钟", 久坐提醒分钟);
                    久坐提醒冷却分钟 = 取浮点(根, "久坐提醒冷却分钟", 久坐提醒冷却分钟);
                    break;
                }
                catch (Exception e) { GD.PrintErr($"[StateMachine] 读节律配置失败 {路径}: {e.Message}"); }
            }

            if (走动间隔最大秒 < 走动间隔最小秒) 走动间隔最大秒 = 走动间隔最小秒;
            if (首次走动最大秒 < 首次走动最小秒) 首次走动最大秒 = 首次走动最小秒;
            if (走动距离最大像素 < 走动距离最小像素) 走动距离最大像素 = 走动距离最小像素;

            // 把 P6 开关交给感知层（它自己会遵守「未启用就一次也不查」）
            EnvironmentSense.启用 = 环境感知启用;
            EnvironmentSense.离开阈值秒 = Math.Max(30f, 离开阈值秒);
            EnvironmentSense.全屏静默 = 全屏静默;

            // 把 P2 贴边隐藏参数交给行为层（含夹取，避免配置写坏导致窗口跑到屏外回不来）
            EdgeHide.启用 = 贴边隐藏启用;
            EdgeHide.贴边阈值像素 = Math.Max(1, 贴边阈值像素);
            EdgeHide.可见比例 = Math.Clamp(隐藏可见比例, 0.10f, 0.90f);
            EdgeHide.探出可见比例 = Math.Clamp(探出可见比例, EdgeHide.可见比例, 1f);
            EdgeHide.缩回延迟秒 = Math.Max(0.1f, 缩回延迟秒);
            EdgeHide.滑行速度像素每秒 = Math.Max(60f, 贴边滑行速度);
            EdgeHide.探出滑行速度 = Math.Max(20f, 探出滑行速度);
            EdgeHide.循环间隔秒 = Math.Clamp(贴边循环间隔秒, 0.2f, 30f);
            EdgeHide.左偏移像素 = Math.Clamp(贴边左偏移像素, -400, 400);
            EdgeHide.右偏移像素 = Math.Clamp(贴边右偏移像素, -400, 400);
        }

        private static bool 取布尔(JsonElement 根, string 键, bool 兜底) =>
            根.TryGetProperty(键, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : 兜底;

        private static float 取浮点(JsonElement 根, string 键, float 兜底) =>
            根.TryGetProperty(键, out var v) && v.TryGetDouble(out var d) ? (float)d : 兜底;

        private static int 取整数(JsonElement 根, string 键, int 兜底) =>
            根.TryGetProperty(键, out var v) && v.TryGetInt32(out var i) ? i : 兜底;

        private static string 取文本(JsonElement 根, string 键, string 兜底) =>
            根.TryGetProperty(键, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? 兜底 : 兜底;
    }

    // ================= 供探针读取（只读） =================

    public static float 空闲秒_只读 => _空闲秒;
    public static bool 入场未完成_只读 => _入场未完成;
    public static int 走动次数_只读 => _走动次数;
    public static int 主动次数_只读 => _主动时间戳.Count;
    public static void 探针_推进空闲(float 秒) => _空闲秒 += 秒;
    public static void 探针_心跳() => 心跳();
    public static bool 探针_允许主动() => 允许主动();

    /// <summary>探针专用：确定性地把「保持/兜底」计时器推进指定秒数（不依赖真实帧率）。</summary>
    public static void 探针_推进时间(float 秒)
    {
        _运行秒 += 秒;
        if (_重播冷却 > 0f) _重播冷却 = Math.Max(0f, _重播冷却 - 秒);
        推进保持与兜底(秒);
    }

    /// <summary>探针专用：模拟「动画播完」回调（驱动交互序列前进 / 锁定态重播）。</summary>
    public static void 探针_动画播完回调() => 重播当前状态();

    /// <summary>探针专用：当前交互序列进度（0 = 无序列）。</summary>
    public static int 探针_序列进度 => _当前序列 == null ? 0 : _序列序 + 1;

    /// <summary>探针专用：模拟「当前动画播完」，返回是否消费了排队项。</summary>
    public static bool 探针_动画播完() => 尝试应用排队状态();

    /// <summary>探针专用：是否还有排队项。</summary>
    public static bool 探针_有排队项 => _排队状态 != null;
}