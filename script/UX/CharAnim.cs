using System;
using System.Linq;
using System.Collections.Generic;
using desktop.script.logic;
using desktop.script.State;
using Godot;

namespace desktop.script.UX;

public partial class CharAnim : AnimatedSprite2D
{
    // 不需要定义额外的 _animatedSprite 变量，因为当前类本身就是 AnimatedSprite2D
    private static CharAnim _单例;
    private static string _退出动画名;
    private static 人物数据 显示人物 => Main.显示人物;
    private const int Idle循环下限 = 8;
    private const int Idle循环上限 = 16;
    private static int _fidget触发次数;
    private static int _idle循环次数;
    // ── 重构#2（2026-09-22）fidget 会话：A → B循环×骰子 → C → idle ──
    // 对齐 VPet MainDisplay.cs:314-320 DisplayBLoopingToNomal：每播完一圈 B，
    // 掷 Rnd.Next(++looptimes) > L 决定「播 C 退出」还是「再来一圈」。
    // 单段变体（VPet Single 型：spin/bubble/doze/…）不开会话，播完直接回 idle（DisplayToNomal 语义）。
    private static string _fidget主名;   // 当前会话主段名（如 fidget-squat）；null = 无会话/单段
    private static int _fidget圈数;      // 已播 B 圈数（= VPet looptimes）
    private static readonly Random _骰子 = new Random();   // fidget 骰子专用（与 工具库.DefaultRand 分开，互不扰流）
    private static readonly List<string> 内置动画组 =
        ["idle", "celerate", "drag", "dragup", "dragdown", "fidget",
         // 语义池（P2 导入资产后即自动生效；池目录不存在时加载管线自动跳过，无副作用）
         "think", "say", "work", "listen", "sleep", "walk", "greet", "interact", "move",
         // 贴边隐藏（素材已导入；行为接线见 script/State/README.md）
         "edge_hide",
         // 捏脸（照 VPet 官方：长按脸触发；素材 Pinch/*；表现由 FacePinch 三段自管）
         "pinch",
         "interact_body", "turn", "switch",
         // 生日彩蛋（2026-09-20 组①：VPet BDay 三段；触发在 Main）
         "bday",
         // 智能移动（2026-09-20 组② → 2026-09-22 重构#4：VPet MOVE/*；表现由 MoveRunner 按移动表自管，走=walk 池 / 趴行=crawl（慢速变体））
         "climb", "climb_top", "crawl", "fall",
         // 起跳（2026-09-20 主人点名：素材未到、逻辑先接）——GamePlayer.上升期播 `jump-left/right`，
         // 缺素材自动回退（`jump` → 起跳前姿态）；素材导入后随池自动生效
         "jump",
         // 坐卧长待机会话（重构#9：VPet StateONE/StateTWO 嵌套待机场；表现由 CharAnim 会话自管）
         "sit", "lie",
         // 音乐反应（2026-09-20 组③：VPet Music；包裹段 + MusicSense）
         "music"];

    /// <summary>以「循环模式」加载的池：走动 6 帧（0.75s）而一次位移约 1s；睡觉是持续态，循环比「播完重播」更顺滑。
    /// fall 同理：`-b`（横着下落）在智能移动与游戏模式的空中段都当持续姿态用（`-a`/`-c` 段仍被排除，见加载处）。</summary>
    private static readonly List<string> 循环动画组 = ["walk", "sleep", "fall"];
    public override void _Ready()
    {
        // 直接给自己的 AnimationFinished 信号绑定方法
        AnimationFinished += OnAnimationFinished;
        _单例 = this;
        _fidget触发次数 = Random.Shared.Next(Idle循环下限, Idle循环上限 + 1);
        应用外观配置();
    }

    /// <summary>从 config/pet.json 应用桌宠默认大小（窗口尺寸随后由 PetWindow 套住角色）。
    /// **大小只在启动时定**：滚轮缩放已删除（主人决策：缩放会破坏动画链 —— 素材偏移/贴边比例都是按固定缩放导入调好的）。</summary>
    private static void 应用外观配置()
    {
        _缩放 = 0.5f;   // **对齐官方内部 ZoomRatio = 0.5**（官方设置界面显示 1.0）；后续动画都按这个显示标准做
        foreach (var 路径 in Util.ConfigFile.候选("pet.json").Concat(Util.ConfigFile.候选("config/pet.json")))
        {
            try
            {
                if (!System.IO.File.Exists(路径)) continue;
                var txt = System.IO.File.ReadAllText(路径);
                if (string.IsNullOrWhiteSpace(txt)) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(txt);
                if (doc.RootElement.TryGetProperty("缩放", out var s) && s.TryGetSingle(out var sv)) _缩放 = sv;
                break;
            }
            catch (Exception e) { GD.PrintErr($"[CharAnim] 读外观配置失败: {e.Message}"); }
        }
        _缩放 = Math.Clamp(_缩放, 0.25f, 1.5f);
        if (_单例 != null) _单例.Scale = new Vector2(_缩放, _缩放);
        GD.Print($"[CharAnim] 外观: 缩放={_缩放}");
    }

    private static float _缩放 = 0.43f;
    // 注：滚轮调缩放（调整缩放 API）已按主人决策**删除** —— 运行时缩放会破坏动画链（导入时按固定缩放对齐的偏移/贴边比例全部失效）。

    /// <summary>按当前缩放与素材尺寸，把窗口收缩到正好套住角色。</summary>
    private static void 初始化窗口尺寸()
    {
        if (_单例?.SpriteFrames == null) return;
        var 尺寸 = _单例.SpriteFrames.GetFrameTexture(_单例.Animation, 0)?.GetSize()
                   ?? new Vector2(512, 512);
        var s = Mathf.RoundToInt(Math.Max(尺寸.X, 尺寸.Y) * _缩放);
        PetWindow.初始化(s);
        GD.Print($"[CharAnim] 窗口尺寸: 角色 {s}px");
    }

    /// <summary>供 PetWindow 摆放角色位置。</summary>
    public static void 设置位置(Vector2 位置)
    {
        if (_单例 != null) _单例.Position = 位置;
    }
    private void OnAnimationFinished()
    {
        // 游戏模式：办公播完逻辑全关（动画由游戏侧接管；见 script/Game/GamePlayer）
        if (Mode.ModeManager.CurrentMode == Mode.ModeManager.Mode.Game) return;

        // 状态机接管中（think/speak/working/listen/sleep 等持续态）：不回 idle，
        // 由状态机决定是否重播当前状态。这是新旧两套状态逻辑的唯一交汇点（见 AIPet-Agent.md §3）。
        // 「包裹中」：非锁定态的包裹会话（气泡说话）段推进也归状态机管（见 StateMachine 包裹段）。
        // 例外：退出动画必须放行，否则 case "exit" 永不触发、程序关不掉。
        if ((StateMachine.接管中 || StateMachine.包裹中) && Animation.ToString() != _退出动画名)
        {
            StateMachine.重播当前状态();
            return;
        }

        // 排队的交互反应（如摸摸）在此生效：等这次动画播完再切，避免观感割裂。
        // 必须放在下面 switch 之前，否则会先被 CharAnim 自己的逻辑切回 idle，产生闪跳。
        if (StateMachine.尝试应用排队状态()) return;

        var 人物数据 = 显示人物;
        var 动画数据 = 人物数据.动画信息映射[Animation];
        var 动画类型 = 动画数据.Type;
        switch (动画类型)
        {
            case "enter":
                SpriteFrames.RemoveAnimation(动画数据.name);//入场动画无用了
                _idle循环次数 = 0;
                进入状态("idle");
                StateMachine.入场完成(); // 入场门解除 → 打一次启动招呼
                break;
            case "drag":
            case "dragup":
                进入状态("drag");
                break;
            case "celerate":
            case "dragdown":
                _idle循环次数 = 0;
                进入状态("idle");
                break;
            case "fidget":
                // 重构#2：三段会话 A → B循环×骰子 → C → idle（VPet DisplayBLoopingToNomal 同款）。
                // 单段变体（无会话，_fidget主名 = null）走 else 分支一次过回 idle（Single 型语义）。
                if (_fidget主名 != null)
                {
                    var 当前 = Animation.ToString();
                    if (当前 == _fidget主名 + "-a")
                    {
                        // A 播完 → B 第一圈（首掷 Next(1)=0 恒不过线，见下）
                        Play(_fidget主名);
                    }
                    else if (当前 == _fidget主名)
                    {
                        // 每播完一圈 B 掷一次骰子（VPet 原样：Rnd.Next(++looptimes) > L）：
                        // 第 n 圈掷 Next(n)——首圈 Next(1)=0 恒不过线（保证至少播一圈），
                        // 圈数越多退出概率越高。命中 → C 退场。
                        _fidget圈数++;
                        if (_骰子.Next(_fidget圈数) > StateMachine.设置.fidget循环L && 有动画(_fidget主名 + "-c"))
                            Play(_fidget主名 + "-c");
                        else
                            Play(_fidget主名);
                    }
                    else
                    {
                        结束fidget会话();   // C 播完 → 落地回 idle
                    }
                    break;
                }
                _idle循环次数 = 0;
                进入状态("idle");
                break;
            case "sit":
            case "lie":
                // 重构#9：坐卧嵌套会话（A → B 循环×骰子 → 概率躺下 → 回坐 → C → idle）
                推进坐卧会话(Animation.ToString());
                break;
            case "idle":
                _idle循环次数++;
                if (_idle循环次数>=_fidget触发次数)
                {
                    _fidget触发次数 = Random.Shared.Next(Idle循环下限, Idle循环上限 + 1);
                    进入状态("fidget");
                }
                else
                {
                    进入状态("idle");
                }
                break;
            case "exit":
                Main.游戏结束();
                GetTree().Quit();
                break;
        }
    }
    public static void 播放退出动画()
    {
        if (_单例 == null) return;
        StateMachine.准备退出(); // 解除状态锁，否则退出动画的播完回调被接管逻辑吞掉 → 关不掉
        _单例.Play(_退出动画名);
    }

    /// <summary>该池是否已登记为可播放（见 内置动画组）。未登记的池不会被预载，播放会失败。</summary>
    public static bool 池已注册(string id) => 内置动画组.Contains(id);

    /// <summary>该动画名是否已载入（可播放）。</summary>
    public static bool 有动画(string 动画名) =>
        !string.IsNullOrEmpty(动画名) && _单例?.SpriteFrames?.HasAnimation(动画名) == true;

    /// <summary>动画时长（秒）= Σ每帧相对时长 ÷ 帧率；未载入返回 0（状态机走链用它对齐起步/停步阶段时长）。
    /// 2026-09-22：按逐帧 duration 求和（有定格帧的动画时长不再被低估）。</summary>
    public static float 动画时长(string 动画名)
    {
        var sf = _单例?.SpriteFrames;
        if (sf == null || !sf.HasAnimation(动画名)) return 0f;
        var 帧率 = Math.Max(1.0, sf.GetAnimationSpeed(动画名));
        double 总时长 = 0;
        for (var i = 0; i < sf.GetFrameCount(动画名); i++) 总时长 += sf.GetFrameDuration(动画名, i);
        return (float)(总时长 / 帧率);
    }

    /// <summary>当前正在播的动画名（只读，供状态机避免重复重播导致相位重置）。</summary>
    public static string 当前动画名_只读 => _单例?.Animation.ToString() ?? "";

    /// <summary>探针用：查询某动画是否按循环模式加载（包裹段 A/C 必须非循环——循环动画不回「播完」信号）。</summary>
    public static bool 动画循环_只读(string 名)
        => _单例 != null && _单例.SpriteFrames.HasAnimation(名) && _单例.SpriteFrames.GetAnimationLoop(名);

    /// <summary>探针用：当前 fidget 会话主段名（null = 无会话/单段一次过）与已播 B 圈数——验证骰子循环推进。</summary>
    public static string fidget会话_只读 => _fidget主名;
    public static int fidget圈数_只读 => _fidget圈数;

    /// <summary>探针用：模拟「当前动画播完」信号（驱动 fidget 会话/状态机段推进，不靠实时等待）。</summary>
    public static void 探针_模拟播完() => _单例?.OnAnimationFinished();

    /// <summary>探针用：以指定主段开 fidget 会话（绕过池内随机——FidgetProbe 要钉死 squat 验证段推进）。</summary>
    public static void 探针_fidget会话(string 主段名) { if (_单例 != null) 开始fidget会话(主段名); }

    /// <summary>探针用：某动画的帧数（未载入返回 0）——BufferProbe 验证「切换风暴下当前动画始终立即可渲染」。</summary>
    public static int 帧数_只读(string 名)
        => _单例 != null && _单例.SpriteFrames.HasAnimation(名) ? _单例.SpriteFrames.GetFrameCount(名) : 0;

    /// <summary>探针用：某动画第 i 帧的相对时长（单位 1/帧率 秒；未载入/越界返回 -1）。验证 durations 逐帧时长生效。</summary>
    public static float 帧时长_只读(string 名, int i)
        => _单例 != null && _单例.SpriteFrames.HasAnimation(名) && i < _单例.SpriteFrames.GetFrameCount(名)
            ? _单例.SpriteFrames.GetFrameDuration(名, i) : -1f;

    /// <summary>按动画名精确播放（区别于 PlayState 的「按池随机取一项」）。可跨线程调用。</summary>
    public static void PlayNamed(string 动画名)
    {
        if (_单例 == null || string.IsNullOrEmpty(动画名)) return;
        _单例.CallDeferred(nameof(单例播放指定动画), 动画名);
    }

    private void 单例播放指定动画(string 动画名)
    {
        if (SpriteFrames?.HasAnimation(动画名) == true) Play(动画名);
        else GD.PrintErr($"[CharAnim] 动画不存在: {动画名}");
    }
    public static void 开始庆祝() => 进入状态("celerate");
    public static void 开始拖拽()=>进入状态("dragup");
    public static void 结束拖拽()=>进入状态("dragdown");

    /// <summary>按状态名播放动画（身体层状态机统一入口）。未知状态自动回退 idle。可跨线程调用。</summary>
    public static void PlayState(string state)
    {
        if (_单例 == null) return;
        _单例.CallDeferred(nameof(单例播放状态), state);
    }

    private void 单例播放状态(string state) => 进入状态(state);

    private static void 进入状态(string id)
    {
        // 换到别的状态：作废未完成的 fidget 会话（拖拽/气泡等硬切时不留脏状态）
        if (id != "fidget") _fidget主名 = null;
        // 重构#9：坐卧会话同理——任何「按池进入」的状态都代表别人接管了表现（sit/lie 段是直接 Play 的，
        // 不会走到这里；会话本身只在 结束坐卧会话 里回 idle）
        _坐卧主名 = null; _坐卧场 = null; _坐卧圈数 = 0; _坐卧次数 = 0;
        if (!显示人物.动画池字典.TryGetValue(id, out var list) || list.Count == 0)
        {
            if (id != "idle") 进入状态("idle");   // ReSharper disable once TailRecursiveCall
            return;
        }
        // 重构#6：择档统一走 StateMachine.挑主名（VPet 式降级链 + 段排除）——
        // idle 不再整池随机串到 happy/poor（默认钉 nomal，落实「默认普通」口径）；
        // fidget 自动排除 -a/-c 过渡段。挑主名 返回 null（数据未就绪）时退回旧的整池随机。
        var 主名 = StateMachine.挑主名(id) ?? list.列表随机项()?.name;
        if (string.IsNullOrEmpty(主名)) { if (id != "idle") 进入状态("idle"); return; }
        // 重构#2：fidget 主段带 -a = 三段结构 → 开会话（A 进场 → B 循环掷骰 → C 退场），否则单段一次过。
        if (id == "fidget" && 有动画(主名 + "-a")) { 开始fidget会话(主名); return; }
        _单例.Play(主名);
    }

    /// <summary>开 fidget 会话：钉死主段、圈数清零、先播 A 进场段（播完由 OnAnimationFinished 接 B 循环）。</summary>
    private static void 开始fidget会话(string 主名)
    {
        _fidget主名 = 主名;
        _fidget圈数 = 0;
        _单例.Play(主名 + "-a");
    }

    /// <summary>fidget 会话收尾：清会话、重置 idle 计数、回 idle（C 播完 / 无 C 段时骰子命中 直接落地）。</summary>
    private static void 结束fidget会话()
    {
        _fidget主名 = null;
        _idle循环次数 = 0;
        进入状态("idle");
    }

    // ── 重构#9（2026-09-22）坐卧嵌套会话（VPet StateONE/StateTWO「嵌套待机场」；抄原库嵌套逻辑）──
    // 结构：sit.A → sit.B（每圈随机换 B 变体，每圈掷骰 Rnd.Next(++n) > L）→ 命中后
    //       1/(躺下基数 + 已躺次数) 进 lie（A → 同款 B 循环 → C 起身 → **回 sit 的 B 循环判定**）
    //       → … → sit.C → idle。整条「坐下→（概率）躺下→回坐→起身」是两场嵌套出来的。
    // 照抄点（MainDisplay.cs:207-266）：`looptimes` 三处清零（进 sit / 进 lie / 退 lie）；
    // 「已躺次数」= VPet CountNomal（进 sit 清零、进 lie +1 —— 压「反复躺下」概率）。
    private static string _坐卧主名;      // 当前场主名前缀（sit-nomal / lie-happy）；null = 无会话
    private static string _坐卧场;        // "sit" | "lie"
    private static int _坐卧圈数;         // 当前场已播 B 圈数（= VPet looptimes）
    private static int _坐卧次数;         // 本会话已进 lie 次数（= VPet CountNomal）
    private static readonly Random _坐卧骰子 = new Random();

    /// <summary>坐卧会话进行中（StateMachine 用它做闸门：VPet 里 StateONE 期间 IsIdel=false，显示骰子整块被跳过）。</summary>
    public static bool 坐卧会话中 => _坐卧主名 != null;

    /// <summary>开始坐卧会话（VPet DisplayToIdel_StateONE）：挑当前情绪档的 sit 素材 → A 进入。
    /// 没有 sit 素材返回 false（调用方当没事发生）。</summary>
    public static bool 开始坐卧会话()
    {
        if (_单例 == null) return false;
        var 主名 = StateMachine.档名("sit");
        if (主名 == null || !有动画(主名 + "-a")) return false;
        _坐卧主名 = 主名; _坐卧场 = "sit"; _坐卧圈数 = 0; _坐卧次数 = 0;
        _单例.Play(主名 + "-a");
        return true;
    }

    /// <summary>B 变体列表（原版每圈随机换一个）：{主名}-b1/-b2/…；单变体时退回 {主名}。</summary>
    private static List<string> 坐卧B变体(string 主名)
    {
        var 结果 = new List<string>();
        for (var i = 1; i <= 4; i++)
            if (有动画($"{主名}-b{i}")) 结果.Add($"{主名}-b{i}");
        if (结果.Count == 0 && 有动画(主名)) 结果.Add(主名);
        return 结果;
    }

    private static void 播坐卧B()
    {
        var 变体 = 坐卧B变体(_坐卧主名);
        if (变体.Count == 0) { 结束坐卧会话(); return; }
        _单例.Play(变体[_坐卧骰子.Next(变体.Count)]);
    }

    /// <summary>会话收尾（sit 的 C 播完）：清会话、重置 idle 计数、回 idle。</summary>
    private static void 结束坐卧会话()
    {
        _坐卧主名 = null; _坐卧场 = null; _坐卧圈数 = 0; _坐卧次数 = 0;
        _idle循环次数 = 0;
        进入状态("idle");
    }

    /// <summary>坐卧段推进（OnAnimationFinished 的 sit/lie 分支，按动画名后缀分诊 -a / -bN / -c）。</summary>
    private static void 推进坐卧会话(string 当前名)
    {
        if (_坐卧主名 == null) { 进入状态("idle"); return; }   // 脏状态兜底
        if (当前名.EndsWith("-a", StringComparison.Ordinal))
        {
            _坐卧圈数 = 1;      // = VPet 回调计数：A 播完是第 1 次（Next(1)=0 恒不过线 → 必进 B）
            播坐卧B();
            return;
        }
        if (当前名.EndsWith("-c", StringComparison.Ordinal))
        {
            if (_坐卧场 == "lie")
            {
                // lie 收尾 → 回 sit 的 B 判定（退 lie 时 looptimes 已清零；= VPet C_End → DisplayIdel_StateONEing）
                _坐卧场 = "sit";
                _坐卧主名 = StateMachine.档名("sit") ?? _坐卧主名;
                _坐卧圈数 = 1;
                播坐卧B();
                return;
            }
            结束坐卧会话();      // sit 收尾 → 回待机
            return;
        }
        // —— 一圈 B 播完：掷「退出骰」——
        _坐卧圈数++;
        var 通过 = 探针_坐卧退出覆盖 ?? (_坐卧骰子.Next(_坐卧圈数) > StateMachine.设置.坐卧循环L);
        if (!通过) { 播坐卧B(); return; }
        if (_坐卧场 == "sit")
        {
            // 去向判定：1/(2 + 已躺次数) 进 lie，否则 sit 收场（= VPet Rnd.Next(2 + CountNomal)）
            var 躺 = 探针_坐卧躺下覆盖 ?? (_坐卧骰子.Next(2 + _坐卧次数) == 0);
            var 躺主名 = 躺 ? StateMachine.档名("lie") : null;
            if (躺主名 != null && 有动画(躺主名 + "-a"))
            {
                _坐卧场 = "lie"; _坐卧主名 = 躺主名; _坐卧圈数 = 0; _坐卧次数++;
                _单例.Play(躺主名 + "-a");
                return;
            }
            _单例.Play(_坐卧主名 + "-c");   // sit 收场（C 播完 → idle）
            return;
        }
        // lie 场通过 → C 起身（喂下一段判定的 looptimes 清零）
        _坐卧圈数 = 0;
        _单例.Play(_坐卧主名 + "-c");
    }

    /// <summary>探针钩子：强制「退出判定」结果（null = 掷真骰子）——让嵌套全流程可复现。</summary>
    public static bool? 探针_坐卧退出覆盖;
    /// <summary>探针钩子：强制「进 lie」判定结果（null = 掷真骰子）。</summary>
    public static bool? 探针_坐卧躺下覆盖;
    /// <summary>探针：直接以指定主名开 sit 会话（绕过档位随机）。</summary>
    public static void 探针_开始坐卧(string 主名)
    {
        if (_单例 == null || !有动画(主名 + "-a")) return;
        _坐卧主名 = 主名; _坐卧场 = "sit"; _坐卧圈数 = 0; _坐卧次数 = 0;
        _单例.Play(主名 + "-a");
    }
    public static string 坐卧会话_只读 => _坐卧主名;
    public static string 坐卧场_只读 => _坐卧场;
    public static int 坐卧圈数_只读 => _坐卧圈数;
    public static int 坐卧次数_只读 => _坐卧次数;
    public static List<string> 探针_坐卧B变体列表(string 主名) => 坐卧B变体(主名);

    /// <summary>会话作废（别的状态接管时由 StateMachine 调）：只清字段，不动表现（新状态自己播）。</summary>
    public static void 作废坐卧会话()
    {
        _坐卧主名 = null; _坐卧场 = null; _坐卧圈数 = 0; _坐卧次数 = 0;
    }

    public static void 载入人物动画()
    {
        var 人物 = 显示人物;
        var 状态机 = _单例.SpriteFrames;
        var 进入动画 = 人物.动画池字典["enter"].列表随机项();
        var 退出动画 = 人物.动画池字典["exit"].列表随机项();
        加载动画(状态机,进入动画);
        _单例.Play(进入动画.name);//先显示,再加载后面动画
        foreach (var 动画组 in 内置动画组)
        {
            加载动画组(人物,动画组);
        }
        加载动画(状态机,退出动画);
        _退出动画名 = 退出动画.name;
        初始化窗口尺寸(); // 动画就绪 -> 窗口收缩到正好套住角色
    }
    private static void 加载动画组(人物数据 人物,string id)
    {
        if (人物.动画池字典.TryGetValue(id,out var list))
        {
            foreach (var 动画 in list)
            {
                加载动画(_单例.SpriteFrames,动画);
            }
        }
    }
    private static void 加载动画(SpriteFrames 状态机, 动画信息 动画信息) => 加载动画(状态机,动画信息.name,动画信息.Path,动画信息.rate,动画信息.Type,动画信息.durations);
    private static void 加载动画(SpriteFrames 状态机, string 动画名, string 目录, int 帧率, string 池 = null, List<int> 帧时长 = null)
    {
        // 1. 检查目录是否存在 (使用绝对路径)
        if (!DirAccess.DirExistsAbsolute(目录))
        {
            GD.PrintErr($"[错误] 外部目录不存在: {目录}");
            return;
        }
        // 2. 初始化动画轨道
        if (状态机.HasAnimation(动画名))
        {
            状态机.RemoveAnimation(动画名);
        }
        状态机.AddAnimation(动画名);
        状态机.SetAnimationSpeed(动画名, 帧率);
        // 包裹段（组①）的 A/C 段**不能**按循环加载：循环动画不回「播完」信号，进入/退出段会永远卡住。
        // 段名约定 = `-a` / `-c` 结尾（sleep-a / sleep-happy-c / think-nomal-a …）——见 StateMachine 包裹段。
        var 是段 = 动画名.EndsWith("-a", StringComparison.Ordinal) || 动画名.EndsWith("-c", StringComparison.Ordinal);
        状态机.SetAnimationLoop(动画名, 池 != null && 循环动画组.Contains(池) && !是段);

        // 3. 获取所有 PNG 文件
        using var dir = DirAccess.Open(目录);
        dir.ListDirBegin();
    
        var filePaths = new List<string>();
        var fileName = dir.GetNext();

        while (fileName != "")
        {
            if (!dir.CurrentIsDir() && fileName.ToLower().EndsWith(".png"))
            {
                // 注意：这里需要存储完整的绝对路径
                filePaths.Add(目录.PathJoin(fileName));
            }
            fileName = dir.GetNext();
        }

        // 4. 自然排序（防止 frame10 排在 frame2 前面）
        filePaths.Sort();

        // 5. 循环加载外部文件并转为 Texture
        //    2026-09-22 逐帧时长：info.json 的 durations（相对时长，单位 1/帧率 秒）逐帧带上——
        //    还原原版「定格/慢动作」节奏（VPet 每帧自带 ms：250=爬墙、500=咀嚼、1000+=长定格）。
        //    无 durations（旧素材/手写 mod）= 每帧 1，行为与从前一致。
        var 帧序 = 0;
        foreach (var path in filePaths)
        {
            // 从磁盘读取字节数据
            var buffer = FileAccess.GetFileAsBytes(path);
            if (buffer == null || buffer.Length == 0) { 帧序++; continue; }

            // 创建 Image 并加载数据
            var img = new Image();
            var err = img.LoadPngFromBuffer(buffer);
        
            if (err == Error.Ok)
            {
                // 将 Image 转为 Godot 渲染可用的 ImageTexture
                var texture = ImageTexture.CreateFromImage(img);
                var 时长 = 帧时长 != null && 帧序 < 帧时长.Count && 帧时长[帧序] > 0 ? 帧时长[帧序] : 1f;
                状态机.AddFrame(动画名, texture, 时长);
            }
            else
            {
                GD.PrintErr($"[解析失败] 无法加载图片: {path}, 错误代码: {err}");
            }
            帧序++;
        }
    }
}

public static class 工具库
{    
// 定义一个静态随机数实例，以确保随机性并避免重复实例化
    private static readonly Random DefaultRand = new Random();
    /// <summary>
    /// 从列表中随机获取一项
    /// </summary>
    public static T 列表随机项<T>(this List<T> list, Random rand = null)
    {
        // 如果未传入参数，则使用默认的静态随机数实例
        rand ??= DefaultRand;
        if (list == null || list.Count == 0)
        {
            return default(T);
        }

        return list[rand.Next(list.Count)];
    }
}