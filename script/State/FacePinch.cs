using System;
using desktop.script.Soul;
using desktop.script.UX;
using Godot;

namespace desktop.script.State;

/// <summary>
/// **捏脸（摸 / 拉脸）** —— 照 VPet 官方实现抄的（`MainWindow.DisplayPinch` + 教程「11/24 Update Pinch Face」）。三条对齐：
/// <list type="number">
/// <item>**触发 = 长按脸**：官方 `presslength` 默认 **300ms**（`Setting.cs`），到点后遍历「长按触摸区」——捏脸区被
/// `Insert(0, …)` 插到最前（优先级最高）。命中区在官方 `.lps` 里是 `pinch: px#149 py#128 sw#56 sh#59`（500 空间）。</item>
/// <item>**动画三段**：`A_Start`(1帧 进入) → `B_Loop`(6帧 **循环**，按住时连续播：官方 `DisplayPinch_loop` 播完 B 再播 B)
/// → 松手播 `C`(21帧 退出)。</item>
/// </list>
/// <para>
/// **数值效果不抄**（主人 2026-09-19：「我们没有设计体力和心情啊，你不要啥都抄」）：官方每次循环会
/// `Strength-2 / Feeling+1`，那是 VPet 自己的体力/心情系统；我们的数值是 心情/精力/亲密（`StatsTable`），
/// 捏脸**只做动作**，不挂钩任何数值。
/// </para>
/// <para>
/// **命中区怎么来的**（可核对）：官方 500 空间 → 素材(1000) = ×2 → `(298..410, 256..374)`；
/// 素材 → 帧(512) = ×0.5116 + 基线偏移（站立姿势 off≈(9, 2.8)）→ `(161..219, 134..194)`；
/// 帧 → 窗口 = ×0.5 → `(81..109, 67..97)`（窗口 256）→ 换算成比例 = `0.315 / 0.261 / 0.427 / 0.379`。
/// 想微调就在 `config/behavior.json` 写 `捏脸命中区`（窗口宽高的比例，四元数组）。
/// </para>
/// <para>与拖拽的分工（和官方一致）：**按住不动到 300ms = 捏脸**；**一按就跑 = 拖窗口**；**按下即松 = 摸摸**。</para>
/// </summary>
public static class FacePinch
{
    public enum 相 { 无, 按住中, 捏脸中, 退出中 }

    public static 相 当前相 { get; private set; } = 相.无;
    public static bool 占用中 => 当前相 != 相.无;

    /// <summary>本次按压是否已经变成捏脸（WindowDrag 用它抑制「摸摸」）。</summary>
    public static bool 本次已捏 { get; private set; }

    // —— 配置（behavior.json；由 StateMachine.设置 注入） ——
    /// <summary>长按阈值（秒）。官方 `presslength` 默认 300ms。</summary>
    public static float 长按秒 { get; set; } = 0.3f;
    /// <summary>命中区（窗口宽高的比例：x0, y0, x1, y1）。默认见类注释的换算。</summary>
    public static float[] 命中区 { get; set; } = { 0.315f, 0.261f, 0.427f, 0.379f };

    private static float _按住秒;

    // 探针
    public static int 探针_捏脸次数 { get; private set; }
    public static float 探针_按住秒 => _按住秒;

    public static void 探针_重置()
    {
        当前相 = 相.无; 本次已捏 = false; _按住秒 = 0f;
        探针_捏脸次数 = 0;
    }

    // ================= 纯函数（探针可直接断言） =================

    /// <summary>窗口内坐标是否落在「脸」上（命中区是窗口宽高的比例）。</summary>
    public static bool 脸区命中(Vector2 局部, Vector2I 窗口尺寸) =>
        desktop.script.Util.HitRegion.命中(命中区, 局部, 窗口尺寸);

    // ================= 输入（WindowDrag 调用） =================

    /// <summary>按下：命中脸区才算「长按候选」。返回是否命中。</summary>
    public static bool 按下(Vector2 局部, Vector2I 窗口尺寸)
    {
        if (当前相 != 相.无) return false;
        if (EdgeHide.占用中) return false;          // 贴边隐藏时不捏（半截在屏外，别抢状态）
        if (!脸区命中(局部, 窗口尺寸)) return false;
        当前相 = 相.按住中;
        本次已捏 = false;
        _按住秒 = 0f;
        return true;
    }

    /// <summary>每帧推进长按计时（由 StateMachine._Process 调）。</summary>
    public static void 每帧(float delta)
    {
        if (当前相 != 相.按住中) return;
        _按住秒 += delta;
        if (_按住秒 >= 长按秒) 触发();
    }

    /// <summary>松手：未触发 → 清掉候选；捏脸中 → 播 C 退出。</summary>
    public static void 松手()
    {
        switch (当前相)
        {
            case 相.按住中:
                当前相 = 相.无;
                _按住秒 = 0f;
                break;
            case 相.捏脸中:
                当前相 = 相.退出中;
                GD.Print("[FacePinch] 松手 → 播退出段");
                播(退出动画());
                break;
        }
    }

    /// <summary>取消（开始拖拽 / 拖走 / 面板接管）：清相、不动表现（调用方负责）。</summary>
    public static void 取消()
    {
        if (当前相 == 相.无) return;
        GD.Print($"[FacePinch] 取消（{当前相}）");
        当前相 = 相.无;
        _按住秒 = 0f;
    }

    // ================= 状态机回调 =================

    /// <summary>动画播完（StateMachine 转发）：捏脸中 → 继续循环 B（官方 `DisplayPinch_loop`）；退出中 → 回 idle。</summary>
    public static void 动画播完()
    {
        switch (当前相)
        {
            case 相.捏脸中:
                // 官方在这里每循环一次扣体力/加心情（Strength-2 / Feeling+1）——**我们不抄**：
                // 数值归我们自己的系统（心情/精力/亲密），捏脸只做动作，不挂钩任何数值。
                播(循环动画());
                break;
            case 相.退出中:
                当前相 = 相.无;
                StateMachine.SetState(StateMachine.Idle);
                break;
        }
    }

    /// <summary>由 StateMachine.应用表现 调用。</summary>
    public static void 应用表现()
    {
        switch (当前相)
        {
            case 相.捏脸中: 播(循环动画()); break;
            case 相.退出中: 播(退出动画()); break;
            // 相.按住中：还没触发，保持原样（不切动作）
        }
    }

    // ================= 内部 =================

    private static void 触发()
    {
        当前相 = 相.捏脸中;
        本次已捏 = true;
        探针_捏脸次数++;
        GD.Print($"[FacePinch] 长按 {长按秒:0.0}s → 捏脸");
        StateMachine.SetState(StateMachine.PinchState);
        EventPool.记("捏脸", EventPool.归属.程序, "主人捏了我的脸（长按触发）");
        StateMachine.本地说话("捏脸", 5f);
        播(进入动画());
    }

    // —— 动画名：固定用 **Nomal** 三段 ——
    // 官方按它自己的 Mode 选 Happy/Nomal/PoorCondition；**我们的心情定义与官方不同，不做这个映射**
    // （主人 2026-09-19：「我们心情的定义和官方是不一样的」）。
    private static string 进入动画() => "pinch-a";
    private static string 循环动画() => "pinch-b";
    private static string 退出动画() => "pinch-c";
    private static void 播(string 动画名)
    {
        if (CharAnim.有动画(动画名)) { CharAnim.PlayNamed(动画名); return; }
        var 兜底 = "pinch-" + 动画名[^1];                    // happy 变体缺 → 退回 nomal
        if (CharAnim.有动画(兜底)) { CharAnim.PlayNamed(兜底); return; }
        GD.PrintErr($"[FacePinch] 素材缺失：{动画名}（检查 mods/main_anim/anim/loris/pinch/）");
    }
}
