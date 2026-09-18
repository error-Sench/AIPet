using System;
using Godot;
using desktop.script.UX;

namespace desktop.script.State;

/// <summary>
/// 贴边隐藏（P2 剩余 —— **行为层**）：拖到屏幕左/右边缘 → 缩进边缘待着；鼠标靠近 → 探出；离开 → 缩回。
/// <para>
/// 素材语义（VPet `SideHide_*`，判据见 `tools/README.md`）：
/// `{left,right}-in` 缩进 / `-keep` 稳定（微动循环）/ `-hold` 长保持（单帧，当静止姿势用）/ `-out` 退出 /
/// `-peek` 探出（弹出动作，一次性）/ `-rise` **探出后的微动循环**（官方：鼠标靠近 → 循环 Rise/B）/
/// `-unpeek` 缩回（= 探出动作的逆序）。
/// <para>
/// 左右素材**逐段一一对应**（各 in9/keep4/hold1/out7/peek4/rise10/unpeek3 帧）：
/// VPet 的 `SideHide_Right_Main/A` 里多粘了 5 帧（2 帧「退出」起跳帧 + 3 帧收尾），
/// 导入时按帧切片重切（见 `tools/anim/import_vpet_anim.py` 规则 7）。
/// </para>
/// </para>
/// <para>
/// 设计（主人授权「你自己定吧」定的默认，见 `script/State/README.md`）：
/// 触发 = 拖拽结束且窗口距屏边 ≤ 阈值；隐藏 = 移出屏外只留 `可见比例`；交互 = 悬停探出 / 移开缩回；
/// 复位 = 点击 / 拖拽 / 面板打开 / 退出。
/// </para>
/// 约定：标识符英文，注释中文（见 AGENTS.md §8）。
/// </summary>
public static class EdgeHide
{
    public enum 侧 { 左, 右 }

    /// <summary>阶段：无 → 缩进中 → 隐藏（静止）→ 探出中 → 已探出 → 缩回中；复位走退出中。</summary>
    public enum 相 { 无, 缩进中, 隐藏, 探出中, 已探出, 缩回中, 退出中 }

    public static 相 当前相 { get; private set; } = 相.无;
    public static 侧 当前侧 { get; private set; } = 侧.左;

    /// <summary>是否正处于贴边流程中（含探出）。</summary>
    public static bool 占用中 => 当前相 != 相.无;

    // —— 配置（由 StateMachine.设置 加载后注入） ——
    public static bool 启用 { get; set; } = true;
    public static int 贴边阈值像素 { get; set; } = 20;
    public static float 可见比例 { get; set; } = 0.30f;
    public static float 探出可见比例 { get; set; } = 0.72f;
    public static float 缩回延迟秒 { get; set; } = 1.0f;
    public static float 滑行速度像素每秒 { get; set; } = 420f;

    /// <summary>探出/缩回的滑行速度（更慢：让「逐渐探出」看得见，而不是瞬间到位 —— 主人反馈「悬浮没有动画效果」）。</summary>
    public static float 探出滑行速度 { get; set; } = 130f;

    /// <summary>每侧微调偏移（像素）：正 = 往屏内多推，负 = 往屏外多推。
    /// 用途：导入时按**包围盒中心**对齐，左右两套姿势的质量分布不同 → 观感不对称，用这个校。</summary>
    public static int 左偏移像素 { get; set; }
    public static int 右偏移像素 { get; set; }

    /// <summary>循环动画的**周期**（秒）：隐藏/探出的循环不是连续播，而是「一个周期里循环几次」。
    /// 主人要求「每两秒循环两次贴边动画」→ 周期 2s、每周期循环 2 次、循环内间隔 0.5s。</summary>
    public static float 循环间隔秒 { get; set; } = 2f;

    /// <summary>一轮里循环几次（1 = 老行为「每周期一次」）。</summary>
    public static int 循环次数 { get; set; } = 2;

    /// <summary>循环内部的间隔（秒）。</summary>
    public static float 循环内间隔秒 { get; set; } = 0.5f;

    private static float _重播计时;      // 循环动画的下次重播倒计时
    private static int _本轮剩余;        // 本周期里还差几次没循环

    private static Vector2I _原位;        // 隐藏前的屏内位置（复位目标）
    private static Vector2I _目标位置;    // 当前滑动目标
    private static float _悬停离开计时;
    private static bool _已报警告;        // 素材缺失只提示一次

    // ================= 纯函数（探针可直接断言） =================

    /// <summary>窗口 X 是否算贴到边缘（返回命中的侧；没贴中返回 null）。
    /// 宽度由调用方传入，保持纯函数（探针可用合成尺寸断言）。
    /// <para>
    /// **判据对齐官方**：窗口必须**真的被推出屏外** ≥`阈值` 像素才吸附（官方 `distLeft &lt; -50×ZoomRatio`）。
    /// 原实现是「离屏边 `阈值` 以内」就吸（还在屏内）→ 太敏感：从屏边拖回中间会像被吸住。
    /// </para></summary>
    public static 侧? 判断贴边侧(int 窗口X, Rect2I 屏, int 阈值, int 窗口宽)
    {
        if (窗口X <= 屏.Position.X - 阈值) return 侧.左;
        if (窗口X + 窗口宽 >= 屏.End.X + 阈值) return 侧.右;
        return null;
    }

    /// <summary>隐藏时该放在哪：把窗口往屏外推，只留「可见比例」在屏内。</summary>
    public static int 隐藏位置X(侧 侧, Rect2I 屏, int 窗口宽, float 可见比例)
    {
        var 留 = (int)(窗口宽 * Math.Clamp(可见比例, 0.05f, 1f));
        return 侧 == 侧.左 ? 屏.Position.X - (窗口宽 - 留) : 屏.End.X - 留;
    }

    /// <summary>探出时该放在哪（露出更多，但仍贴边）。</summary>
    public static int 探出位置X(侧 侧, Rect2I 屏, int 窗口宽, float 可见比例)
    {
        var 留 = (int)(窗口宽 * Math.Clamp(可见比例, 0.05f, 1f));
        return 侧 == 侧.左 ? 屏.Position.X - (窗口宽 - 留) : 屏.End.X - 留;
    }

    private static int 窗口宽() => DisplayServer.WindowGetSize().X;

    private static Rect2I 可用屏() => DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());

    /// <summary>鼠标是否落在「屏内可见的那一条」上（隐藏时只有这一条可交互）。</summary>
    public static bool 鼠标在可见条(Vector2I 鼠标, Vector2I 窗口位, Vector2I 窗口尺, Rect2I 屏)
    {
        var 左 = Math.Max(屏.Position.X, 窗口位.X);
        var 右 = Math.Min(屏.End.X, 窗口位.X + 窗口尺.X);
        return 鼠标.X >= 左 && 鼠标.X < 右 &&
               鼠标.Y >= 窗口位.Y && 鼠标.Y < 窗口位.Y + 窗口尺.Y;
    }

    // ================= 对外动作 =================

    /// <summary>拖拽结束时调用：贴边就进入隐藏，否则什么都不做。</summary>
    public static void 检查贴边()
    {
        if (!启用) return;
        var 屏 = 可用屏();
        var 侧 = 判断贴边侧(DisplayServer.WindowGetPosition().X, 屏, 贴边阈值像素, 窗口宽());
        if (侧 == null) return;

        当前侧 = 侧.Value;
        _原位 = DisplayServer.WindowGetPosition();
        _目标位置 = new Vector2I(隐藏位置X(当前侧, 屏, 窗口宽(), 可见比例) + 该侧偏移(), _原位.Y);
        当前相 = 相.缩进中;
        StateMachine.SetState(StateMachine.EdgeHideState);
        GD.Print($"[EdgeHide] 贴{当前侧}边 → 缩进（目标 X={_目标位置.X}）");
    }

    /// <summary>复位：滑回原位、回 idle。任何交互/面板打开都会走这里。</summary>
    public static void 复位(string 原因 = "")
    {
        if (当前相 == 相.无) return;
        var 屏 = 可用屏();
        GD.Print($"[EdgeHide] 复位（{原因}）");
        _目标位置 = new Vector2I(Math.Clamp(_原位.X, 屏.Position.X, Math.Max(屏.Position.X, 屏.End.X - 窗口宽())), _原位.Y);
        当前相 = 相.退出中;
        StateMachine.SetState(StateMachine.EdgeHideState);
    }

    /// <summary>让位给其它状态（状态机在别人抢状态时调用）。
    /// <para>
    /// **关键防护一**：若宠还在屏外（缩进/隐藏/探出/缩回），必须把窗口拉回屏内，否则阶段清空后**再也滑不回来**。
    /// </para>
    /// <para>
    /// **关键防护二（实测 bug）**：`退出中` 表示已经复位过（点击/拖拽已经接管）→ **绝不能再动窗口**。
    /// 否则「拖到屏幕中间松手」时会把它拽回原位（原位往往贴着边缘）→ 又被判定贴边 → 看起来像**持续吸附回边缘**。
    /// </para></summary>
    public static void 让位()
    {
        if (当前相 == 相.无) return;
        if (当前相 == 相.退出中)
        {
            GD.Print("[EdgeHide] 让位（退出中 → 只清阶段，不动窗口：主人已接管）");
            当前相 = 相.无;
            _悬停离开计时 = 0f;
            return;
        }
        GD.Print($"[EdgeHide] 让位（{当前相} → 回原位）");
        // **必须夹进屏幕**：贴边触发时 _原位 本身就可能带一点屏外（比如 X=-30）。直接回原始值会把宠物留在屏外，
        // 而相已清空 → 再也没人把它滑回来（实测 bug，探针 case 608 抓到）。
        var 屏 = 可用屏();
        var 回X = Math.Clamp(_原位.X, 屏.Position.X, Math.Max(屏.Position.X, 屏.End.X - 窗口宽()));
        DisplayServer.WindowSetPosition(new Vector2I(回X, _原位.Y));
        当前相 = 相.无;
        _悬停离开计时 = 0f;
    }

    // ================= 每帧 / 动画回调 =================

    /// <summary>每帧：滑行到目标位置 + 悬停判定（由 StateMachine._Process 驱动）。</summary>
    public static void 每帧(float delta)
    {
        if (当前相 == 相.无) return;

        // 1. 滑行
        var 位 = DisplayServer.WindowGetPosition();
        if (位.X != _目标位置.X)
        {
            var 方向 = Math.Sign(_目标位置.X - 位.X);
            // 探出/缩回用更慢的速度：让「逐渐探出」看得见（主人反馈悬浮没有动画效果）
            var 速度 = 当前相 is 相.探出中 or 相.缩回中 ? 探出滑行速度 : 滑行速度像素每秒;
            var 步 = (int)Math.Max(1f, 速度 * delta);
            var 新X = 方向 > 0 ? Math.Min(_目标位置.X, 位.X + 步) : Math.Max(_目标位置.X, 位.X - 步);
            DisplayServer.WindowSetPosition(new Vector2I(新X, 位.Y));
            位 = DisplayServer.WindowGetPosition();
        }

        // 3. 循环动画的重播节拍：一个周期内循环「循环次数」次（主人：「每两秒循环两次贴边动画」）
        if (_重播计时 > 0f)
        {
            _重播计时 -= delta;
            if (_重播计时 <= 0f && 当前相 is 相.隐藏 or 相.已探出)
                播(当前相 == 相.隐藏 ? 静止动画() : 探出动画());
        }

        // 2. 悬停判定：只在「隐藏/已探出」这两个稳定相里切换探出/缩回
        if (当前相 is 相.隐藏 or 相.已探出)
        {
            var 悬停 = 鼠标在可见条(DisplayServer.MouseGetPosition(), 位, DisplayServer.WindowGetSize(), 可用屏());
            if (当前相 == 相.隐藏 && 悬停) { 探出(); }
            else if (当前相 == 相.已探出)
            {
                if (悬停) _悬停离开计时 = 0f;
                else
                {
                    _悬停离开计时 += delta;
                    if (_悬停离开计时 >= 缩回延迟秒) 缩回();
                }
            }
        }
    }

    /// <summary>动画播完的回调（由 StateMachine.重播当前状态 转发）：按阶段推进。</summary>
    public static void 动画播完()
    {
        switch (当前相)
        {
            case 相.缩进中:
                当前相 = 相.隐藏;
                播(静止动画());
                break;
            case 相.隐藏:
                break;   // 不立刻重播：等 循环间隔秒 的节拍（主人要求「每两秒循环两次贴边动画」）
            case 相.探出中:
                当前相 = 相.已探出;
                播(探出动画());   // 探出后转「微动循环」（Rise/B）——**不能停帧**（停帧 = 静止画面，主人反馈「没动画」）
                break;
            case 相.缩回中:
                当前相 = 相.隐藏;
                播(静止动画());
                break;
            case 相.退出中:
                当前相 = 相.无;
                StateMachine.SetState(StateMachine.Idle);
                break;
        }
    }

    private static void 探出()
    {
        当前相 = 相.探出中;
        _目标位置 = new Vector2I(探出位置X(当前侧, 可用屏(), 窗口宽(), 探出可见比例) + 该侧偏移(), _目标位置.Y);
        播($"{前缀()}-peek");
    }

    private static void 缩回()
    {
        当前相 = 相.缩回中;
        _目标位置 = new Vector2I(隐藏位置X(当前侧, 可用屏(), 窗口宽(), 可见比例) + 该侧偏移(), _目标位置.Y);
        播($"{前缀()}-unpeek");
    }

    /// <summary>由 StateMachine.应用表现 调用：按当前阶段播对应动画。</summary>
    public static void 应用表现()
    {
        switch (当前相)
        {
            case 相.缩进中: 播($"{前缀()}-in"); break;
            case 相.隐藏:   播(静止动画()); break;
            case 相.探出中: 播($"{前缀()}-peek"); break;
            case 相.已探出: 播(探出动画()); break;   // 循环探出动作（保持「活在探头」的状态）
            case 相.缩回中: 播($"{前缀()}-unpeek"); break;
            case 相.退出中: 播($"{前缀()}-out"); break;
        }
    }

    private static string 静止动画() => $"{前缀()}-keep";   // Main/B_1：4 帧微动循环（不是 -hold 单帧：那是静止画面，主人反馈「没动画」）
    private static string 探出动画() => $"{前缀()}-rise";   // Rise/B：**探出后的**微动循环（官方：鼠标进 → 循环 B；早先漏导 → 「探出没动画」）
    private static string 前缀() => 当前侧 == 侧.左 ? "edge_hide-left" : "edge_hide-right";

    /// <summary>该侧的微调偏移（修左右不对称）。
    /// 语义统一为「**正 = 往屏内多推**（露更多）、负 = 往屏外多推（藏更多）」：
    /// 左侧往屏内是 +X，右侧往屏内是 −X，所以右侧取负。</summary>
    private static int 该侧偏移() => 当前侧 == 侧.左 ? 左偏移像素 : -右偏移像素;

    private static void 播(string 动画名)
    {
        if (CharAnim.有动画(动画名))
        {
            CharAnim.PlayNamed(动画名);
            安排重播(动画名);
            return;
        }
        if (当前相 == 相.已探出) return; // 探出后本就不该重播：素材缺失也不报
        if (!_已报警告)
        {
            _已报警告 = true;
            GD.PrintErr($"[EdgeHide] 素材缺失：{动画名}（贴边隐藏已降级为纯窗口位移；检查 mods/main_anim/anim/loris/edge_hide/）");
        }
    }

    /// <summary>
    /// 循环类动画（`-keep` 隐藏保持 / `-rise` 探出微动）的**重播节拍**：一个周期内循环「循环次数」次。
    /// 主人要求「每两秒循环两次贴边动画」→ 周期 2s / 循环 2 次 / 内间隔 0.5s：
    /// 播 → 0.5s 后再播 → 再等 1.5s 进下一周期（每两秒循环两次贴边动画）。
    /// 循环次数 = 1 时退化为老行为（每周期播一次）。
    /// </summary>
    private static void 安排重播(string 动画名)
    {
        var 循环 = 动画名.EndsWith("-keep") || 动画名.EndsWith("-rise");
        if (!循环) { _重播计时 = 0f; _本轮剩余 = 0; return; }
        探针_循环播次数++;
        if (_本轮剩余 > 0)
        {
            _本轮剩余--;
            _重播计时 = _本轮剩余 > 0
                ? 循环内间隔秒
                : Math.Max(0.1f, 循环间隔秒 - (循环次数 - 1) * 循环内间隔秒);
        }
        else
        {
            _本轮剩余 = Math.Max(0, 循环次数 - 1);
            _重播计时 = _本轮剩余 > 0 ? 循环内间隔秒 : Math.Max(0.1f, 循环间隔秒);
        }
    }

    // ================= 探针专用 =================

    public static 相 探针_相 => 当前相;
    public static Vector2I 探针_目标位置 => _目标位置;
    public static Vector2I 探针_原位 => _原位;

    /// <summary>循环动画（-keep/-peek）已经播了几次 —— 探针用它断言「每 2 秒才播放一次」的节拍。</summary>
    public static int 探针_循环播次数 { get; set; }

    /// <summary>探针：剩余多少秒后重播循环动画。</summary>
    public static float 探针_重播计时 => _重播计时;

    /// <summary>探针：直接进入某阶段（跳过拖拽前置），用于验证阶段机与动画。</summary>
    public static void 探针_强制阶段(侧 侧, 相 相)
    {
        当前侧 = 侧;
        当前相 = 相;
        _原位 = DisplayServer.WindowGetPosition();
    }

    /// <summary>探针：只改阶段、**不动 _原位**（用于精确复现「拖走后松手」这类场景）。</summary>
    public static void 探针_设相(相 相) { 当前相 = 相; }

    public static void 探针_重置()
    {
        当前相 = 相.无;
        _悬停离开计时 = 0f;
        _重播计时 = 0f;
        _本轮剩余 = 0;
        _已报警告 = false;
    }
}