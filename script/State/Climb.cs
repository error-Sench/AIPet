using System;
using Godot;
using desktop.script.UX;

namespace desktop.script.State;

/// <summary>
/// 爬边（组② 行为层）：走到屏幕左/右边缘 → 悬挂上墙 → 向上爬 → 顶边横爬 → 对侧向下爬 → 松手掉落 → 落地回待机。
/// <para>
/// 素材语义（VPet `MOVE/*`，语义判据 = 官方 `vup.lps` 的 move 行 + `GraphHelper.Move`）：
/// `climb-{侧}-a` 扑向墙挂住 / `-b` 手脚交替爬（循环，方向由窗口位移决定）/ `-c` 脱手回站姿；
/// `climb_top-{向}-a/b/c` 顶边横爬（素材是横置构图：挂在顶边、身体垂在屏内）；
/// `fall-{向}-a` 脱手 / `-b` 横着下落（循环）/ `-c` 落地起身（21 帧长起身）。
/// 官方吸附几何：侧爬把窗口推出屏外 LocateLength（左 145 / 右 185 @Zoom1）；顶爬推出屏顶 150。
/// 我们的换算 = 可见比例参数（`挂边可见比例`/`顶挂可见比例`），左右不对称用偏移像素校正。
/// </para>
/// <para>
/// 设计（自主行为，与走动同闸门）：空闲达标时 `尝试走动` 有 `爬边概率` 改成爬边；
/// 流程 = 走向最近边（复用走动链）→ A 段播完吸附 → 垂直爬 → 顶爬 → 对侧下爬 → 近底转下落（自由落体 + 屏外 X 拉回）
/// → 落地 C 段 → idle。走完一整条路线后进冷却（`爬边冷却秒`）。
/// 过程中任何状态接管（拖拽/面板/Agent）= `让位`：窗口拉回屏内、流程终止。
/// </para>
/// <para>
/// 探针：纯函数（挂边/顶挂/落地/走到位 X、最近侧）可直接断言；相位机用 位置/屏幕/尺寸覆盖 注入假窗口（headless 可跑）。
/// </para>
/// </summary>
public static class Climb
{
    public enum 侧 { 左, 右 }
    public enum 相 { 无, 走向边, 侧爬, 顶爬, 下落, 落地 }

    public static 相 当前相 { get; private set; } = 相.无;
    public static 侧 当前侧 { get; private set; } = 侧.左;
    public static bool 占用中 => 当前相 != 相.无;

    // —— 配置（StateMachine.设置 注入；默认值即为缺省行为） ——
    public static bool 启用 { get; set; } = true;
    public static float 速度侧爬 { get; set; } = 90f;       // 悬爬垂直速度 px/s（官方 SpeedY 10/125ms ≈ 80）
    public static float 速度顶爬 { get; set; } = 64f;       // 顶边横爬 px/s（官方 SpeedX 8/125ms）
    public static float 掉落初速 { get; set; } = 240f;
    public static float 掉落加速度 { get; set; } = 1600f;
    public static float 掉落终端速度 { get; set; } = 1400f;
    public static float 挂边可见比例 { get; set; } = 0.52f; // 侧挂：留在屏内的窗口宽度比例
    public static float 顶挂可见比例 { get; set; } = 0.55f; // 顶挂：留在屏内的窗口高度比例
    public static int 左偏移像素 { get; set; }
    public static int 右偏移像素 { get; set; }
    public static int 顶偏移像素 { get; set; }
    public static float 走到边余量比例 { get; set; } = 0.30f; // 走向边时，窗口 X 距屏边 = 窗口宽×此比例就停下（然后 A 段吸附）
    public static int 脚底余量像素 { get; set; } = 6;         // 落地时脚底距屏底的余量
    public static float 回落拉回速度 { get; set; } = 320f;    // 下落中把屏外的 X 拉回屏内的速度
    public static float 冷却秒 { get; set; } = 600f;          // 爬完一整条路线后的冷却

    private static float _冷却;
    private static int _爬方向;         // 侧爬：-1 向上 / +1 向下
    private static int _顶方向;         // 顶爬：-1 向左 / +1 向右
    private static float _掉落速度;
    private static bool _吸附完成;      // 进入段（A）播完、已吸附到边线上（之后才走位移）
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
    public static int 挂边位置X(侧 侧, Rect2I 屏, int 窗口宽, float 可见比例, int 偏移)
    {
        var 留 = (int)(窗口宽 * Math.Clamp(可见比例, 0.05f, 0.95f));
        return 侧 == 侧.左 ? 屏.Position.X - (窗口宽 - 留) + 偏移 : 屏.End.X - 留 - 偏移;
    }

    /// <summary>顶挂位置 Y：把窗口推出屏顶，只留「可见比例」在屏内。偏移语义：正 = 往屏内多推（向下）。</summary>
    public static int 顶挂位置Y(Rect2I 屏, int 窗口高, float 可见比例, int 偏移)
    {
        var 留 = (int)(窗口高 * Math.Clamp(可见比例, 0.05f, 0.95f));
        return 屏.Position.Y - (窗口高 - 留) + 偏移;
    }

    /// <summary>落地（脚踩地面）时的窗口 Y：窗口底贴屏底、脚底留余量。</summary>
    public static int 落地Y(Rect2I 屏, int 窗口高, int 脚底余量) => 屏.End.Y - 窗口高 + 脚底余量;

    /// <summary>走向边时停下的 X（还留在屏内，等 A 段再吸附出去）。</summary>
    public static int 走到位置X(侧 侧, Rect2I 屏, int 窗口宽, float 余量比例)
    {
        var 余 = (int)(窗口宽 * Math.Clamp(余量比例, 0f, 0.9f));
        return 侧 == 侧.左 ? 屏.Position.X + 余 : 屏.End.X - 窗口宽 - 余;
    }

    /// <summary>离哪边近（按窗口中心）。</summary>
    public static 侧 最近侧(Rect2I 屏, int 窗口中心X)
        => Math.Abs(窗口中心X - 屏.Position.X) <= Math.Abs(屏.End.X - 窗口中心X) ? 侧.左 : 侧.右;

    // ================= 触发 =================

    /// <summary>能不能开始一次爬边（调度方用）。</summary>
    public static bool 可触发()
    {
        if (!启用 || 占用中 || _冷却 > 0f) return false;
        if (!素材齐()) return false;
        var 屏 = Climb.屏();   // 限定类名：直接写 屏() 会被解析成「正在声明的局部变量」（CS0841）
        var 位 = Climb.位();
        var 尺 = Climb.尺();
        // 得完整在屏内、且上方有空间可爬（不然挂上去只能原地蠕动）
        if (位.X < 屏.Position.X || 位.X + 尺.X > 屏.End.X) return false;
        var 上方空间 = 位.Y - 屏.Position.Y;
        if (上方空间 < 150) return false;
        return true;
    }

    private static bool 素材齐()
    {
        var 齐 = CharAnim.有动画("climb-left-a") && CharAnim.有动画("climb-right-a")
              && CharAnim.有动画("fall-left-a") && CharAnim.有动画("fall-right-a");
        if (!齐 && !_已报警告)
        {
            _已报警告 = true;
            GD.PrintErr("[Climb] 素材缺失（climb-*-a / fall-*-a）→ 爬边禁用；检查 mods/main_anim/anim/loris/climb/ 与 fall/");
        }
        return 齐;
    }

    // ================= 流程 =================

    /// <summary>开始一次爬边：走向最近边 → 到边吸附上墙。由调度方（尝试走动）或 set_state 直接调用。</summary>
    public static void 开始()
    {
        if (占用中) return;
        if (!素材齐()) return;
        var 屏 = Climb.屏();   // 限定类名：直接写 屏() 会被解析成「正在声明的局部变量」（CS0841）
        var 位 = Climb.位();
        var 尺 = Climb.尺();
        当前侧 = 最近侧(屏, 位.X + 尺.X / 2);
        var 到边X = 走到位置X(当前侧, 屏, 尺.X, 走到边余量比例);
        if (Math.Abs(位.X - 到边X) <= 8)
        {
            到达边缘(当前侧);
            return;
        }
        当前相 = 相.走向边;
        GD.Print($"[Climb] 开始爬边：走向{当前侧}边（X {位.X} → {到边X}）");
        // 不挂链回调：到边由 每帧 检测（回调路径会走链引擎的空链分支 → SetState(Idle) 顶掉爬边状态）
        StateMachine.走向目标X(到边X);
    }

    /// <summary>走到边了（或本来就在边上）：上墙（A 段 → 吸附 → B 循环）。</summary>
    public static void 到达边缘(侧 侧)
    {
        当前侧 = 侧;
        当前相 = 相.侧爬;
        _爬方向 = -1;        // 先向上爬
        _吸附完成 = false;
        _掉落速度 = 0f;
        GD.Print($"[Climb] 到达{侧}边 → 上墙（A 段）");
        StateMachine.SetState(StateMachine.ClimbState);
    }

    /// <summary>每帧（由 StateMachine._Process 驱动）：位移 + 相位推进。</summary>
    public static void 每帧(float delta)
    {
        if (_冷却 > 0f) _冷却 -= delta;
        if (当前相 == 相.无) return;

        var 位 = Climb.位();
        var 屏 = Climb.屏();   // 限定类名：直接写 屏() 会被解析成「正在声明的局部变量」（CS0841）
        var 尺 = Climb.尺();

        switch (当前相)
        {
            case 相.走向边:
                {
                    // 到边检测（帧检查——比链回调稳：回调路径会被链引擎空链分支顶掉状态）
                    var 目标 = 走到位置X(当前侧, 屏, 尺.X, 走到边余量比例);
                    if (Math.Abs(位.X - 目标) <= 8) 到达边缘(当前侧);
                }
                break;

            case 相.侧爬:
                if (!_吸附完成) break;   // A 段还没播完
                {
                    var 新Y = 位.Y + _爬方向 * (int)Math.Max(1f, Math.Round(速度侧爬 * delta));
                    if (_爬方向 < 0)
                    {
                        var 顶线 = 顶挂位置Y(屏, 尺.Y, 顶挂可见比例, 顶偏移像素);
                        if (新Y <= 顶线)
                        {
                            设位(new Vector2I(位.X, 顶线));
                            转顶爬();
                            return;
                        }
                    }
                    else
                    {
                        var 底线 = 落地Y(屏, 尺.Y, 脚底余量像素) - 240;
                        if (新Y >= 底线)
                        {
                            设位(new Vector2I(位.X, 底线));
                            转下落();
                            return;
                        }
                    }
                    设位(new Vector2I(位.X, 新Y));
                }
                break;

            case 相.顶爬:
                if (!_吸附完成) break;
                {
                    var 新X = 位.X + _顶方向 * (int)Math.Max(1f, Math.Round(速度顶爬 * delta));
                    var 中心 = 新X + 尺.X / 2;
                    if (_顶方向 > 0 && 中心 >= 屏.End.X - 80)
                    {
                        转侧爬下(侧.右);
                        return;
                    }
                    if (_顶方向 < 0 && 中心 <= 屏.Position.X + 80)
                    {
                        转侧爬下(侧.左);
                        return;
                    }
                    设位(new Vector2I(新X, 位.Y));
                }
                break;

            case 相.下落:
                if (!_吸附完成) break;
                {
                    _掉落速度 = Math.Min(掉落终端速度, _掉落速度 + 掉落加速度 * delta);
                    var 新Y = 位.Y + (int)Math.Max(1f, Math.Round(_掉落速度 * delta));
                    var 底线 = 落地Y(屏, 尺.Y, 脚底余量像素);
                    if (新Y >= 底线)
                    {
                        设位(new Vector2I(位.X, 底线));
                        转落地();
                        return;
                    }
                    // 挂在屏外的 X 在下落中拉回屏内（不瞬移，观感自然）
                    var 步 = (int)Math.Max(1f, Math.Round(回落拉回速度 * delta));
                    var 新X = 位.X;
                    if (位.X < 屏.Position.X) 新X = Math.Min(屏.Position.X, 位.X + 步);
                    else if (位.X + 尺.X > 屏.End.X) 新X = Math.Max(屏.End.X - 尺.X, 位.X - 步);
                    设位(new Vector2I(新X, 新Y));
                }
                break;

            case 相.落地:
                break;   // 等 C 段播完
        }
    }

    /// <summary>动画播完的回调（由 StateMachine.重播当前状态 转发）：按相位推进段。</summary>
    public static void 动画播完()
    {
        switch (当前相)
        {
            case 相.侧爬:
                if (!_吸附完成)
                {
                    _吸附完成 = true;
                    设位(new Vector2I(挂边位置X(当前侧, 屏(), 尺().X, 挂边可见比例, 侧偏移(当前侧)), 位().Y));
                }
                播($"{侧前缀()}-b");
                break;
            case 相.顶爬:
                _吸附完成 = true;   // 顶挂吸附在 转顶爬() 里已完成
                播($"{顶前缀()}-b");
                break;
            case 相.下落:
                if (!_吸附完成)
                {
                    _吸附完成 = true;
                    if (!CharAnim.有动画($"{落前缀()}-b")) { 转落地(); return; }   // 缺 B 段直接落地
                }
                播($"{落前缀()}-b");
                break;
            case 相.落地:
                完成();
                break;
        }
    }

    private static void 转顶爬()
    {
        当前相 = 相.顶爬;
        _顶方向 = 当前侧 == 侧.左 ? 1 : -1;   // 从哪边上来的就往对侧爬
        _吸附完成 = false;
        设位(new Vector2I(位().X, 顶挂位置Y(屏(), 尺().Y, 顶挂可见比例, 顶偏移像素)));
        GD.Print("[Climb] 爬到顶 → 转顶边横爬");
        播($"{顶前缀()}-a");
    }

    private static void 转侧爬下(侧 侧)
    {
        当前侧 = 侧;
        当前相 = 相.侧爬;
        _爬方向 = 1;         // 向下
        _吸附完成 = true;    // 从顶边转过来：B 段直接续（素材没有「顶→侧」专用过渡段）
        设位(new Vector2I(挂边位置X(侧, 屏(), 尺().X, 挂边可见比例, 侧偏移(侧)), 位().Y));
        GD.Print($"[Climb] 顶边爬到端 → 沿{侧}边向下爬");
        播($"{侧前缀()}-b");
    }

    private static void 转下落()
    {
        当前相 = 相.下落;
        _掉落速度 = 掉落初速;
        _吸附完成 = false;
        GD.Print("[Climb] 转下落");
        播($"{落前缀()}-a");
    }

    private static void 转落地()
    {
        当前相 = 相.落地;
        _吸附完成 = true;
        设位(new Vector2I(位().X, 落地Y(屏(), 尺().Y, 脚底余量像素)));
        GD.Print("[Climb] 落地");
        播($"{落前缀()}-c");
    }

    private static void 完成()
    {
        当前相 = 相.无;
        _吸附完成 = false;
        _冷却 = 冷却秒;
        GD.Print("[Climb] 爬边完成 → idle");
        StateMachine.SetState(StateMachine.Idle);
    }

    // ================= 让位 / 应用表现 =================

    /// <summary>让位给其它状态（状态机在别人抢状态时调用）：窗口拉回屏内、流程终止。</summary>
    public static void 让位()
    {
        if (当前相 == 相.无) return;
        var 屏 = Climb.屏();   // 限定类名：直接写 屏() 会被解析成「正在声明的局部变量」（CS0841）
        var 尺 = Climb.尺();
        var 位 = Climb.位();
        var 回X = Math.Clamp(位.X, 屏.Position.X, Math.Max(屏.Position.X, 屏.End.X - 尺.X));
        var 回Y = Math.Clamp(位.Y, 屏.Position.Y, Math.Max(屏.Position.Y, 屏.End.Y - 尺.Y));
        设位(new Vector2I(回X, 回Y));
        当前相 = 相.无;
        _吸附完成 = false;
        GD.Print("[Climb] 让位：窗口拉回屏内、流程终止");
    }

    /// <summary>由 StateMachine.应用表现 调用：按当前相位播对应段；相=无（被 set_state 直接切进来）则就地开始。</summary>
    public static void 应用表现()
    {
        switch (当前相)
        {
            case 相.无:
                开始();
                break;
            case 相.走向边:
                break;   // 走路段由状态机链在放
            case 相.侧爬:
                播($"{侧前缀()}-{(_吸附完成 ? "b" : "a")}");
                break;
            case 相.顶爬:
                播($"{顶前缀()}-{(_吸附完成 ? "b" : "a")}");
                break;
            case 相.下落:
                播($"{落前缀()}-{(_吸附完成 ? "b" : "a")}");
                break;
            case 相.落地:
                播($"{落前缀()}-c");
                break;
        }
    }

    // ================= 工具 =================

    private static string 侧前缀() => 当前侧 == 侧.左 ? "climb-left" : "climb-right";
    private static string 顶前缀() => _顶方向 > 0 ? "climb_top-right" : "climb_top-left";
    private static string 落前缀() => 当前侧 == 侧.左 ? "fall-left" : "fall-right";   // 掉落按「从哪侧下来」选朝向
    private static int 侧偏移(侧 侧) => 侧 == 侧.左 ? 左偏移像素 : -右偏移像素;

    private static void 播(string 动画名)
    {
        if (CharAnim.有动画(动画名)) { CharAnim.PlayNamed(动画名); return; }
        if (!_已报警告)
        {
            _已报警告 = true;
            GD.PrintErr($"[Climb] 素材缺失：{动画名}（流程降级/终止；检查 mods/main_anim/anim/loris/）");
        }
    }

    // ================= 探针专用 =================

    public static 相 探针_相 => 当前相;
    public static 侧 探针_侧 => 当前侧;
    public static float 探针_冷却 => _冷却;
    public static bool 探针_吸附完成 => _吸附完成;
    public static int 探针_爬方向 => _爬方向;
    public static int 探针_顶方向 => _顶方向;

    /// <summary>探针：设冷却（0 = 可立即触发）。</summary>
    public static void 探针_设冷却(float 秒) => _冷却 = 秒;

    /// <summary>探针：直接设相位（跳过走向边等前置；不动窗口）。</summary>
    public static void 探针_设相(相 相, 侧 侧 = 侧.左)
    {
        当前相 = 相;
        当前侧 = 侧;
    }

    /// <summary>探针：接上「走向边完成」回调（模拟走链到点）。</summary>
    public static void 探针_到达边缘(侧 侧) => 到达边缘(侧);

    public static void 探针_重置()
    {
        当前相 = 相.无;
        _冷却 = 0f;
        _吸附完成 = false;
        _掉落速度 = 0f;
        探针_位置覆盖 = null;
        探针_屏幕覆盖 = null;
        探针_尺寸覆盖 = null;
        _已报警告 = false;
    }
}
