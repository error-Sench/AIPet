using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using desktop.script.UX;

namespace desktop.script.State;

/// <summary>
/// 智能移动（重构#4：**抄 VPet 原库的移动方式**——GraphHelper.Move + DisplayToMove 的「数据驱动移动 + 兼容接力」模型）。
/// 替代旧的「走链 + Climb 相位机」两套自写逻辑。
/// <para>
/// VPet 模型（源码对照 `VPet-Simulator.Core_Graph_GraphHelper.cs:378-544`、`MainDisplay.cs:110-128`、vup.lps 16 条 move: 行）：
/// ① **移动 = 数据条目**（lps 一行 = 一条定义）：触发 / 检查条件（屏幕边距 + 方向位）、速度、距离骰参数、档位过滤、贴边吸附；
/// ② `DisplayToMove`：在全部移动里**随机轮询**，第一条「触发通过」的开跑（= 我们的 `尝试移动`）；
/// ③ 运行：A_Start（起步）→ 贴边吸附 → B 循环（窗口按速度位移）→ 每圈 B 播完依次掷骰：
///    检查不过 → 接力骰；距离骰不过 → 接力骰；都不中 → 收势 C_End 回待机；
/// ④ **接力**（GetCompatibilityMove）：方向评分（同向 +1 / 反向 -1；某轴任一方为 0 不参与）≥0 且触发通过的候选里随机挑一条，
///    直接接它的 A 段（不回待机）——「走→爬→顶爬→掉落」整条路线是**概率接力自然涌现**的，不是写死的状态机。
/// </para>
/// <para>
/// 我们的换算与取舍（「只抄机制/动作，不抄数值」）：
/// - 边距阈值 ≈ VPet 值 × 0.5（它 500px 窗 / 我们 256px 窗）：walk 触发 200→100、检查 100→50；climb 近边 100→64（略放宽）；
/// - 速度用 px/s（VPet 每 125ms 的像素 ×8 同量级）；几何（挂边/顶挂/偏移/脚底余量）与掉落物理沿用我们 2026-09-20 调过的值；
/// - 接力概率可调（VPet 40%；默认 0.8 让整条路线更常一气呵成）；距离骰按我们的观感重定标（走 3 → 单程 ≈4s）；
/// - 档位过滤保留我们的三档（happy/nomal/poor）：walk 只做普通档、档位三档通用（2026-09-24 Plan #22——不按 happy/poor 分快慢，快/慢变体已删）；
///   crawl/climb/fall 照 VPet ModeType 限 nomal|poor（开心档不爬，VPet 同款）。
/// - 定义表见 `config/moves.json`（缺失用内置默认）——**加新移动方式 = 加一条数据，不用改代码**。
/// </para>
/// <para>
/// 探针：纯函数（挂边/顶挂/落地位置、距离骰、接力骰、兼容评分、触发/检查）+「位置/屏幕/尺寸覆盖」注入假窗口（headless 全流程）。
/// </para>
/// </summary>
public static class MoveRunner
{
    // ================= 数据模型 =================

    /// <summary>一条移动定义（= VPet lps 的一行 move:）。</summary>
    public sealed class 移动定义
    {
        public string 名 = "";
        public string 动画 = "";            // 资产前缀：walk-left / crawl-left / climb-left / climb_top-right / fall-left
        public string[] 档位 = { "nomal" }; // 允许的情绪档（happy/nomal/poor）；三档关闭时恒为 nomal
        /// <summary>触发「近边」：距该边 ≤ 值 才可开跑（VPet 方向位）；空 = 不检查。</summary>
        public Dictionary<string, int> 触发近 = new();
        /// <summary>触发「远边」：距该边 ≥ 值 才可开跑（VPet *Greater 位）；空 = 不检查。</summary>
        public Dictionary<string, int> 触发远 = new();
        public Dictionary<string, int> 检查近 = new();
        /// <summary>检查「远边」：不满足 → 停/接力（VPet Check*）；空 = 恒通过。</summary>
        public Dictionary<string, int> 检查远 = new();
        public float 速度X;                 // px/s（正右负左）；重力移动只用于接力评分
        public float 速度Y;                 // px/s（正下负上）；重力移动只用于接力评分
        public int 距离 = 3;                // 距离骰：每圈 Rnd.Next(圈数) < 距离 → 继续（VPet Distance）
        public string 吸附 = "";            // ""/left/right/top —— 进入段播完把窗口贴到该边（VPet LocateType）
        public bool 重力;                   // fall：加速下落 + X 拉回；落地即收尾
        public override string ToString() => 名;
    }

    private static List<移动定义> _表 = 默认表();

    /// <summary>全部移动定义（只读）。</summary>
    public static IReadOnlyList<移动定义> 表 => _表;

    /// <summary>按名字找一条定义（找不到 null）。</summary>
    public static 移动定义 找(string 名) => _表.FirstOrDefault(m => m.名 == 名);

    // ================= 配置（StateMachine.设置 注入；默认值即缺省行为） =================

    public static bool 启用 { get; set; } = true;
    public static float 接力概率 { get; set; } = 0.8f;        // VPet TreeRND=40%；我们默认调高（路线完整性优先），可配置
    public static float 冷却秒 { get; set; } = 600f;          // 重力移动（落地）后的「爬边族」冷却（我们保留的防重复观感）
    public static float 挂边可见比例 { get; set; } = 0.52f;
    public static float 顶挂可见比例 { get; set; } = 0.55f;
    public static int 左偏移像素 { get; set; }
    public static int 右偏移像素 { get; set; }
    public static int 顶偏移像素 { get; set; }
    public static int 脚底余量像素 { get; set; } = 6;
    public static float 掉落初速 { get; set; } = 240f;
    public static float 掉落加速度 { get; set; } = 1600f;
    public static float 掉落终端速度 { get; set; } = 1400f;
    public static float 回落拉回速度 { get; set; } = 320f;    // 下落中把屏外 X 拉回屏内的速度

    // ================= 运行状态 =================

    public enum 相 { 无, 进入, 循环, 收尾 }

    public static 相 当前相 { get; private set; } = 相.无;
    public static bool 占用中 => 当前相 != 相.无;
    public static 移动定义 当前定义 => _当前定义;

    private static 移动定义 _当前定义;

    private static int _圈数;            // 距离骰的圈计数（VPet walklength；每条移动开始清零）
    private static bool _吸附完成;       // 吸附动作已完成（或本条无需吸附）
    private static float _循环计时;      // 循环加载段的每圈计时（循环动画不回播完信号，见 每帧）
    private static float _冷却;
    private static float _掉落速度;
    private static string _当前段;       // 正在播的动画名
    private static bool _已触地;         // 重力移动：已夹到落地线（触发收尾，防重复）
    private static readonly Random 骰子 = new();
    private static bool _已报警告;

    // ================= 读取/写入（探针可覆盖） =================

    public static Vector2I? 探针_位置覆盖;
    public static Rect2I? 探针_屏幕覆盖;
    public static Vector2I? 探针_尺寸覆盖;

    private static Vector2I 位() => 探针_位置覆盖 ?? DisplayServer.WindowGetPosition();
    private static void 设位(Vector2I v)
    {
        if (探针_位置覆盖 != null) 探针_位置覆盖 = v;
        else DisplayServer.WindowSetPosition(v);
    }
    private static Rect2I 屏() => 探针_屏幕覆盖 ?? DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
    private static Vector2I 尺() => 探针_尺寸覆盖 ?? DisplayServer.WindowGetSize();

    // ================= 纯函数（探针直接断言） =================

    /// <summary>侧挂位置 X：把窗口推出屏外，只留「可见比例」在屏内。偏移语义同 EdgeHide：正 = 往屏内多推。</summary>
    public static int 挂边位置X(string 侧, Rect2I 屏, int 窗口宽, float 可见比例, int 偏移)
    {
        var 留 = (int)(窗口宽 * Math.Clamp(可见比例, 0.05f, 0.95f));
        return 侧 == "left" ? 屏.Position.X - (窗口宽 - 留) + 偏移 : 屏.End.X - 留 - 偏移;
    }

    /// <summary>顶挂位置 Y：把窗口推出屏顶，只留「可见比例」在屏内。偏移语义：正 = 往屏内多推（向下）。</summary>
    public static int 顶挂位置Y(Rect2I 屏, int 窗口高, float 可见比例, int 偏移)
    {
        var 留 = (int)(窗口高 * Math.Clamp(可见比例, 0.05f, 0.95f));
        return 屏.Position.Y - (窗口高 - 留) + 偏移;
    }

    /// <summary>落地（脚踩地面）时的窗口 Y：窗口底贴屏底、脚底留余量。</summary>
    public static int 落地Y(Rect2I 屏, int 窗口高, int 脚底余量) => 屏.End.Y - 窗口高 + 脚底余量;

    /// <summary>窗口距屏幕某边的余量（px，正 = 在屏内这么多，负 = 推出屏外）。</summary>
    public static int 距(string 方向, Vector2I 位, Rect2I 屏, Vector2I 尺) => 方向 switch
    {
        "左" => 位.X - 屏.Position.X,
        "右" => 屏.End.X - (位.X + 尺.X),
        "上" => 位.Y - 屏.Position.Y,
        _ => 屏.End.Y - (位.Y + 尺.Y),
    };

    /// <summary>距离骰（VPet `Rnd.Next(walklength++) &lt; Distance`）：true = 继续循环。</summary>
    public static bool 距离续圈(int 圈数, int 距离, Random 随) => 随.Next(Math.Max(0, 圈数)) < 距离;

    /// <summary>接力骰：true = 尝试换一条兼容移动（VPet `Rnd.Next(TreeRND) &lt;= 1` = 40%；概率可调）。</summary>
    public static bool 接力掷骰(Random 随) => 随.NextDouble() < Math.Clamp(接力概率, 0f, 1f);

    /// <summary>VPet GetCompatibilityMove：方向评分（同向 +1 / 反向 -1；某轴任一方为 0 不参与）≥0 且触发通过的候选。
    /// 注：VPet 未排除「自己」（跳过自己的判断被注释掉了）；我们排除——自接力只是重播一次进入段，实际收益低。</summary>
    public static List<移动定义> 兼容候选(移动定义 当前)
    {
        var 结果 = new List<移动定义>();
        if (当前 == null) return 结果;
        foreach (var m in _表)
        {
            if (ReferenceEquals(m, 当前) || m.名 == 当前.名) continue;
            var 分 = 0;
            if (当前.速度X != 0 && m.速度X != 0) 分 += (m.速度X > 0) == (当前.速度X > 0) ? 1 : -1;
            if (当前.速度Y != 0 && m.速度Y != 0) 分 += (m.速度Y > 0) == (当前.速度Y > 0) ? 1 : -1;
            if (分 >= 0 && 触发通过(m)) 结果.Add(m);
        }
        return 结果;
    }

    // ================= 条件判定 =================

    /// <summary>触发通过（VPet Triggered）：档位合适 + 素材在 + 位置条件满足 + 冷却只挡「爬边族」。</summary>
    public static bool 触发通过(移动定义 定义)
    {
        if (定义 == null || !定义.档位.Contains(StateMachine.当前情绪档)) return false;
        if (!素材齐(定义)) return false;
        if (_冷却 > 0f && (定义.吸附 != "" || 定义.重力)) return false;   // 冷却期不接爬边族（走照常）
        var 位 = MoveRunner.位(); var 屏 = MoveRunner.屏(); var 尺 = MoveRunner.尺();
        foreach (var (边, 值) in 定义.触发近) if (距(边, 位, 屏, 尺) > 值) return false;
        foreach (var (边, 值) in 定义.触发远) if (距(边, 位, 屏, 尺) < 值) return false;
        return true;
    }

    /// <summary>检查通过（VPet Checked）：不满足 → 停/接力。</summary>
    public static bool 检查通过(移动定义 定义)
    {
        var 位 = MoveRunner.位(); var 屏 = MoveRunner.屏(); var 尺 = MoveRunner.尺();
        foreach (var (边, 值) in 定义.检查近) if (距(边, 位, 屏, 尺) > 值) return false;
        foreach (var (边, 值) in 定义.检查远) if (距(边, 位, 屏, 尺) < 值) return false;
        return true;
    }

    private static bool 素材齐(移动定义 定义) =>
        CharAnim.有动画(定义.动画) || CharAnim.有动画(定义.动画 + "-b");

    // ================= 调度入口 =================

    /// <summary>尝试开始一次自主移动（VPet DisplayToMove：随机轮询触发通过的移动）。返回是否开跑。</summary>
    public static bool 尝试移动()
    {
        if (!启用 || 占用中) return false;
        var 候选 = _表.Where(触发通过).ToList();
        if (候选.Count == 0) return false;
        开始(候选[GD.RandRange(0, 候选.Count - 1)]);
        return true;
    }

    /// <summary>开始一条移动（接力/调度共用）：进入段 A → 吸附 → 循环 B。</summary>
    public static void 开始(移动定义 定义)
    {
        if (定义 == null || !素材齐(定义)) return;
        _当前定义 = 定义;
        _圈数 = 0;
        _循环计时 = 0f;
        _掉落速度 = 掉落初速;
        _已触地 = false;
        _吸附完成 = 定义.吸附 == "";
        当前相 = 段名(定义, "-a") != null ? 相.进入 : 相.循环;
        GD.Print($"[MoveRunner] 开跑：{定义.名}");
        if (StateMachine.CurrentState != StateMachine.MoveState) StateMachine.SetState(StateMachine.MoveState);   // → 应用表现 → 播段
        else PlaySegment();
    }

    // ================= 段推进 =================

    /// <summary>StateMachine.应用表现（含 SetState 直切 move）调用：播当前段；相=无（被外部直接切进来）则自己挑一条开跑。</summary>
    public static void 应用表现()
    {
        if (当前相 == 相.无) { 尝试移动(); return; }
        PlaySegment();
    }

    /// <summary>动画播完回调（StateMachine.重播当前状态 转发；循环加载段不回信号、由 每帧 计时驱动）。</summary>
    public static void 动画播完()
    {
        switch (当前相)
        {
            case 相.进入:
                吸附();
                _吸附完成 = true;
                当前相 = 相.循环;
                PlaySegment();
                break;
            case 相.循环:
                圈完成();
                break;
            case 相.收尾:
                结束();
                break;
        }
    }

    /// <summary>每帧：位移（循环段 + 吸附完成后）+ 循环加载段的圈计时。</summary>
    public static void 每帧(float delta)
    {
        if (_冷却 > 0f) _冷却 -= delta;
        if (当前相 != 相.循环) return;

        if (_吸附完成) 推进(delta);

        // 循环加载的段（walk/fall）：没有播完信号，用「段时长」当圈边界。
        if (_当前段 != null && CharAnim.动画循环_只读(_当前段))
        {
            var 时长 = CharAnim.动画时长(_当前段);
            if (时长 > 0.01f)
            {
                _循环计时 += delta;
                if (_循环计时 >= 时长)
                {
                    _循环计时 = 0f;
                    圈完成();
                }
            }
        }
    }

    /// <summary>一圈 B 播完（VPet Displaying）：先查「检查」，再掷距离骰，都通往「接力骰 / 收势」。</summary>
    private static void 圈完成()
    {
        if (当前相 != 相.循环 || _当前定义 == null) return;

        // 落地即完成一整条路线：开「爬边族」冷却（我们保留的防重复观感；VPet 无此概念）
        if (_当前定义.重力 && _冷却 <= 0f) _冷却 = 冷却秒;

        var 可继续 = 检查通过(_当前定义) && !(_当前定义.重力 && _已触地);
        if (!可继续)
        {
            // 探针钩子优先（探针_接力目标 != null = 强制走接力路径，绕开概率骰 → 全流程可复现）
            if (探针_接力目标 != null || 接力掷骰(骰子))
            {
                var 新 = 挑兼容();
                if (新 != null) { 开始(新); return; }
            }
            停();
            return;
        }
        if (距离续圈(_圈数++, _当前定义.距离, 骰子))
        {
            // 继续循环：非循环段重播一遍；循环段本来就无缝，不用重播
            if (!CharAnim.动画循环_只读(_当前段)) PlaySegment();
            return;
        }
        if (探针_接力目标 != null || 接力掷骰(骰子))
        {
            var 新 = 挑兼容();
            if (新 != null) { 开始(新); return; }
        }
        停();
    }

    private static 移动定义 挑兼容()
    {
        var 候选 = 兼容候选(_当前定义);
        if (候选.Count == 0) return null;
        // 探针钩子：强制接力到指定一条（让全流程探针可复现；生产路径为 null）
        if (探针_接力目标 != null)
        {
            var 指定 = 候选.FirstOrDefault(m => m.名 == 探针_接力目标);
            if (指定 != null) return 指定;
        }
        return 候选[GD.RandRange(0, 候选.Count - 1)];
    }

    /// <summary>收势（VPet StopMoving）：窗口回位 → 播 C 段（有的话）→ 回待机。</summary>
    private static void 停()
    {
        回位();
        var c = 段名(_当前定义, "-c");
        if (c != null)
        {
            当前相 = 相.收尾;
            PlaySegment();
        }
        else
        {
            结束();
        }
    }

    private static void 结束()
    {
        GD.Print($"[MoveRunner] 收步：{_当前定义?.名}");
        当前相 = 相.无;
        _当前定义 = null;
        _当前段 = null;
        _吸附完成 = false;
        _循环计时 = 0f;
        StateMachine.SetState(StateMachine.Idle);
    }

    /// <summary>让位（别人抢状态时由 StateMachine 调用）：窗口回位、流程终止。</summary>
    public static void 让位()
    {
        if (当前相 == 相.无) return;
        回位();
        当前相 = 相.无;
        _当前定义 = null;
        _当前段 = null;
        _吸附完成 = false;
        _循环计时 = 0f;
        GD.Print("[MoveRunner] 让位：移动终止");
    }

    // ================= 演出 / 位移 =================

    private static void PlaySegment()
    {
        if (_当前定义 == null) return;
        var 名 = 当前相 switch
        {
            相.进入 => 段名(_当前定义, "-a"),
            相.收尾 => 段名(_当前定义, "-c") ?? 段名(_当前定义, ""),
            _ => 段名(_当前定义, ""),
        };
        if (名 == null)
        {
            if (!_已报警告)
            {
                _已报警告 = true;
                GD.PrintErr($"[MoveRunner] 段素材缺失：{_当前定义.名}（检查 mods/main_anim/anim/loris/）");
            }
            结束();
            return;
        }
        _当前段 = 名;
        _循环计时 = 0f;
        CharAnim.PlayNamed(名);
    }

    /// <summary>进入段播完的贴边吸附（VPet MoveTimerSmartMove 的 LocateType/LocateLength 段）。</summary>
    private static void 吸附()
    {
        switch (_当前定义.吸附)
        {
            case "left":
                设位(new Vector2I(挂边位置X("left", 屏(), 尺().X, 挂边可见比例, 左偏移像素), 位().Y));
                break;
            case "right":
                设位(new Vector2I(挂边位置X("right", 屏(), 尺().X, 挂边可见比例, -右偏移像素), 位().Y));
                break;
            case "top":
                设位(new Vector2I(位().X, 顶挂位置Y(屏(), 尺().Y, 顶挂可见比例, 顶偏移像素)));
                break;
        }
    }

    private static void 推进(float delta)
    {
        var 位 = MoveRunner.位(); var 屏 = MoveRunner.屏(); var 尺 = MoveRunner.尺();
        int 新X, 新Y;

        if (_当前定义.重力)
        {
            // 掉落：加速物理 + 夹在落地线 + X 拉回屏内（不瞬移，观感自然）
            _掉落速度 = Math.Min(掉落终端速度, _掉落速度 + 掉落加速度 * delta);
            新Y = 位.Y + (int)Math.Max(1f, Math.Round(_掉落速度 * delta));
            var 落地 = 落地Y(屏, 尺.Y, 脚底余量像素);
            var 触地 = 新Y >= 落地;
            if (触地) 新Y = 落地;
            新X = 位.X;
            var 步 = (int)Math.Max(1f, Math.Round(回落拉回速度 * delta));
            if (位.X < 屏.Position.X) 新X = Math.Min(屏.Position.X, 位.X + 步);
            else if (位.X + 尺.X > 屏.End.X) 新X = Math.Max(屏.End.X - 尺.X, 位.X - 步);
            设位(new Vector2I(新X, 新Y));
            if (触地 && !_已触地)
            {
                _已触地 = true;
                圈完成();   // 落地立即收尾（不等这一圈动画放完）
            }
            return;
        }

        新X = 位.X + (int)Math.Round(_当前定义.速度X * delta);
        新Y = 位.Y + (int)Math.Round(_当前定义.速度Y * delta);
        设位(new Vector2I(新X, 新Y));
    }

    /// <summary>窗口回位（VPet MWController.ResetPosition）：某轴被推出屏外超过 25% → 贴回该边。</summary>
    private static void 回位()
    {
        var 屏 = MoveRunner.屏(); var 尺 = MoveRunner.尺(); var 位 = MoveRunner.位();
        var qx = Math.Max(1, (int)(尺.X * 0.25));
        var qy = Math.Max(1, (int)(尺.Y * 0.25));
        var 新X = 位.X; var 新Y = 位.Y;
        if (位.X - 屏.Position.X < -qx && 位.X + 尺.X <= 屏.End.X) 新X = 屏.Position.X;
        else if (屏.End.X - (位.X + 尺.X) < -qx && 位.X >= 屏.Position.X) 新X = 屏.End.X - 尺.X;
        if (位.Y - 屏.Position.Y < -qy && 位.Y + 尺.Y <= 屏.End.Y) 新Y = 屏.Position.Y;
        else if (屏.End.Y - (位.Y + 尺.Y) < -qy && 位.Y >= 屏.Position.Y) 新Y = 屏.End.Y - 尺.Y;
        if (新X != 位.X || 新Y != 位.Y) 设位(new Vector2I(新X, 新Y));
    }

    // ================= 工具 =================

    /// <summary>段名解析：`-a`/`-c` 直接试；循环段（段=空）先试无后缀（walk/crawl 命名），再试 `-b`（climb/climb_top/fall 命名）。</summary>
    private static string 段名(移动定义 定义, string 段)
    {
        if (段.Length > 0)
        {
            var 名 = 定义.动画 + 段;
            return CharAnim.有动画(名) ? 名 : null;
        }
        if (CharAnim.有动画(定义.动画)) return 定义.动画;
        var b = 定义.动画 + "-b";
        return CharAnim.有动画(b) ? b : null;
    }

    // ================= 定义表：加载 / 默认 =================

    private static Dictionary<string, int> 条(params (string 边, int 值)[] 对)
    {
        var d = new Dictionary<string, int>();
        foreach (var (边, 值) in 对) d[边] = 值;
        return d;
    }

    /// <summary>内置默认表（= `config/moves.json` 同内容；文件缺失/损坏时用它）。</summary>
    public static List<移动定义> 默认表()
    {
        // 走（VPet walk.left/right 系；#22：只做普通档——快/慢变体已删、档位三档通用，不按心情分快慢）——边距阈值 ≈ VPet×0.5
        var 表 = new List<移动定义>
        {
            new() { 名 = "walk-left", 动画 = "walk-left", 档位 = ["nomal", "happy", "poor"], 触发远 = 条(("左", 100)), 检查远 = 条(("左", 50)), 速度X = -90, 距离 = 3 },
            new() { 名 = "walk-right", 动画 = "walk-right", 档位 = ["nomal", "happy", "poor"], 触发远 = 条(("右", 100)), 检查远 = 条(("右", 50)), 速度X = 90, 距离 = 3 },
            // 趴行（慢速走；nomal|poor）
            new() { 名 = "crawl-left", 动画 = "crawl-left", 档位 = ["nomal", "poor"], 触发远 = 条(("左", 100)), 检查远 = 条(("左", 50)), 速度X = -65, 距离 = 3 },
            new() { 名 = "crawl-right", 动画 = "crawl-right", 档位 = ["nomal", "poor"], 触发远 = 条(("右", 100)), 检查远 = 条(("右", 50)), 速度X = 65, 距离 = 3 },
            // 侧爬（上/下）：近该边才吸得上；顶爬到头 / 下爬到「近底 240」接力（240 = 我们的「近底转下落」junction）
            new() { 名 = "climb-left-up", 动画 = "climb-left", 档位 = ["nomal", "poor"], 触发近 = 条(("左", 64)), 触发远 = 条(("上", 100)), 检查远 = 条(("上", 50)), 速度Y = -90, 距离 = 20, 吸附 = "left" },
            new() { 名 = "climb-right-up", 动画 = "climb-right", 档位 = ["nomal", "poor"], 触发近 = 条(("右", 64)), 触发远 = 条(("上", 100)), 检查远 = 条(("上", 50)), 速度Y = -90, 距离 = 20, 吸附 = "right" },
            new() { 名 = "climb-left-down", 动画 = "climb-left", 档位 = ["nomal", "poor"], 触发近 = 条(("左", 64)), 触发远 = 条(("下", 100)), 检查远 = 条(("下", 240)), 速度Y = 90, 距离 = 20, 吸附 = "left" },
            new() { 名 = "climb-right-down", 动画 = "climb-right", 档位 = ["nomal", "poor"], 触发近 = 条(("右", 64)), 触发远 = 条(("下", 100)), 检查远 = 条(("下", 240)), 速度Y = 90, 距离 = 20, 吸附 = "right" },
            // 顶爬（横爬）：近顶边才吸得上；爬到对侧端接力
            new() { 名 = "climb_top-left", 动画 = "climb_top-left", 档位 = ["nomal", "poor"], 触发近 = 条(("上", 64)), 触发远 = 条(("左", 100)), 检查远 = 条(("左", 50)), 速度X = -64, 距离 = 64, 吸附 = "top" },
            new() { 名 = "climb_top-right", 动画 = "climb_top-right", 档位 = ["nomal", "poor"], 触发近 = 条(("上", 64)), 触发远 = 条(("右", 100)), 检查远 = 条(("右", 50)), 速度X = 64, 距离 = 64, 吸附 = "top" },
            // 掉落：近该侧边（按从哪侧下来选朝向）+ 下方有空间；触地即收尾。速度仅用于接力评分（实位移=重力+拉回）
            new() { 名 = "fall-left", 动画 = "fall-left", 档位 = ["nomal", "poor"], 触发近 = 条(("左", 64)), 触发远 = 条(("下", 100)), 速度X = -20, 速度Y = 20, 距离 = 7, 重力 = true },
            new() { 名 = "fall-right", 动画 = "fall-right", 档位 = ["nomal", "poor"], 触发近 = 条(("右", 64)), 触发远 = 条(("下", 100)), 速度X = 20, 速度Y = 20, 距离 = 7, 重力 = true },
        };
        return 表;
    }

    /// <summary>加载 `config/moves.json`（找到了就**整体替换**内置默认表；没有/坏了就用默认）。</summary>
    public static void 加载()
    {
        foreach (var 路径 in Util.ConfigFile.候选("moves.json").Concat(Util.ConfigFile.候选("config/moves.json")))
        {
            try
            {
                if (!File.Exists(路径)) continue;
                var 文本 = File.ReadAllText(路径);
                if (string.IsNullOrWhiteSpace(文本)) continue;
                using var 文档 = JsonDocument.Parse(文本);
                if (!文档.RootElement.TryGetProperty("移动", out var 数组) || 数组.ValueKind != JsonValueKind.Array) continue;
                var 新表 = new List<移动定义>();
                foreach (var 项 in 数组.EnumerateArray())
                {
                    var d = new 移动定义
                    {
                        名 = 取文本(项, "名"),
                        动画 = 取文本(项, "动画"),
                        速度X = 取浮点(项, "速度X"),
                        速度Y = 取浮点(项, "速度Y"),
                        距离 = 取整数(项, "距离", 3),
                        吸附 = 取文本(项, "吸附"),
                        重力 = 取布尔(项, "重力"),
                    };
                    if (d.名.Length == 0 || d.动画.Length == 0) continue;
                    if (项.TryGetProperty("档位", out var 档) && 档.ValueKind == JsonValueKind.Array)
                    {
                        var 列 = 档.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray();
                        if (列.Length > 0) d.档位 = 列;
                    }
                    读边表(项, "触发近", d.触发近); 读边表(项, "触发远", d.触发远);
                    读边表(项, "检查近", d.检查近); 读边表(项, "检查远", d.检查远);
                    新表.Add(d);
                }
                if (新表.Count > 0)
                {
                    _表 = 新表;
                    GD.Print($"[MoveRunner] 移动表已加载（{_表.Count} 条）：{路径}");
                }
                return;
            }
            catch (Exception e)
            {
                GD.PrintErr($"[MoveRunner] 移动表解析失败（{路径}）：{e.Message} → 用内置默认表");
                return;
            }
        }
        GD.Print($"[MoveRunner] 未发现 moves.json → 用内置默认表（{_表.Count} 条）");
    }

    private static void 读边表(JsonElement 项, string 键, Dictionary<string, int> 目标)
    {
        if (!项.TryGetProperty(键, out var 组) || 组.ValueKind != JsonValueKind.Object) return;
        foreach (var 边 in 组.EnumerateObject())
        {
            if (边.Value.ValueKind == JsonValueKind.Number) 目标[边.Name] = 边.Value.GetInt32();
        }
    }

    private static string 取文本(JsonElement 项, string 键) =>
        项.TryGetProperty(键, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
    private static float 取浮点(JsonElement 项, string 键) =>
        项.TryGetProperty(键, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : 0f;
    private static int 取整数(JsonElement 项, string 键, int 缺省) =>
        项.TryGetProperty(键, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 缺省;
    private static bool 取布尔(JsonElement 项, string 键) =>
        项.TryGetProperty(键, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    // ================= 探针专用 =================

    public static 相 探针_相 => 当前相;
    public static string 探针_当前名 => _当前定义?.名;
    public static int 探针_圈数 => _圈数;
    public static bool 探针_吸附完成 => _吸附完成;
    public static float 探针_冷却 => _冷却;
    public static void 探针_设冷却(float 秒) => _冷却 = 秒;
    public static 移动定义 探针_定义(string 名) => 找(名);
    public static bool 探针_触发(string 名) => 触发通过(找(名));
    public static bool 探针_检查(string 名) => 检查通过(找(名));
    public static List<string> 探针_候选名() => _表.Where(触发通过).Select(m => m.名).ToList();
    public static List<string> 探针_兼容名() => 兼容候选(_当前定义).Select(m => m.名).ToList();
    public static void 探针_开始(string 名) => 开始(找(名));
    public static void 探针_圈完成() => 圈完成();
    /// <summary>探针钩子：强制接力到指定移动（null = 随机）。</summary>
    public static string 探针_接力目标;

    public static void 探针_重置()
    {
        当前相 = 相.无;
        _当前定义 = null;
        _当前段 = null;
        _圈数 = 0;
        _吸附完成 = false;
        _循环计时 = 0f;
        _冷却 = 0f;
        _掉落速度 = 0f;
        _已触地 = false;
        _已报警告 = false;
        探针_位置覆盖 = null;
        探针_屏幕覆盖 = null;
        探针_尺寸覆盖 = null;
        探针_接力目标 = null;
        _表 = 默认表();
        加载();
    }
}
