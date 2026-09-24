using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using desktop.script.Logic;
using desktop.script.Agent;
using desktop.script.UX;
using desktop.script.Game;
using Godot;

namespace desktop.script.State;

/// <summary>
/// 身体层：状态机 + 行为链 + **自主行为节律**。
///
/// 定位（见 AIPet-Agent.md §3）：本类是身体层的**唯一状态权威**——向上接收交互/Agent 事件，
/// 向下驱动 CharAnim；CharAnim 自身「动画播完回 idle」的旧逻辑在持续态期间让位（见 `接管中`）。
///
/// 状态效果表见 `_效果表`：每个状态 → 动画池（目标池 / 当前兼容池）+ 是否持续态 + 秒数。
/// 节律参数全部来自 `config/behavior.json`（改完重启生效，无需重编译）。
/// 约定：标识符英文，注释中文（见 AIPet-Agent.md §8）。
/// </summary>
public partial class StateMachine : Node
{
    // —— 状态常量 ——
    public const string Idle = "idle";
    /// <summary>生日彩蛋（VPet BDay 三段序列；config「生日」MM-dd 命中 → 入场完成后自动触发）。</summary>
    public const string Bday = "bday";
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

    /// <summary>捏脸（照 VPet 官方：长按脸触发）：表现由 FacePinch 自管，状态机只当「锁定持续态」占位。</summary>
    public const string PinchState = "pinch";

    /// <summary>气泡说话（伴随气泡的短动作）：**不锁定**（任何交互都能立刻打断）+ 定时回 idle；时长 = 气泡显示秒。</summary>
    public const string BubbleTalk = "bubble_talk";

    // —— P10 扩充（VPet 素材：摸身体 / 转身 / 干活进出场）——
    /// <summary>摸身体：单击落在「身体区」时走这条序列（VPet `Touch_Body` 三段）。</summary>
    public const string InteractBody = "interact_body";
    /// <summary>被摸转身：摸身体时按概率改成她转身躲一下（VPet `Touch_Body/Happy_Turn`）。</summary>
    public const string Turn = "turn";
    /// <summary>干活进出场：VPet `Switch_Up`（起身开工）/ `Switch_Down`（收工坐下）的过渡段。</summary>
    public const string WorkIn = "work_in";
    public const string WorkOut = "work_out";

    /// <summary>单击落在哪儿：头（默认，抱头反应）还是身体（另一套反应 / 概率转身）。</summary>
    public enum TouchPart { Head, Body }

    // —— 行为链状态（不映射固定动画，语义化） ——
    /// <summary>智能移动（组②，重构#4：抄 VPet 原库的移动方式）：表现由 MoveRunner 自管（A 起步 → B 循环 → 接力/收势；定义表 config/moves.json）。</summary>
    public const string MoveState = "move";
    /// <summary>音乐反应（组③）：包裹段自管（A 起跳 → 主段舞蹈循环 → C 收尾），检测在 MusicSense.cs。</summary>
    public const string Music = "music";

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
        public float 秒;    // 非持续态 = 保持时长；**持续态的秒值是死值**——SetState 传 -1 时统一用 `设置.持续态兜底秒`（默认 120s），
                            // 只有 `免兜底=true`（贴边隐藏）才是真正无限期。别再往持续态写「超时秒数」，它是不会被读的（子 Agent 核对时发现）
        public bool 免兜底; // true = 真·无限期持续态（贴边隐藏用）：不吃「持续态兜底」，否则 2 分钟被踢回 idle（实测 bug）
        public string 具体动画;   // 非空 = 这个状态固定播它（不按池随机）——「一个池服务多个状态」时用（switch-up / switch-down）
        public string 回落 = Idle; // 非持续态到点**回落到哪个状态**（默认 idle；干活过渡段回 working）
        public bool 包裹;   // true = 包裹段状态（组①）：进入先播 A（`{主名}-a`，无则池级 `{池}-a`）→ 循环主段 → 退出先播 C 再真正切。
                            // 只给有 A/C 素材的 think / sleep / 说话类标；会话期间主段变体钉死不换。
    }

    private static readonly Dictionary<string, 状态效果> _效果表 = new()
    {
        [Idle] = new 状态效果 { 目标池 = "idle", 兼容池 = "idle", 持续 = true, 锁定 = false, 秒 = 0 },
        [Interact] = new 状态效果 { 目标池 = "interact", 兼容池 = "fidget", 持续 = false, 锁定 = false, 秒 = 2.0f },
        [Drag] = new 状态效果 { 目标池 = "drag", 兼容池 = "drag", 持续 = true, 锁定 = false, 秒 = 0 },
        [Think] = new 状态效果 { 目标池 = "think", 兼容池 = "fidget", 持续 = true, 锁定 = true, 秒 = 0, 包裹 = true },
        [Speak] = new 状态效果 { 目标池 = "say", 兼容池 = "fidget", 持续 = true, 锁定 = true, 秒 = 0, 包裹 = true },
        [Listen] = new 状态效果 { 目标池 = "listen", 兼容池 = "fidget", 持续 = true, 锁定 = true, 秒 = 0 },  // 秒值不生效，见字段注释
        [Working] = new 状态效果 { 目标池 = "work", 兼容池 = "fidget", 持续 = true, 锁定 = true, 秒 = 0, 包裹 = true },
        [Sleep] = new 状态效果 { 目标池 = "sleep", 兼容池 = "idle", 持续 = true, 锁定 = true, 秒 = 0, 包裹 = true },
        [Greet] = new 状态效果 { 目标池 = "greet", 兼容池 = "celerate", 持续 = false, 锁定 = false, 秒 = 2.5f },
        // 贴边隐藏：持续 + 锁定；表现不走「池内随机」，由 EdgeHide.应用表现() 按阶段精确播（见应用表现）
        // 免兜底：贴边是**无限期**待着，不能吃 2 分钟的持续态兜底（否则会自己变 idle 挪回屏内 —— 实测 bug）
        [EdgeHideState] = new 状态效果 { 目标池 = "edge_hide", 兼容池 = "idle", 持续 = true, 锁定 = true, 免兜底 = true, 秒 = 0 },
        // 捏脸：同上 —— 表现由 FacePinch 三段自管（A 进入 → B 循环 → 松手 C），锁定 + 免兜底（按住多久都行）
        [PinchState] = new 状态效果 { 目标池 = "pinch", 兼容池 = "idle", 持续 = true, 锁定 = true, 免兜底 = true, 秒 = 0 },
        // 智能移动（组② 重构#4）：表现由 MoveRunner 自管（VPet Move 模型：数据驱动 + 兼容接力），锁定 + 免兜底（走/爬多久都行）
        [MoveState] = new 状态效果 { 目标池 = "move", 兼容池 = "idle", 持续 = true, 锁定 = true, 免兜底 = true },
        // 音乐反应（组③）：包裹段（A 起跳 → 主段舞蹈 → C 收尾）；退出由 MusicSense 在静音够了时 SetState(Idle) 触发
        [Music] = new 状态效果 { 目标池 = "music", 兼容池 = "idle", 持续 = true, 锁定 = true, 免兜底 = true, 包裹 = true },
        // 气泡说话（P8）：**不锁定**（动作可被打断；重播由「包裹中」兜住，见 CharAnim 播完回调）+ 定时回 idle；
        // 真正的时长由 冒泡说话() 用「气泡显示秒」覆盖传入。组①加包裹段：say-{感情}-a 进入 → B 循环 → C 退出。
        [BubbleTalk] = new 状态效果 { 目标池 = "say", 兼容池 = "fidget", 持续 = false, 锁定 = false, 秒 = 4f, 包裹 = true },
        // 生日彩蛋（2026-09-20 组①）：三段序列自管（A惊喜→B摇摆→C比心），播完回 idle
        [Bday] = new 状态效果 { 目标池 = "bday", 兼容池 = "idle", 持续 = false, 锁定 = false, 秒 = 0f },
        // —— P10 ——（摸身体 / 转身 / 干活进出场；素材来源见 tools/README.md 池表）
        [InteractBody] = new 状态效果 { 目标池 = "interact_body", 兼容池 = "interact", 持续 = false, 锁定 = false, 秒 = 2.0f },
        [Turn] = new 状态效果 { 目标池 = "turn", 兼容池 = "interact_body", 持续 = false, 锁定 = false, 秒 = 1.6f },
        [WorkIn] = new 状态效果 { 目标池 = "switch", 兼容池 = "work", 具体动画 = "switch-up", 持续 = false, 锁定 = true, 秒 = 1.7f, 回落 = Working },
        [WorkOut] = new 状态效果 { 目标池 = "switch", 兼容池 = "work", 具体动画 = "switch-down", 持续 = false, 锁定 = true, 秒 = 1.8f, 回落 = Idle },
    };

    /// <summary>
    /// 多段交互序列：状态 → 动画名序列。有些互动在素材里本身就是「进入 → 保持 → 退出(回待机)」多段
    /// （VPet 摸头 = Touch_Head/A + B + C），逐段播放、末段播完自动回 idle，避免在姿势上硬切。
    /// 动画名 = `{池}-{变体}`（见 §3.1.2 导入器）；序列不存在时自动回退到效果表的单池路径。
    /// </summary>
    private static readonly Dictionary<string, string[]> _序列表 = new()
    {
        [Interact] = ["interact-a", "interact-b", "interact-c"],
        [InteractBody] = ["interact_body-a", "interact_body-b", "interact_body-c"],   // P10：VPet Touch_Body（摸身体）
        [Turn] = ["turn-a", "turn-b", "turn-c"],                                       // P10：VPet Happy_Turn（被摸转身）
        [Bday] = ["bday-a", "bday-b", "bday-c"],                                       // 2026-09-20 组①：VPet BDay（生日彩蛋）
    };

    private static string[] _当前序列;
    private static int _序列序;
    /// <summary>重构#5（VPet SetContinue）：B 段「续命」标记——序列进行中又摸了一次，
    /// 则 B 这圈播完从头再来一遍而不进 C（连续摸头不会一直重播进场动作）。</summary>
    private static bool _序列续命;

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
    private static float _走动倒计时;    // 空闲达标后，距**首次**自主走动的秒数（重构#3 后只管首次；后续走概率爬坡骰子）
    private static float _爬坡待机秒;    // 连续待机秒（= VPet CountNomal 的时间版）：走动/互动后清零
    private static float _爬坡骰子秒;    // 爬坡骰子累计：每满「爬坡秒」掷一次（= VPet EventTimer 15s）
    private static readonly Random 骰子 = new Random();   // 爬坡骰子专用
    private static float _重播冷却;      // 防止动画极短时疯狂重播
    private static string _排队状态;     // 排队等待「当前动画播完」再切的交互反应
    private static float _排队秒 = -1f;
    private static float _排队兜底剩余;  // 防止动画不回完成信号导致排队状态永远不生效

    // ── 包裹段（组①）：think / sleep / 说话类的「进入 A → 循环 B → 退出 C」会话状态 ──
    private static string _包裹主名;      // 本会话钉死的主段动画名（如 think-happy / say-shining / sleep-loop）；会话结束清空
    private static bool _进入段中;        // 正在播 A 段（播完接主段）
    private static bool _退出段中;        // 正在播 C 段（播完落地 _退出目标 的切换）
    private static string _退出目标;      // 退出段播完后要切到的状态
    private static float _退出目标秒 = -1f;
    /// <summary>探针隔离：断言「即时切换」语义的探针置 true，包裹段整体旁路。</summary>
    public static bool 探针_禁用包裹;
    /// <summary>包裹态进入时的「主段指定」（MusicSense 挑歌等用；空 = 交给 挑主名 默认挑）。用后即清。</summary>
    public static string 包裹主名指定;
    /// <summary>包裹会话进行中（CharAnim 用它把「动画播完」派发给状态机——非锁定态如气泡说话也走重播）。</summary>
    public static bool 包裹中 => _包裹主名 != null;
    private static bool _已报可走动;     // 「空闲达标」日志只报一次，避免刷屏
    private static readonly List<double> _主动时间戳 = new();
    private static double _运行秒;

    // （旧「走动执行状态」字段 _走动中/_本次爬行/_走动目标X/_走动速度 已删——重构#4：位移与圈推进归 MoveRunner）

    // ================= 生命周期 =================

    public override void _Ready()
    {
        Instance = this;
        设置.加载();
        MoveRunner.加载();   // 重构#4：移动定义表（config/moves.json；缺失用内置默认）
        CurrentState = Idle;
        _走动倒计时 = 首次间隔(); // 首次移动用短间隔，否则要等「走动空闲秒 + 爬坡骰子」才看得到
        // 注意：这里**不播放表现**。入场动画由 CharAnim.载入人物动画() 负责，
        // 本节点是 game.tscn 里 Main 的兄弟且顺序在后，若在此 PlayState("idle")（deferred）
        // 会抢掉入场动画，导致 case "enter" 永不触发。入场门由 CharAnim 回调解除。
        GD.Print($"[StateMachine] 就绪: 启用={设置.启用} 睡眠={设置.睡眠空闲秒}s 走动={设置.走动空闲秒}s 上限={设置.每小时主动上限}/h");
    }

    /// <summary>CharAnim 入场动画播完时调用：解除入场门、打一次招呼姿态，并排上**启动问候**（每次启动一次）。</summary>
    public static void 入场完成()
    {
        if (!_入场未完成) return;
        _入场未完成 = false;
        GD.Print("[StateMachine] 入场完成 → 启动招呼");
        启动招呼();
        DailyRoutine.启动问候();   // 主人定的口径：每次启动打一次招呼（话由 config/phrases.json 定）
        入场完成后?.Invoke();
        入场完成后 = null;   // 一次性：入场只完成一次，回调也只用一次
    }

    public override void _Process(double delta)
    {
        // —— 游戏模式：办公表现全暂停（模式层接管，StateMachine 不驱动任何办公行为）——
        // 数值心跳例外：它记的是「主人情绪读数」，与桌宠表演无关。
        if (游戏模式中)
        {
            Soul.StatsTable.心跳((float)delta);
            return;
        }

        // —— 行为链推进（原有逻辑） ——
        if (_chainRunning)
        {
            _stepRemaining -= (float)delta;
            if (_stepRemaining <= 0f) 完成当前链节();
        }

        // —— 计时器（每帧，精度足够） ——
        _运行秒 += delta;
        if (_重播冷却 > 0f) _重播冷却 -= (float)delta;
        推进保持与兜底((float)delta);
        推进本地话语冷却((float)delta);   // 本地话语（被摸等）的冷却

        // —— 数值层：主人情绪读数衰减（每 30s 向中性 50 回 5 点），每 30s 自动存盘 ——
        Soul.StatsTable.心跳((float)delta);

        // —— 贴边隐藏（P2 行为层）：滑行到位 + 悬停探出/缩回 ——
        EdgeHide.每帧((float)delta);

        // —— 捏脸：长按计时（按住脸到阈值 → 触发） ——
        FacePinch.每帧((float)delta);

        // —— 智能移动（组② 重构#4）：位移 + 圈推进，由 MoveRunner 自管 ——
        MoveRunner.每帧((float)delta);

        // —— 音乐反应（组③）：系统音量采样 + 起跳/收场 ——
        MusicSense.每帧((float)delta);

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
        // 捏脸同理：别人抢状态就放弃（表现交给新状态）
        if (state != PinchState && FacePinch.占用中) FacePinch.取消();
        // 智能移动同理：别人抢状态就终止移动（窗口拉回屏内）
        if (state != MoveState && MoveRunner.占用中) MoveRunner.让位();
        // 坐卧会话同理（重构#9）：别人抢状态 → 会话作废（表现交给新状态；回 idle 不算「别人抢」）
        if (state != Idle && CharAnim.坐卧会话中) CharAnim.作废坐卧会话();

        var 变化 = CurrentState != state;

        // ── 包裹段（组①）：think / sleep / 说话类的「进入 A → 循环 B → 退出 C」──
        // 已在会话中又切到同状态（连发气泡等）：只复位计时，保持当前段相位，不重播；
        // 若正在播退出段则取消退出、回主段（状态又被要回来了，如 think 续期赶上退出窗口）。
        if (!变化 && _包裹主名 != null)
        {
            if (_退出段中)
            {
                _退出段中 = false;
                _退出目标 = null;
                if (CharAnim.有动画(_包裹主名)) CharAnim.PlayNamed(_包裹主名);
            }
            设定计时(效果, 秒);
            return;
        }
        // 退出包裹态：先播 C 段，播完在 重播当前状态() 里落地切换（延迟极短，衔接顺）。
        // 硬接管（拖拽 / 捏脸 / 贴边）跳过退出段 —— 用户上手要立刻响应，不等过渡。
        if (_包裹主名 != null && !_进入段中 && !探针_禁用包裹
            && state != Drag && state != PinchState && state != EdgeHideState)
        {
            if (_退出段中)
            {
                _退出目标 = state;   // 已在退出段：更新目标即可
                _退出目标秒 = 秒;
                return;
            }
            var 退出段 = 段名(_包裹主名, "c");
            if (退出段 != null)
            {
                _退出段中 = true;
                _退出目标 = state;
                _退出目标秒 = 秒;
                GD.Print($"[StateMachine] 包裹退出段: {退出段}（目标 {state}）");
                CharAnim.PlayNamed(退出段);
                return;
            }
        }

        CurrentState = state;
        _包裹主名 = null;      // 真切换：旧会话结束（若新状态也是包裹态，下面重建）
        _进入段中 = false;
        _退出段中 = false;
        _退出目标 = null;

        // 交互序列：有些互动在素材里本身就是「进入 → 保持 → 退出(回待机)」多段
        // （VPet 摸头 = Touch_Head/A + B + C）。只播其中一段会在姿势上硬切到待机 —— 用户反馈「很突兀」。
        // 序列由「动画播完」回调逐段推进，不做时长猜测。
        _当前序列 = null;
        _序列序 = 0;
        _序列续命 = false;
        string[] 序列 = null;
        if (_序列表.TryGetValue(state, out var 候选) && 候选.Length > 0 && CharAnim.有动画(候选[0])) 序列 = 候选;

        接管中 = 效果.锁定 || 序列 != null;

        if (序列 != null)
        {
            _保持剩余 = 0f; // 序列自己会收尾，不走保持计时
            _兜底剩余 = 0f;
        }
        else
        {
            设定计时(效果, 秒);
        }

        if (序列 != null)
        {
            _当前序列 = 序列;
            播放序列段();
        }
        else if (效果.包裹 && !探针_禁用包裹)
        {
            // 包裹态进入：挑一个主名钉住本次会话（变体不再随机换）；池里有 A 段就先播 A，播完接主段
            _包裹主名 = string.IsNullOrEmpty(包裹主名指定) ? 挑主名(选择池(效果)) : 包裹主名指定;
            包裹主名指定 = null;
            var 进入段 = _包裹主名 != null ? 段名(_包裹主名, "a") : null;
            if (进入段 != null)
            {
                _进入段中 = true;
                CharAnim.PlayNamed(进入段);
            }
            else if (_包裹主名 != null && CharAnim.有动画(_包裹主名))
            {
                CharAnim.PlayNamed(_包裹主名);   // 没有 A 段就直接起主段
            }
            else
            {
                _包裹主名 = null;                // 池空：退回普通表现
                应用表现(state);
            }
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
        // 包裹段：C 播完 → 落地延迟切换；A 播完 → 接主段（先清会话再派发，避免又走一次退出段）
        if (_退出段中)
        {
            _退出段中 = false;
            var 目标 = _退出目标;
            var 目标秒 = _退出目标秒;
            _退出目标 = null;
            _退出目标秒 = -1f;
            _包裹主名 = null;
            GD.Print($"[StateMachine] 包裹退出完成 → {目标}");
            if (!string.IsNullOrEmpty(目标)) { SetState(目标, 目标秒); return; }
        }
        if (_进入段中)
        {
            _进入段中 = false;
            if (_包裹主名 != null && CharAnim.有动画(_包裹主名)) { CharAnim.PlayNamed(_包裹主名); return; }
        }
        // 交互序列：播完一段就推进到下一段（最后一段播完 → 回 idle）
        if (_当前序列 != null) { 推进序列(); return; }
        // 贴边隐藏：阶段推进由 EdgeHide 自管（缩进→静止→探出→缩回→退出）
        if (CurrentState == EdgeHideState) { EdgeHide.动画播完(); return; }
        // 捏脸：段推进由 FacePinch 自管（A → B 循环 → 松手 C）
        if (CurrentState == PinchState) { FacePinch.动画播完(); return; }
        // 智能移动：圈推进由 MoveRunner 自管（A 进入 → 吸附 → B 循环 → 接力/收势）
        if (CurrentState == MoveState) { MoveRunner.动画播完(); return; }
        // 包裹态主段循环：重播钉死的主段（变体不再随机换）
        if (_包裹主名 != null && CharAnim.有动画(_包裹主名)) { CharAnim.PlayNamed(_包裹主名); return; }
        if (!_效果表.TryGetValue(CurrentState, out var 效果)) return;
        CharAnim.PlayState(选择池(效果));
    }

    /// <summary>播放交互序列的当前段。</summary>
    /// <summary>把变体插进动画名末尾那段之前：`interact-a` + happy → `interact-happy-a`。</summary>
    private static string 带变体(string 动画名, string 变体)
    {
        var i = 动画名.LastIndexOf('-');
        return i < 0 ? 动画名 : 动画名[..i] + "-" + 变体 + 动画名[i..];
    }

    private static void 播放序列段()
    {
        if (_当前序列 == null) return;
        var 名 = _当前序列[_序列序];
        // 三档 / 心情择档：序列也能换档（`interact-a` → `interact-happy-a`，摸头高兴档）——素材在才换，不硬造
        var 池名 = 名.Contains('-') ? 名[..名.IndexOf('-')] : 名;
        var 档 = 情绪变体(池名);
        if (档.Length > 0) { var 换 = 带变体(名, 档); if (CharAnim.有动画(换)) 名 = 换; }
        GD.Print($"[StateMachine] 交互序列 {_序列序 + 1}/{_当前序列.Length}: {名}");
        CharAnim.PlayNamed(名);
    }

    /// <summary>推进交互序列；已到末段则收尾回 idle（这就是「原版的后半段」）。</summary>
    private static void 推进序列()
    {
        if (_当前序列 == null) return;
        // 重构#5：B 段（中段）续命——期间又摸了一次就从头再演这圈，不进 C（VPet SetContinue 语义）
        if (_序列续命)
        {
            _序列续命 = false;
            if (_序列序 > 0 && _序列序 < _当前序列.Length - 1)
            {
                GD.Print($"[StateMachine] 触摸续命：重播 B 段（第 {_序列序 + 1}/{_当前序列.Length} 段）");
                播放序列段();
                return;
            }
        }
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
            _序列续命 = false;
        }
        // 包裹会话同样作废：拖拽/松手立即生效，旧会话不能残留（否则重播会错播上一条会话的主段）
        _包裹主名 = null;
        _进入段中 = false;
        _退出段中 = false;
        _退出目标 = null;
        CurrentState = state;
        接管中 = _效果表[state].锁定;
    }

    /// <summary>
    /// 退出前调用：解除状态锁，保证退出动画的播完回调（真正 Quit 的那一步）不被接管逻辑吞掉。
    /// </summary>
    public static void 准备退出()
    {
        接管中 = false;
        _包裹主名 = null;   // 退出前清包裹会话：退出动画的播完回调不能被包裹逻辑吞掉（同 接管中 的理由）
        _进入段中 = false;
        _退出段中 = false;
        _退出目标 = null;
        EdgeHide.让位();   // 退出前把宠从屏外拉回来，别让它烂在边上
        MoveRunner.让位(); // 智能移动同理：走/爬半路退出也要把窗口拉回来
        _chainRunning = false;
        _chainQueue.Clear();
        _activeCallback = null;
        _排队状态 = null;
        _排队兜底剩余 = 0f;
        _当前序列 = null;
        _序列序 = 0;
        _序列续命 = false;
    }

    // ================= 交互入口（唯一重置空闲的口径） =================

    /// <summary>任何交互都应调用：重置空闲计时；若在睡觉则唤醒并打招呼。</summary>
    public static void NotifyInteraction(string 来源 = "")
    {
        _空闲秒 = 0f;
        _爬坡待机秒 = 0f;   // 重构#3：互动后爬坡清零——刚陪过它，不该马上又自主走动（VPet CountNomal = 0）
        _已报可走动 = false;
        DailyRoutine.交互();   // 时间驱动行为：当天首次见面 → 问个好（Plan #11）
        if (CurrentState == Sleep) 唤醒(来源);
    }

    /// <summary>
    /// 左键单击（摸摸）：按下未超拖动阈值即松手。P10 起**分部位** —— 头（默认）= 抱头反应；
    /// 身体 = 另一套反应（VPet `Touch_Body`），并按 30% 概率改成**转身躲一下**（`Happy_Turn`）。
    /// 部位由 `WindowDrag` 用窗口比例矩形判定（脸区优先）。
    /// </summary>
    public static void 摸摸(TouchPart 部位 = TouchPart.Head)
    {
        NotifyInteraction(部位 == TouchPart.Body ? "摸摸·身体" : "摸摸");
        // 数值不因互动变化（2026-09-20 主人定：数值只影响回复策略、不加入互动）
        if (CurrentState is Drag or Think or Speak or Working or WorkIn or WorkOut) return; // 忙时不当成互动
        if (CurrentState == Sleep) return; // 唤醒流程已接管（会走 greet），让招呼播完
        // 不硬切：等当前这次动画播完再进入对应反应
        var 目标 = 部位 == TouchPart.Body && CharAnim.有动画("interact_body-a")
            ? (Random.Shared.NextDouble() < 0.3 && CharAnim.有动画("turn-a") ? Turn : InteractBody)
            : Interact;
        // 重构#5（VPet DisplayToTouchHead/Body 语义，MainDisplay.cs:146-165）：同类触摸序列进行中又摸——
        // A 段忽略（进场动作不打断）；B 段续命（这圈播完重播 B、不进 C——连续摸头不会反复重演进场）；
        // C 段照常排队（退场播完开新一轮）。
        if (CurrentState == 目标 && _当前序列 != null)
        {
            if (_序列序 == 0) return;                       // A 段：忽略
            if (_序列序 < _当前序列.Length - 1)             // B 段：续命
            {
                _序列续命 = true;
                GD.Print("[StateMachine] 触摸续命：B 段这圈播完从头再来（VPet SetContinue）");
                return;
            }
        }
        else if (CurrentState is InteractBody or Turn) return;   // 在对另一种身体反应做反应了：不打断（原语义）
        排队状态(目标);
        // 本地模式（没接 Agent）时，被摸也要有话说 —— 走话语表；接了 Agent 则由 Agent 自己回
        本地说话("被摸", 15f);
    }

    /// <summary>开工（P10）：先播「起身」（VPet `Switch_Up`，到点自动落到 `working`）；素材缺就直接进 working。</summary>
    public static void 开始干活() => SetState(CharAnim.有动画("switch-up") ? WorkIn : Working);

    /// <summary>
    /// 收工（P10）：先播「坐下」（VPet `Switch_Down`，到点自动回 idle）；素材缺就直接回 idle。
    /// 只在干活（working / 开工过渡）时有效 —— 别把别处的 idle 切换也绕进来。
    /// </summary>
    public static void 结束干活()
    {
        if (CurrentState is not (Working or WorkIn)) return;
        // 「办公即游戏」钩子（最小切片）：干完一次活 → 游戏侧记一颗办公星。
        // 只写游戏容器（GameSession），不改办公表现 —— 符合「办公表现不受游戏影响」的不变式。
        GameSession.办公星加一();
        GD.Print($"[Game] 干完一次活 → 办公星 +1（共 {GameSession.办公星}）");
        SetState(CharAnim.有动画("switch-down") ? WorkOut : Idle);
    }

    /// <summary>从休眠唤醒：先打招呼，再回待机。</summary>
    public static void 唤醒(string 来源 = "")
    {
        GD.Print($"[StateMachine] 唤醒（{来源}）");
        SetState(Greet);
    }

    /// <summary>启动完成后打一次招呼（进入 greet → 自动回 idle）。</summary>
    public static void 启动招呼()
    {
        _空闲秒 = 0f;
        SetState(Greet);
    }

    // ================= 心跳：自主行为 =================

    /// <summary>入场完成后的一次性回调（首启提示等启动流程挂这里——**入场期间不许冒泡**，会顶状态、抢入场动画）。</summary>
    public static event System.Action 入场完成后;

    private static bool _入场未完成 = true;

    private static void 心跳()
    {
        if (_入场未完成) return; // 入场动画没播完前不调度任何自主行为

        DailyRoutine.推进(设置.心跳秒);   // 时间驱动行为（问候 / 喝水 / 磁盘）：计时始终推进，冒泡另受闸门约束

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
        if (CurrentState is Drag or Think or Speak or Working or Listen or Greet or Interact or EdgeHideState or PinchState) return;

        // 坐卧会话中（重构#9）：VPet 里 StateONE/StateTWO 期间 DisplayType 非 Default/Work → EventTimer 的
        // 显示骰子整块被跳过（IsIdel=false）。等同处理：会话期间不入睡、不掷移动/坐卧骰子（每日问候等数值心跳照常）。
        if (CharAnim.坐卧会话中) return;

        if (CurrentState == Sleep) return; // 已在睡，等交互唤醒

        _空闲秒 += 设置.心跳秒;

        var 目标睡眠 = 是否深夜() ? 设置.深夜睡眠秒 : 设置.睡眠空闲秒;
        if (_空闲秒 >= 目标睡眠)
        {
            入睡();
            return;
        }

        // 自主移动：空闲达标后触发（重构#4：走/爬/掉全归 MoveRunner 的移动表，这里只当调度器）。
        // 重构#3（2026-09-22）：首次走动仍走短倒计时（首跑体验：启动后 10~25s 内见它动一次）；
        // 之后的每次移动改用 VPet 式**概率爬坡骰子**（MainLogic.cs:489-494 EventTimer_Elapsed）：
        //   每「爬坡秒」掷一次 Next(max(爬坡下限, 爬坡周期 - 连续待机秒))，命中前「爬坡移动槽」个值 → 移动。
        //   连续待机越久窗口越小、走动越勤；互动/移动后清零重新爬坡（= VPet CountNomal 语义）。
        //   旧「固定 120~300s 均匀倒计时」没有爬坡——互动后和闲置 10 分钟一个频率，机械。
        if (_空闲秒 >= 设置.走动空闲秒 && 允许主动())
        {
            if (!_已报可走动)
            {
                _已报可走动 = true;
                GD.Print($"[StateMachine] 空闲达 {_空闲秒:0}s（阈值 {设置.走动空闲秒:0}s），距首次移动 {_走动倒计时:0.0}s");
            }
            if (_移动次数 == 0)
            {
                // 首次：短倒计时保底（否则要等骰子爬坡几分钟才动，首跑看不到走动）
                _走动倒计时 -= 设置.心跳秒;
                if (_走动倒计时 <= 0f) 尝试移动();
            }
            else
            {
                // 后续：概率爬坡骰子（连续待机秒只在 Idle 累计——移动期间 允许主动() 本就关门）
                _爬坡待机秒 += 设置.心跳秒;
                _爬坡骰子秒 += 设置.心跳秒;
                if (_爬坡骰子秒 >= 设置.爬坡秒)
                {
                    _爬坡骰子秒 = 0f;
                    // 重构#9：按「槽位」分诊（VPet EventTimer_Elapsed 的完整 switch）——
                    // 移动 3 槽（case 0/1/2）/ 坐卧 1 槽（case 6 的 StateONE）/ 其余槽什么都不发生。
                    掷爬坡一次();
                }
            }
        }
    }

    /// <summary>重构#3：爬坡骰子的窗口 = max(下限, 周期 - 连续待机秒)——闲置越久窗口越小（VPet `Math.Max(20, InteractionCycle - CountNomal)`）。</summary>
    public static int 爬坡窗口(int 连续待机秒)
        => Math.Max(设置.爬坡下限, 设置.爬坡周期 - 连续待机秒);

    /// <summary>重构#3/#9：掷一次爬坡骰子的**槽位**（VPet `switch (Rnd.Next(rnddisplay))`）——
    /// 槽 &lt; 爬坡移动槽 → 移动（case 0/1/2）；再往后 &lt; 移动槽+坐卧槽 → 坐卧（case 6 的 StateONE）；
    /// 其余本次什么都不发生。纯函数（不读时钟、不动状态），便于探针直接验证分布。</summary>
    public static int 爬坡掷槽(int 连续待机秒, Random 随) => 随.Next(爬坡窗口(连续待机秒));

    /// <summary>重构#3：命中「移动」槽（= VPet case 0/1/2）。</summary>
    public static bool 爬坡掷骰(int 连续待机秒, Random 随)
        => 爬坡掷槽(连续待机秒, 随) < 设置.爬坡移动槽;

    /// <summary>掷一次爬坡骰子并按槽位分派（VPet EventTimer_Elapsed 的完整 switch）。心跳与探针共用同一路径。</summary>
    private static void 掷爬坡一次()
    {
        var 槽 = 爬坡掷槽((int)_爬坡待机秒, 骰子);
        if (槽 < 设置.爬坡移动槽) 尝试移动();
        else if (设置.坐卧启用 && 槽 < 设置.爬坡移动槽 + 设置.坐卧槽) 尝试坐卧();
    }

    /// <summary>探针：立即掷一次爬坡骰子（与心跳完全同一条分派路径）。</summary>
    public static void 探针_掷爬坡一次() => 掷爬坡一次();

    private static void 入睡()
    {
        GD.Print("[StateMachine] 空闲超时 → sleep");
        SetState(Sleep);
    }

    /// <summary>游戏模式中（模式层接管表现）：办公自主行为全暂停。见 script/Game/。</summary>
    public static bool 游戏模式中 => Mode.ModeManager.CurrentMode == Mode.ModeManager.Mode.Game;

    /// <summary>主动行为闸门：任一约束命中即禁止（「不打扰」是设计红线，见 idea.md §7）。</summary>
    private static bool 允许主动()
    {
        if (!设置.启用) return false;
        if (_入场未完成) return false;
        if (游戏模式中) return false;   // 游戏模式：模式层接管，办公不主动
        if (面板可见()) return false;
        if (鼠标悬停桌宠()) return false;
        if (CurrentState != Idle) return false;
        if (EnvironmentSense.应当静默) return false; // P6：主人在全屏应用里（游戏/视频/演示）→ 彻底安静
        return 主动预算剩余() > 0;
    }

    /// <summary>主动行为闸门（供状态机外部共用）：任一约束命中即禁止。见 <see cref="允许主动"/>。</summary>
    public static bool 主动闸门开放 => 允许主动();

    /// <summary>演出闸门（音乐舞蹈等「响应型」主动表现用）：空闲 + 环境安静 + 入场完成 —— 与 允许主动 的区别是**不吃每小时预算**（对声音的反应不是打扰型行为）。</summary>
    public static bool 演出闸门开放 =>
        !_入场未完成 && CurrentState == Idle && !面板可见() && !鼠标悬停桌宠() && !EnvironmentSense.应当静默;

    /// <summary>占一次主动预算（其他模块冒泡也要记账，否则「每小时上限」会被绕过）。</summary>
    public static void 占一次主动预算() => 记一次主动();

    /// <summary>主动预算剩余次数（供行为层其他模块判断「这次还说不说」）。</summary>
    public static int 主动预算剩余值 => 主动预算剩余();

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
        Dialogue.显示临时标题(久坐语句());
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
        var 句 = Soul.PhraseTable.取("久坐");     // 话语表优先（config/phrases.json）
        if (!string.IsNullOrEmpty(句)) return 句;
        var 候选 = new[]
        {
            "坐太久啦，起来伸个懒腰嘛～",
            "已经连着忙好久咯，喝口水再继续？",
            "腰要哭啦，站起来走两步吧～",
        };
        return 候选[Random.Shared.Next(候选.Length)];
    }

    // ================= 本地话语（没接 Agent 时的兜底说话） =================

    /// <summary>
    /// **冒泡说话**：气泡出现时演「说话」动作（`say` 池）。由 `Dialogue.单例显示标题`（主线程唯一出口）调用。
    /// <para>
    /// 三条约定（主人 2026-09-19）：① 动作**可被打断** —— `锁定=false`，任何交互都立刻接管；
    /// ② **气泡时长与动画无关**：固定 `config/behavior.json` 的 `气泡显示秒`（默认 4s），到点回 idle；
    /// ③ **只在闲下来时演**（白名单：`idle` / `bubble_talk`，比黑名单安全 —— 新加的忙态默认不抢）。
    /// 特别注意**问候 / 摸摸是「先冒泡、后置状态」**，而气泡要等下一帧才演 —— 黑名单写法会把它们的
    /// 姿态偷换成说话（实测踩过）。
    /// </para>
    /// </summary>
    /// <summary>是否有排队等待中的交互反应（摸摸等当前动画播完）。</summary>
    public static bool 排队中 => _排队状态 != null;

    public static void 冒泡说话()
    {
        if (Instance == null || !设置.启用) return;
        if (入场未完成_只读) return;   // 入场期间不抢表现（气泡照显；入场动画由 CharAnim 独占——首启提示实测会顶掉状态）
        if (排队中) return;   // 有排队中的交互反应 → 说话动作让位（气泡照显，但别用 SetState 把排队项冲掉——实测踩过）
        if (CurrentState != Idle && CurrentState != BubbleTalk) return;
        SetState(BubbleTalk, Math.Max(0.5f, UX.Dialogue.气泡显示秒));
    }

    private static readonly Dictionary<string, float> _本地话语冷却 = new();

    /// <summary>
    /// 本地模式说话：从话语表取一句冒泡（**同一分类有冷却**，避免连点刷屏）。
    /// 只在**没接 Agent** 时用——接了 Agent，日常反应归 Agent 自己（分工见 `config/phrases.json` 注释）。
    /// </summary>
    public static void 本地说话(string 分类, float 冷却秒)
    {
        if (AgentBridge.IsRunning) return;
        if (_本地话语冷却.TryGetValue(分类, out var 余) && 余 > 0f) return;
        var 句 = Soul.PhraseTable.取(分类);
        if (string.IsNullOrEmpty(句)) return;
        _本地话语冷却[分类] = 冷却秒;
        GD.Print($"[StateMachine] 本地话语（{分类}）：{句}");
        Dialogue.显示临时标题(句);
    }

    private static void 推进本地话语冷却(float delta)
    {
        if (_本地话语冷却.Count == 0) return;
        foreach (var 键 in _本地话语冷却.Keys.ToArray())
        {
            var 余 = _本地话语冷却[键] - delta;
            if (余 > 0f) _本地话语冷却[键] = 余;
            else _本地话语冷却.Remove(键);
        }
    }

    // 探针专用
    public static void 探针_心跳一次() => 心跳();
    public static float 探针_活跃累计秒 => _活跃累计秒;
    public static int 探针_久坐提醒次数 => _久坐提醒次数;
    public static void 探针_清久坐冷却() => _久坐冷却剩余 = 0f;
    public static void 探针_重置久坐() { _活跃累计秒 = 0f; _久坐冷却剩余 = 0f; _久坐提醒次数 = 0; _久坐升级已写 = false; _上次主人不在 = false; }

    /// <summary>探针用（重构#3）：爬坡计数器读写——验证「互动/走动后清零、闲置累计」。</summary>
    public static float 探针_爬坡待机秒 => _爬坡待机秒;
    public static void 探针_设爬坡待机秒(float 值) => _爬坡待机秒 = 值;

    /// <summary>探针用（重构#5）：触摸序列的当前段序与续命标记——验证 B 段续命（SetContinue 语义）。</summary>
    public static int 探针_序列序 => _序列序;
    public static bool 探针_序列续命 => _序列续命;

    private static int _移动次数; // 累计自主移动次数（观测用；旧名「走动次数」）

    // 重构#3：旧的「随机间隔()」（走动间隔最小/最大秒 均匀倒计时）已删——后续走动由概率爬坡骰子调度（见 心跳()）。

    /// <summary>
    /// 按**三档状态**（手动档位，mod 可改）挑动画变体（`think-happy` / `think-poor` …）。
    /// 返回 "" = 用池内随机（该池没有这个变体）。
    /// <para>**数值（mood）不参与择档**（2026-09-20 主人定：「心情值只影响回复策略，不加入互动」；
    /// 原先按心情自动择档的耦合已移除）。</para>
    /// <para>**白名单 = 有档位素材的池**。`greet` 2026-09-24 入列（动画组A：整池换 `IDEL/Meow` 9 变体三档，
    /// 命名 `{档}-{n}` 对齐 idle 口径——主人口径见 document/VPet动画系统分析.md §9.1「Meow：手敲屏幕，
    /// 非常适合做问候语动画」）；`enter`/`exit` 同日入列（动画组G：StartUP/Shutdown 除 Ill 全档整池重导）。
    /// 不入列 = 整池随机串档，违背「默认普通」。</para>
    /// </summary>
    private static string 情绪变体(string 池)
    {
        if (池 is not ("think" or "say" or "sleep" or "interact" or "walk" or "work" or "idle" or "sit" or "lie" or "greet" or "enter" or "exit")) return "";
        // P10 三档状态（开心 / 普通 / 不良）：**开关打开时手动档位生效** —— 默认关（= 一直按「普通」演）。
        // 重构#6：三档关闭 / 档位=普通 → 返回 "nomal"（而非旧的空串）——真正落实主人「默认普通」口径：
        // 钉普通档演，不再整池随机串到 happy/poor 变体；精确档缺失由 挑主名 的降级链兜（相邻档 → 随机）。
        if (!设置.三档状态启用) return "nomal";
        return 设置.状态档位 switch { "开心" => "happy", "不良" => "poor", _ => "nomal" };
    }

    /// <summary>MoveRunner 用：当前情绪档（happy/nomal/poor；三档关闭时恒为 nomal）。</summary>
    public static string 当前情绪档 => 情绪变体("walk");

    /// <summary>坐卧会话用（重构#9）：按当前情绪档挑「{池}-{档}」主名前缀——走与 挑主名 同一条降级链；
    /// 无档基名（{池}-a）兜底；都没有返回 null。CharAnim 拿它拼 A/B/C 段名（sit-nomal-a 等）。</summary>
    public static string 档名(string 池)
    {
        var 变体 = 情绪变体(池);
        if (变体.Length > 0)
            foreach (var 档 in 降级链(变体))
            {
                var 名 = $"{池}-{档}";
                if (CharAnim.有动画(名 + "-a")) return 名;
                if (档 == 变体 && CharAnim.有动画($"{池}-a")) return 池;   // 无档基名：普通档的实际落点，先于升档试
            }
        return CharAnim.有动画($"{池}-a") ? 池 : null;
    }

    /// <summary>档位降级链（重构#6，VPet GraphCore.FindGraphs 的 ModeType 序号相邻降级：Happy↔Nomal↔PoorCondition）。
    /// Ill 第 4 档不引入——无生病玩法、Ill 素材也未导入（仅 14/609 目录且全在 Eat/Drink/Gift/Raise 等未导入类）。</summary>
    public static string[] 降级链(string 档) => 档 switch
    {
        "happy" => new[] { "happy", "nomal" },          // VPet Happy(0)：向下兼容 = Nomal(1)
        "poor" => new[] { "poor", "nomal" },            // VPet PoorCondition(2)：向上兼容 = Nomal(1)（Ill 跳过）
        "nomal" => new[] { "nomal", "poor", "happy" },  // VPet Nomal(1)：向下 Poor(2) → 向上 Happy(0)
        _ => new[] { 档 },
    };

    // 重构#4：旧的「走链/爬边相位」实现（走段时长、走动档后缀/倍率、尝试走动、走向目标X、推进走动）已全部删除——
    // 移动改为 VPet Move 模型：MoveRunner 在「触发通过」的移动里随机轮询，圈推进与位移由它自管（见 MoveRunner.cs）。

    private static float 首次间隔() =>
        (float)GD.RandRange(设置.首次走动最小秒, 设置.首次走动最大秒);

    /// <summary>自主移动（VPet DisplayToMove：随机轮询触发通过的移动；整条路线由「兼容接力」涌现）。</summary>
    private static void 尝试移动()
    {
        if (!MoveRunner.尝试移动()) return;   // 没有可跑的移动（都不触发 / 爬边族冷却中）→ 不消耗主动预算
        记一次主动();
        _爬坡待机秒 = 0f;   // 重构#3：任何主动行为后爬坡清零（= VPet CountNomal = 0）
        _爬坡骰子秒 = 0f;
        _移动次数++;
    }

    /// <summary>坐卧长待机会话（重构#9，VPet EventTimer case 6 → DisplayIdel_StateONE）：
    /// 挑当前情绪档的 sit 素材开嵌套会话（坐 → 概率躺 → 起身）。没有素材 → 不消耗主动预算。</summary>
    private static void 尝试坐卧()
    {
        if (!CharAnim.开始坐卧会话()) return;
        记一次主动();
        _爬坡待机秒 = 0f;   // 主动行为后爬坡清零（= VPet CountNomal = 0）
        _爬坡骰子秒 = 0f;
    }

    private static void 推进保持与兜底(float delta)
    {
        if (_保持剩余 > 0f)
        {
            _保持剩余 -= delta;
            if (_保持剩余 <= 0f)
            {
                _保持剩余 = 0f;
                if (_效果表.TryGetValue(CurrentState, out var 效果) && !效果.持续) SetState(效果.回落 ?? Idle);
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

    /// <summary>入队一条行为链并开始执行。示例：思考→说话→收尾 = [think(2s), speak(6s), idle(0)]。</summary>
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
        SetState(Idle);
    }

    private static void ExecuteNextStep()
    {
        if (_chainQueue.Count == 0)
        {
            _chainRunning = false;
            _activeCallback = null;
            SetState(Idle);
            StateChanged?.Invoke("chain_done");
            return;
        }
        var step = _chainQueue[0];
        _chainQueue.RemoveAt(0);
        // 链节状态直接落到 CurrentState（不走 SetState，避免清空链）
        CurrentState = step.State;
        接管中 = false;
        _包裹主名 = null;   // 链节绕过 SetState：旧包裹会话不能残留（否则重播会错播上一条会话的主段）
        _进入段中 = false;
        _退出段中 = false;
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

    /// <summary>设定保持/兜底计时（SetState 与「同状态重入」共用）。</summary>
    private static void 设定计时(状态效果 效果, float 秒)
    {
        if (效果.持续)
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
    }

    /// <summary>有 A/C 过渡段的池（包裹段机制）：挑主名从这里挑随机时要把段本身排除掉（别把 sleep-a 当主段）。</summary>
    private static readonly string[] 包裹池 = { "think", "say", "sleep", "music", "work" };

    /// <summary>是不是 A/C 过渡段变体（`-a` / `-c` 结尾）。</summary>
    private static bool 是段名(string 名)
        => 名.EndsWith("-a", StringComparison.Ordinal) || 名.EndsWith("-c", StringComparison.Ordinal);

    /// <summary>按三档/组变体规则从池里挑一条主段动画名（不播放）；池不存在或为空返回 null。与包裹段共用同一套挑法。
    /// 重构#6：择档走 VPet 式降级链（精确档 → 无档基名 → 相邻档），不再「精确档缺失就整池随机」。</summary>
    public static string 挑主名(string 池)
    {
        try
        {
            var 列表 = Main.显示人物?.动画池字典.GetValueOrDefault(池);
            if (列表 is not { Count: > 0 }) return null;
            // 段变体（-a/-c）不进普通随机：包裹池必然有；fidget/walk 等拆段池同理（按「池里存在段」判定，
            // 比写死池名更稳——新池拆段后自动生效）。music 的 `single-*` 是「嗨档」专用（MusicSense 显式点播）。
            bool 排除(string n) => (列表.Exists(x => 是段名(x.name)) && 是段名(n))
                                   || (池 == "music" && n.Contains("-single-", StringComparison.Ordinal));
            var 候选 = 列表.FindAll(x => !排除(x.name));
            if (候选.Count == 0) 候选 = 列表;

            var 变体 = 情绪变体(池);
            if (变体.Length > 0)
            {
                foreach (var 档 in 降级链(变体))
                {
                    var 精确 = $"{池}-{档}";
                    // 单条精确（think-nomal）或组变体（idle-nomal-1/2/3）一并收
                    var 命中 = 候选.FindAll(x => x.name == 精确
                        || x.name.StartsWith($"{精确}-", StringComparison.Ordinal));
                    if (命中.Count > 0) return (挑同档变体(池, 命中) ?? 命中.列表随机项()).name;
                    // 无档基名（sleep-loop / say-shining：从 Nomal 素材导入、名字里不带档位）——
                    // 它是「普通档」的实际落点，必须先于升到别的档位试（否则 sleep 会被迫演 sleep-happy）。
                    if (档 == 变体)
                    {
                        var 基 = 候选.FindAll(x => 无档名(x.name, 池));
                        if (基.Count > 0) return 基.列表随机项().name;
                    }
                }
            }
            return 候选.列表随机项().name;
        }
        catch (Exception)
        {
            return null;   // 人物数据未就绪（同 选择池 的惯例）：交给应用表现的兜底
        }
    }

    /// <summary>同档内变体选择（动画组D / Plan #24，2026-09-24）：**档的选择不动**——仍由上面的降级链决定
    /// （三档关 = 命中组恒为 nomal，零串档、GradeProbe 口径不破）；只在**命中组内部**按 `设置.idle权重`
    /// 加权随机，不再均匀概览。主人口径：「idle 变体不要均匀随机——要有主次/权重（比如 nomal 晃头为主，
    /// 其他低频）」（document/VPet动画系统分析.md §9 Default 行；2026-09-24 确认原文）。目前只作用于
    /// `idle` 池（其他池保持原样均匀随机——将来别的池要做主次，在这里加池名即可）。</summary>
    private static 动画信息 挑同档变体(string 池, List<动画信息> 组)
    {
        if (组 is not { Count: > 0 }) return null;
        if (池 != "idle" || 组.Count == 1) return 组.列表随机项();
        var 总权重 = 0;
        foreach (var x in 组) 总权重 += Math.Max(0, 设置.idle权重值(x.name));
        if (总权重 <= 0) return 组.列表随机项();   // 全 0 / 表坏 = 防呆回退均匀（惯例同 ModModels 招呼加权随机）
        var 骰 = Random.Shared.Next(总权重);
        foreach (var x in 组)
        {
            var 权重 = Math.Max(0, 设置.idle权重值(x.name));
            if (骰 < 权重) return x;
            骰 -= 权重;
        }
        return 组.列表随机项();   // 理论上到不了（浮点/空表边界兜底）
    }

    /// <summary>名字里不带任何档位标记（happy/nomal/poor）= 「无档基名」。
    /// 我们的导入约定：VPet 的 Nomal 档素材多数落成无档名（sleep-loop / say-shining / interact-a），
    /// 只有部分池显式写了 `-nomal`（think/idle/music）。（2026-09-24 Plan #22：walk 的 fast/slow 变体已删，标记表不再含它们。）</summary>
    private static bool 无档名(string 名, string 池)
    {
        var 余 = 名.StartsWith($"{池}-", StringComparison.Ordinal) ? 名[(池.Length + 1)..] : 名;
        foreach (var 档 in new[] { "happy", "nomal", "poor" })
        {
            if (余 == 档 || 余.StartsWith($"{档}-", StringComparison.Ordinal) || 余.EndsWith($"-{档}", StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>包裹段解析：① 精确 `{主名}-{段}`（think-nomal-a / work-happy-calligraphy-c）；
    /// ② 同类无档 `{池}-{去掉档位前缀}-{段}`（work-happy-study2-c → work-study2-c；VPet 每段动画各自找档，
    /// Happy 源缺 C 时退 Nomal 素材——重构#7）；③ 池级 `{池}-{段}`（sleep-a）；
    /// ④ 同感情前缀递减（动画组E）：主名带描述后缀时退到感情级段名（say-self-smile → say-self-a、
    /// say-shining-calm → say-shining-c——同感情的多个 B 变体共用同一份 A/C 段）；都没有返回 null。</summary>
    private static string 段名(string 主名, string 段)
    {
        if (string.IsNullOrEmpty(主名)) return null;
        var 变体段 = $"{主名}-{段}";
        if (CharAnim.有动画(变体段)) return 变体段;
        var i = 主名.IndexOf('-');
        if (i < 0) return null;
        var 池 = 主名[..i];
        foreach (var 档 in new[] { "happy", "poor" })
        {
            var 档前缀 = $"{池}-{档}-";
            if (!主名.StartsWith(档前缀, StringComparison.Ordinal)) continue;
            var 无档 = $"{池}-{主名[档前缀.Length..]}";
            if (CharAnim.有动画($"{无档}-{段}")) return $"{无档}-{段}";
            break;
        }
        var 池段 = $"{池}-{段}";
        if (CharAnim.有动画(池段)) return 池段;
        // ④ 同感情前缀递减：逐级剥掉尾部 `-组件` 再试（剥到「池名 + 1 段」为止——池级段 ③ 已试过）。
        // 目前实际生效的是 say 的感情级段（say-self-smile / say-shining-calm）；其他池的多段名
        // （work-happy-* 之类）在 ② 就命中，不受影响。
        var 前缀 = 主名;
        while (true)
        {
            var 断 = 前缀.LastIndexOf('-');
            if (断 <= 池.Length) break;
            前缀 = 前缀[..断];
            if (CharAnim.有动画($"{前缀}-{段}")) return $"{前缀}-{段}";
        }
        return null;
    }

    /// <summary>探针：段名解析（GradeProbe 校验档位段降级链）。</summary>
    public static string 探针_段名(string 主名, string 段) => 段名(主名, 段);

    private static void 应用表现(string state)
    {
        // 智能移动（重构#4）：表现由 MoveRunner 自管（VPet Move 模型——A 起步 → 吸附 → B 循环 → 接力/收势）
        if (state == MoveState) { MoveRunner.应用表现(); return; }
        if (!_效果表.TryGetValue(state, out var 效果)) 效果 = _效果表[Idle];
        // 贴边隐藏：表现由 EdgeHide 按阶段精确播（不走池内随机）
        if (state == EdgeHideState) { EdgeHide.应用表现(); return; }
        // 捏脸：同理（三段由 FacePinch 自管）
        if (state == PinchState) { FacePinch.应用表现(); return; }
        // 固定动画（一个池服务多个状态时用，如 switch-up / switch-down）
        if (!string.IsNullOrEmpty(效果.具体动画) && CharAnim.有动画(效果.具体动画)) { CharAnim.PlayNamed(效果.具体动画); return; }
        var 池 = 选择池(效果);
        // 情绪表达：心情好/糟时优先用该池的对应变体（如 think-happy / think-poor、摸头用 interact-happy）。
        // 该池没有这个变体就退回池内随机 —— 不硬造。（挑主名 与包裹段共用同一套挑法）
        var 主名 = 挑主名(池);
        if (主名 != null) { CharAnim.PlayNamed(主名); return; }
        CharAnim.PlayState(池);
    }

    // ================= 节律配置 =================

    /// <summary>节律参数（config/behavior.json；缺失/损坏用内置默认值）。</summary>
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
        // ── 重构#3（2026-09-22）走动概率爬坡（VPet MainLogic EventTimer 同款机制，数值按我们观感重定标）──
        // 每「爬坡秒」掷一次 Next(max(爬坡下限, 爬坡周期 - 连续待机秒))，命中前「爬坡移动槽」个值 → 走动。
        // VPet 原值：15s 一掷 / 周期 200（intercycle 默认）/ 下限 20 / 移动占 3 槽——我们照抄这套默认。
        // 模拟：中位 ~210s、P75 ~270s（与旧固定 120~300s 均匀倒计时量级一致，但闲置越久越走得勤、互动后重新爬坡）。
        public static float 爬坡秒 = 15f;
        public static int 爬坡周期 = 200;
        public static int 爬坡下限 = 20;
        public static int 爬坡移动槽 = 3;
        // —— 组② 坐卧长待机会话（重构#9：抄 VPet StateONE/StateTWO 嵌套待机场；素材池 sit/lie）——
        public static bool 坐卧启用 = true;
        public static int 坐卧槽 = 2;       // 爬坡骰子里坐卧占的槽数（VPet 1 槽/200 面；我们调高一点，主人能常看到它坐下）
        public static int 坐卧循环L = 2;    // B 循环骰子阈值（VPet `state#10`；机制照抄、数值按观感重定标）
        public static int 躺下基数 = 2;     // 进 lie 的概率 = 1/(躺下基数 + 已躺次数)（VPet `Rnd.Next(2 + CountNomal)`）
        public static int 每小时主动上限 = 8;
        public static float 持续态兜底秒 = 120f;
        public static float 排队兜底秒 = 3f;
        /// <summary>重构#2：fidget 待机小动作 B 循环的骰子阈值 L（VPet DisplayBLoopingToNomal 的 loopLength）。
        /// 每播完第 n 圈掷 Rnd.Next(n) > L 决定退场（首圈恒不过线）：L=1 平均 ~4.2 圈、L=2 平均 ~5.6 圈、L=5 平均 ~9.5 圈。
        /// VPet lps 的 duration 表是 10~20（平均 15~27 圈 ≈ 分钟级蹲坐）——我们的 fidget 是几十秒一冒的小动作，
        /// 按观感重定标为 2（一次会话 ≈ 8~16s），**不抄数值只抄机制**。
        /// 个别变体可单独调长/调短（如 squat）→ 见「fidget循环L覆盖」。</summary>
        public static int fidget循环L = 2;

        // —— 动画组B（2026-09-24）fidget 循环节奏修正（主人口径见 document/VPet动画系统分析.md §9/§9.1）——
        /// <summary>B 循环 L 的「每变体覆盖表」（变体短名 → L；缺省空表 = 全部用全局 fidget循环L）。
        /// 主人口径（§9.1 Squat 行）：「蹲下看着主人，B 段循环可以做久一点，增加萌点」——squat 单独调长。
        /// 机制不变（VPet DisplayBLoopingToNomal 概率递减退出），只换每变体的 L 取值。</summary>
        public static Dictionary<string, int> fidget循环L覆盖 = new();

        /// <summary>单段变体的「循环次数表」（变体短名 → [最小, 最大]；缺省无条目 = [1,1] 一次过，向后兼容）。
        /// 主人口径（§9.1 amusement_B 行）：「一组循环动画：退出帧是第一帧不是最后一帧，每次可以循环 2-5 次」——
        /// 每次触发在区间里随机取次数 N，播完直接重播同名（末帧≈首帧，接得无缝），N 遍后回 idle。
        /// 其余单段变体（spin/bubble/doze/happy520/meowlook/yawning）无条目 → 仍一次过。</summary>
        public static Dictionary<string, int[]> fidget单段循环 = new();

        /// <summary>取某变体的有效 B 循环 L：覆盖表优先，缺省全局 fidget循环L（动画组B：squat 调长用）。</summary>
        public static int fidgetL(string 变体) => fidget循环L覆盖.TryGetValue(变体, out var l) ? l : fidget循环L;

        /// <summary>取单段变体的循环次数区间（缺省 [1,1] = 一次过）。</summary>
        public static (int 最小, int 最大) fidget单段区间(string 变体)
            => fidget单段循环.TryGetValue(变体, out var v) && v is { Length: 2 } ? (v[0], v[1]) : (1, 1);

        // —— 动画组D（2026-09-24）idle 变体加权（Plan #24 口径） ——
        /// <summary>idle 池「同档内变体选择」的权重表（变体名或档位 → 权重）。主人口径：「idle 变体不要
        /// 均匀随机——要有主次/权重（比如 nomal 晃头为主，其他低频）」（document/VPet动画系统分析.md
        /// §9 Default 行；2026-09-24 确认原文）+ 审视野「我们默认为普通级，切换要留」——**档的选择不动**
        /// （降级链照旧：三档关 = nomal 绝对为主、零串档），权重只在挑中的同档组内生效（见 挑同档变体）。
        /// 键三种写法都认：变体短名（nomal-1）/ 全名（idle-nomal-1）/ 档位（happy——该档里没单列的变体用它的值）。
        /// 值 clamp 0..20；0 = 不参与（整组全 0 防呆回退均匀）。出货表：nomal-1 为主（4/8 = 50%）、
        /// nomal-2/3 各 2；happy/poor 的 1 只在三档开（开心/不良）时用到。想回「均匀随机」：config 里把
        /// 表设成 {}（空表 = 全部缺省 1）；删掉整个键 = 用这里的代码内置表（同出货表）。</summary>
        public static Dictionary<string, int> idle权重 = new()
        {
            ["nomal-1"] = 4,
            ["nomal-2"] = 2,
            ["nomal-3"] = 2,
            ["happy"] = 1,
            ["poor"] = 1,
        };

        /// <summary>取某 idle 变体（全名，如 idle-nomal-1）的有效权重：全名 → 短名 → 档位键 → 缺省 1。</summary>
        public static int idle权重值(string 全名)
        {
            if (string.IsNullOrEmpty(全名)) return 1;
            if (idle权重.TryGetValue(全名, out var w)) return w;
            var 短 = 全名.StartsWith("idle-", StringComparison.Ordinal) ? 全名["idle-".Length..] : 全名;
            if (短 != 全名 && idle权重.TryGetValue(短, out var w2)) return w2;
            var 断 = 短.IndexOf('-');
            var 档 = 断 > 0 ? 短[..断] : 短;
            return idle权重.TryGetValue(档, out var w3) ? w3 : 1;
        }

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
        public static float 贴边循环间隔秒 = 2f;   // 循环动画（隐藏保持/探出）的**周期**：主人要求「每两秒循环两次贴边动画」
        public static int 贴边循环次数 = 2;        // 一个周期里循环几次（2 = 每 2 秒眨两次）
        public static float 贴边循环内间隔秒 = 0.5f;  // 循环内部间隔（同周期两次之间隔多久）
        public static int 贴边左偏移像素 = 0;      // 微调：正=往屏内多推，负=往屏外多推（左右不对称就调这俩）
        public static int 贴边右偏移像素 = 0;
        // —— P7 捏脸（照 VPet 官方：**长按脸**触发；实现见 FacePinch.cs） ——
        public static float 捏脸长按秒 = 0.3f;         // 官方 presslength 默认 300ms
        public static float[] 捏脸命中区;               // null = 用 FacePinch 默认（窗口比例 x0,y0,x1,y1）
        /// <summary>拖拽命中区（窗口比例 x0,y0,x1,y1）；null = 用 WindowDrag 默认（窗口顶部 40% = 头部）。</summary>
        public static float[] 拖拽命中区;

        // —— P10 摸身体 / 三档状态 ——
        /// <summary>摸身体命中区（窗口比例 x0,y0,x1,y1）；null = 用 WindowDrag 默认。</summary>
        public static float[] 摸身体命中区;
        /// <summary>三档状态开关（开心 / 普通 / 不良）：**默认关** = 一直按「普通」演（= 不改现有观感）。</summary>
        public static bool 三档状态启用;
        /// <summary>三档下的当前档位（自动切换的判据与表现还没定 → 先手动选；配置窗「行为」页）。</summary>
        public static string 状态档位 = "普通";
        public static float 久坐提醒分钟 = 90f;      // 连续活跃多久提醒休息（程序侧事件）；0 = 关
        public static float 久坐提醒冷却分钟 = 90f;  // 两次提醒的最小间隔

        // —— Plan #11 时间驱动主动行为（问候 / 磁盘；实现见 DailyRoutine.cs） ——
        // 取舍原则：**只提醒主人自己不容易察觉的事**（磁盘悄悄变满、久坐忘时间）；喝水/该睡了这类「主人自己知道的事」一律不做。
        public static bool 问候启用 = true;           // 当天首次见面按时间段问好
        public static bool 磁盘提醒启用 = true;       // 磁盘余量低 → 每天最多提醒一次
        public static int 磁盘剩余下限GB = 10;        // 低于这个余量算「快满了」
        /// <summary>探针用：置 true 后 加载() 强制关闭「时间驱动」开关（问候/磁盘/音乐）——
        /// 探针实例化场景前设的隔离值不再被 behavior.json 注入覆盖（否则全量回归里断言随顺序抖）。</summary>
        public static bool 探针_冻结时间驱动开关;

        // —— 组② 智能移动（重构#4：抄 VPet 原库的移动方式；**移动定义表在 config/moves.json**）——
        // 逐条移动的触发/检查/速度/距离骰都进表；这里只放全局参数。
        public static bool 移动启用 = true;
        public static float 接力概率 = 0.8f;      // 检查/距离骰不过时的「兼容接力」命中率（VPet TreeRND 40%；我们调高让路线更常一气呵成）
        public static float 移动冷却秒 = 600f;     // 落地后的「爬边族」冷却（走照常；我们保留的防重复观感，VPet 无此概念）
        public static float 挂边可见比例 = 0.52f;  // 侧挂时留在屏内的窗口宽比例
        public static float 顶挂可见比例 = 0.55f;  // 顶挂时留在屏内的窗口高比例
        public static int 爬边左偏移像素;
        public static int 爬边右偏移像素;
        public static int 顶挂偏移像素;
        public static int 脚底余量像素 = 6;
        public static float 掉落初速 = 24f;
        public static float 掉落加速度 = 160f;
        public static float 掉落终端速度 = 1400f;

        // —— 组③ 音乐反应（MusicSense.cs）：系统在放声音就跳舞，安静就收场 ——
        public static bool 音乐检测启用 = true;
        public static float 音乐音量阈值 = 0.02f;    // 峰值超过算「有声音」
        public static float 音乐刺激阈值 = 0.25f;    // 识别期平均超过算「嗨」→ 换 Single 舞
        public static float 音乐识别秒 = 3f;         // 连续有声多久才开跳
        public static float 音乐静音秒 = 6f;         // 安静多久收场

        public static void 加载()
        {
            foreach (var 路径 in Util.ConfigFile.候选("behavior.json").Concat(Util.ConfigFile.候选("config/behavior.json")))
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
                    爬坡秒 = Math.Max(1f, 取浮点(根, "爬坡秒", 爬坡秒));
                    爬坡下限 = Math.Max(1, 取整数(根, "爬坡下限", 爬坡下限));
                    爬坡移动槽 = Math.Max(1, 取整数(根, "爬坡移动槽", 爬坡移动槽));
                    爬坡周期 = Math.Max(爬坡下限 + 爬坡移动槽, 取整数(根, "爬坡周期", 爬坡周期));
                    坐卧启用 = 取布尔(根, "坐卧启用", 坐卧启用);
                    坐卧槽 = Math.Clamp(取整数(根, "坐卧槽", 坐卧槽), 0, 10);
                    坐卧循环L = Math.Clamp(取整数(根, "坐卧循环L", 坐卧循环L), 0, 20);
                    躺下基数 = Math.Clamp(取整数(根, "躺下基数", 躺下基数), 2, 10);
                    // 槽位总和不能超过窗口上限（否则尾部槽永远掷不到）
                    爬坡周期 = Math.Max(爬坡周期, 爬坡下限 + 爬坡移动槽 + (坐卧启用 ? 坐卧槽 : 0));
                    每小时主动上限 = 取整数(根, "每小时主动上限", 每小时主动上限);
                    持续态兜底秒 = 取浮点(根, "持续态兜底秒", 持续态兜底秒);
                    fidget循环L = Math.Clamp(取整数(根, "fidget循环L", fidget循环L), 0, 20);
                    fidget循环L覆盖 = 取整数表(根, "fidget循环L覆盖", fidget循环L覆盖);
                    fidget单段循环 = 取区间表(根, "fidget单段循环", fidget单段循环);
                    idle权重 = 取整数表(根, "idle权重", idle权重);   // 动画组D/#24：idle 同档内变体权重（缺键 = 内置表）
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
                    贴边循环次数 = Math.Clamp(取整数(根, "贴边循环次数", 贴边循环次数), 1, 6);
                    贴边循环内间隔秒 = 取浮点(根, "贴边循环内间隔秒", 贴边循环内间隔秒);
                    贴边左偏移像素 = 取整数(根, "贴边左偏移像素", 贴边左偏移像素);
                    贴边右偏移像素 = 取整数(根, "贴边右偏移像素", 贴边右偏移像素);
                    捏脸长按秒 = 取浮点(根, "捏脸长按秒", 捏脸长按秒);
                    捏脸命中区 = 取矩形(根, "捏脸命中区", 捏脸命中区);
                    拖拽命中区 = 取矩形(根, "拖拽命中区", 拖拽命中区);
                    摸身体命中区 = 取矩形(根, "摸身体命中区", 摸身体命中区);
                    三档状态启用 = 取布尔(根, "三档状态启用", 三档状态启用);
                    状态档位 = 取文本(根, "状态档位", 状态档位);
                    久坐提醒分钟 = 取浮点(根, "久坐提醒分钟", 久坐提醒分钟);
                    久坐提醒冷却分钟 = 取浮点(根, "久坐提醒冷却分钟", 久坐提醒冷却分钟);
                    问候启用 = 取布尔(根, "问候启用", 问候启用);
                    磁盘提醒启用 = 取布尔(根, "磁盘提醒启用", 磁盘提醒启用);
                    磁盘剩余下限GB = Math.Max(1, 取整数(根, "磁盘剩余下限GB", 磁盘剩余下限GB));
                    移动启用 = 取布尔(根, "移动启用", 移动启用);
                    接力概率 = Math.Clamp(取浮点(根, "接力概率", 接力概率), 0f, 1f);
                    移动冷却秒 = 取浮点(根, "移动冷却秒", 移动冷却秒);
                    掉落初速 = 取浮点(根, "掉落初速", 掉落初速);
                    掉落加速度 = 取浮点(根, "掉落加速度", 掉落加速度);
                    掉落终端速度 = 取浮点(根, "掉落终端速度", 掉落终端速度);
                    挂边可见比例 = 取浮点(根, "挂边可见比例", 挂边可见比例);
                    顶挂可见比例 = 取浮点(根, "顶挂可见比例", 顶挂可见比例);
                    爬边左偏移像素 = 取整数(根, "爬边左偏移像素", 爬边左偏移像素);
                    爬边右偏移像素 = 取整数(根, "爬边右偏移像素", 爬边右偏移像素);
                    顶挂偏移像素 = 取整数(根, "顶挂偏移像素", 顶挂偏移像素);
                    脚底余量像素 = 取整数(根, "脚底余量像素", 脚底余量像素);
                    音乐检测启用 = 取布尔(根, "音乐检测启用", 音乐检测启用);
                    音乐音量阈值 = 取浮点(根, "音乐音量阈值", 音乐音量阈值);
                    音乐刺激阈值 = 取浮点(根, "音乐刺激阈值", 音乐刺激阈值);
                    音乐识别秒 = 取浮点(根, "音乐识别秒", 音乐识别秒);
                    音乐静音秒 = 取浮点(根, "音乐静音秒", 音乐静音秒);
                    break;
                }
                catch (Exception e) { GD.PrintErr($"[StateMachine] 读节律配置失败 {路径}: {e.Message}"); }
            }

            if (首次走动最大秒 < 首次走动最小秒) 首次走动最大秒 = 首次走动最小秒;

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
            EdgeHide.循环次数 = Math.Clamp(贴边循环次数, 1, 6);
            EdgeHide.循环内间隔秒 = Math.Clamp(贴边循环内间隔秒, 0.1f, 10f);
            EdgeHide.左偏移像素 = Math.Clamp(贴边左偏移像素, -400, 400);
            EdgeHide.右偏移像素 = Math.Clamp(贴边右偏移像素, -400, 400);
            // 把组② 智能移动参数交给 MoveRunner（含夹取，避免配置写坏导致窗口跑到屏外回不来）
            MoveRunner.启用 = 移动启用;
            MoveRunner.接力概率 = Math.Clamp(接力概率, 0f, 1f);
            MoveRunner.冷却秒 = Math.Max(10f, 移动冷却秒);
            MoveRunner.掉落初速 = Math.Clamp(掉落初速, 50f, 2000f);
            MoveRunner.掉落加速度 = Math.Clamp(掉落加速度, 200f, 8000f);
            MoveRunner.掉落终端速度 = Math.Clamp(掉落终端速度, 100f, 3000f);
            MoveRunner.挂边可见比例 = Math.Clamp(挂边可见比例, 0.10f, 0.90f);
            MoveRunner.顶挂可见比例 = Math.Clamp(顶挂可见比例, 0.10f, 0.90f);
            MoveRunner.左偏移像素 = Math.Clamp(爬边左偏移像素, -400, 400);
            MoveRunner.右偏移像素 = Math.Clamp(爬边右偏移像素, -400, 400);
            MoveRunner.顶偏移像素 = Math.Clamp(顶挂偏移像素, -400, 400);
            MoveRunner.脚底余量像素 = Math.Clamp(脚底余量像素, -40, 80);
            // 把组③ 音乐反应参数交给 MusicSense（含夹取）
            // —— 探针隔离（2026-09-22）：探针在**实例化场景前**设的「时间驱动」开关会被上面这批配置注入
            //    （读 behavior.json）覆盖回配置文件值 → 全量回归里问候气泡/音乐反应会在探针中途抢状态，
            //    断言随探针顺序抖（实测 MoveProbe「收步回 idle」被每日问候顶掉）。置冻结开关则强制关闭。
            if (探针_冻结时间驱动开关) { 问候启用 = false; 磁盘提醒启用 = false; 音乐检测启用 = false; }
            MusicSense.启用 = 音乐检测启用;
            MusicSense.音量阈值 = Math.Clamp(音乐音量阈值, 0.001f, 0.5f);
            MusicSense.刺激阈值 = Math.Clamp(音乐刺激阈值, 0.01f, 1f);
            MusicSense.识别秒 = Math.Clamp(音乐识别秒, 0.5f, 30f);
            MusicSense.静音秒 = Math.Clamp(音乐静音秒, 1f, 120f);
            // 捏脸（P7）：长按阈值 + 命中区（照 VPet 官方；命中区是窗口宽高的比例）
            FacePinch.长按秒 = Math.Clamp(捏脸长按秒, 0.1f, 3f);
            if (捏脸命中区 is { Length: 4 }) FacePinch.命中区 = 捏脸命中区;
            // 摸身体（P10）：命中区交给 WindowDrag（它做单击判定；脸区优先）
            UX.WindowDrag.命中区 = 摸身体命中区 is { Length: 4 } ? 摸身体命中区 : null;
            // 拖拽命中区（2026-09-19）：只有头顶（窗口顶部 40%）能起手拖拽；脸区优先
            UX.WindowDrag.拖拽命中区 = 拖拽命中区 is { Length: 4 } ? 拖拽命中区 : null;

            // 把 Plan #11 的时间驱动行为参数交给 DailyRoutine（含夹取，避免配置写坏）
            DailyRoutine.问候启用 = 问候启用;
            DailyRoutine.磁盘提醒启用 = 磁盘提醒启用;
            DailyRoutine.磁盘剩余下限GB = 磁盘剩余下限GB;
        }

        /// <summary>读 [x0,y0,x1,y1] 比例数组（长度必须是 4 且都是数字，否则用兜底）。</summary>
        private static float[] 取矩形(JsonElement 根, string 键, float[] 兜底)
        {
            if (!根.TryGetProperty(键, out var 区) || 区.ValueKind != JsonValueKind.Array || 区.GetArrayLength() != 4) return 兜底;
            var 值 = new float[4];
            var 下标 = 0;
            foreach (var e in 区.EnumerateArray())
            {
                if (!e.TryGetSingle(out var f)) return 兜底;
                值[下标++] = f;
            }
            return 值;
        }

        private static bool 取布尔(JsonElement 根, string 键, bool 兜底) =>
            根.TryGetProperty(键, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : 兜底;

        private static float 取浮点(JsonElement 根, string 键, float 兜底) =>
            根.TryGetProperty(键, out var v) && v.TryGetDouble(out var d) ? (float)d : 兜底;

        private static int 取整数(JsonElement 根, string 键, int 兜底) =>
            根.TryGetProperty(键, out var v) && v.TryGetInt32(out var i) ? i : 兜底;

        private static string 取文本(JsonElement 根, string 键, string 兜底) =>
            根.TryGetProperty(键, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? 兜底 : 兜底;

        /// <summary>读「变体 → 整数」表（动画组B：fidget循环L覆盖）。逐项 clamp 0..20（与全局 fidget循环L 同域）；
        /// 键缺失/不是对象 → 用兜底；坏项（非整数）跳过不整表作废。</summary>
        private static Dictionary<string, int> 取整数表(JsonElement 根, string 键, Dictionary<string, int> 兜底)
        {
            if (!根.TryGetProperty(键, out var v) || v.ValueKind != JsonValueKind.Object) return 兜底;
            var 表 = new Dictionary<string, int>();
            foreach (var 项 in v.EnumerateObject())
                if (项.Value.TryGetInt32(out var i)) 表[项.Name] = Math.Clamp(i, 0, 20);
            return 表;
        }

        /// <summary>读「变体 → [最小, 最大]」表（动画组B：fidget单段循环）。逐值 clamp 1..20（至少播一遍）；
        /// 写反了（最小>最大）按最小算；坏项（非 2 元整数数组）跳过不整表作废。</summary>
        private static Dictionary<string, int[]> 取区间表(JsonElement 根, string 键, Dictionary<string, int[]> 兜底)
        {
            if (!根.TryGetProperty(键, out var v) || v.ValueKind != JsonValueKind.Object) return 兜底;
            var 表 = new Dictionary<string, int[]>();
            foreach (var 项 in v.EnumerateObject())
            {
                if (项.Value.ValueKind != JsonValueKind.Array || 项.Value.GetArrayLength() != 2) continue;
                var 值 = new int[2];
                var 下标 = 0;
                var 都是整数 = true;
                foreach (var e in 项.Value.EnumerateArray())
                {
                    if (!e.TryGetInt32(out var i)) { 都是整数 = false; break; }
                    值[下标++] = Math.Clamp(i, 1, 20);
                }
                if (!都是整数) continue;
                if (值[0] > 值[1]) 值[1] = 值[0];
                表[项.Name] = 值;
            }
            return 表;
        }
    }

    // ================= 供探针读取（只读） =================

    public static float 空闲秒_只读 => _空闲秒;
    public static bool 入场未完成_只读 => _入场未完成;
    public static int 移动次数_只读 => _移动次数;
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