using System;
using System.Collections.Generic;
using System.IO;
using desktop.script.UX;
using Godot;

namespace desktop.script.State;

/// <summary>
/// **时间驱动的主动行为**（Plan #11）——两部分：
/// ① **启动问候**（主人 2026-09-19 改的口径：**每次启动打一次招呼**，替换原「当天首次见面」）；
/// ② **主人不易察觉的系统状态**（磁盘余量悄悄变满）——这类才值得提醒。
/// <para>
/// **提醒的取舍原则（主人 2026-09-19 定，硬规则）**：
/// **只提醒「主人自己不容易注意到的事」**——比如磁盘在悄悄变满、连续坐着忘了时间（久坐提醒在 <see cref="StateMachine"/> 里）。
/// **不做**「主人自己知道的事」：喝水、吃饭、该睡了这类**生活管家式提醒**一律不做
/// （主人原话：「这种喝水提醒什么的不用写吧」）——桌宠不是健康 App，功能再多，不像它就是在扣分。
/// 加新提醒前先过这一条：**这件事主人会自己意识到吗？会 → 不做。**
/// </para>
/// <para>
/// 闸门：**每次启动一次**（问候）+ 每天一次（磁盘）+ 受「不打扰」约束（忙态等回到 idle 再兑现）；
/// **启动问候不占「每小时主动预算」**——它是「我出现了」的招呼，不算主动打扰；磁盘提醒占预算。
/// </para>
/// <para>
/// **说什么由话语表定**：`config/phrases.json`（<see cref="Soul.PhraseTable"/>）——没接 Agent 时桌宠靠它说话。
/// 配置在 `config/behavior.json`：`问候启用` / `磁盘提醒启用` / `磁盘剩余下限GB`。
/// </para>
/// </summary>
public static class DailyRoutine
{
    // ================= 配置（由 StateMachine.设置 加载后注入） =================

    /// <summary>启动问候：**每次启动打一次招呼**（按时间段挑话）。</summary>
    public static bool 问候启用 = true;
    /// <summary>磁盘余量低 → 提醒（每天最多一次）。</summary>
    public static bool 磁盘提醒启用 = true;
    /// <summary>低于这个余量算「快满了」（GB）。</summary>
    public static int 磁盘剩余下限GB = 10;

    /// <summary>可注入时钟（探针用）；默认系统本地时间。</summary>
    public static Func<DateTime> 时钟 = () => DateTime.Now;
    /// <summary>探针：磁盘余量覆写（null = 读真实磁盘）。</summary>
    public static Func<long> 探针_磁盘剩余字节;

    // ================= 运行状态 =================

    private const float 无交互 = 99999f;
    private static float _距上次交互秒 = 无交互;
    private static bool _启动已问候;           // 「每次启动一次」的兑现标记
    private static bool _待问候;               // 已排上、等回到 idle 再兑现
    private static bool _待问候_启动;          // 这条是启动问候（不占预算）
    private static float _待问候计时;
    private static string _上次磁盘检查日 = "";
    private static string _上次磁盘提醒日 = "";
    private static bool _磁盘待提醒;

    // 探针
    public static int 探针_问候次数 { get; private set; }
    public static int 探针_磁盘提醒次数 { get; private set; }
    public static string 探针_最近语句 { get; private set; } = "";
    public static bool 探针_待问候 => _待问候;
    public static bool 探针_启动已问候 => _启动已问候;
    public static bool 探针_磁盘待提醒 => _磁盘待提醒;
    public static float 探针_距上次交互秒 => _距上次交互秒;

    public static void 探针_重置()
    {
        探针_问候次数 = 0; 探针_磁盘提醒次数 = 0; 探针_最近语句 = "";
        _距上次交互秒 = 无交互; _启动已问候 = false;
        _待问候 = false; _待问候_启动 = false; _待问候计时 = 0f;
        _上次磁盘检查日 = ""; _上次磁盘提醒日 = ""; _磁盘待提醒 = false;
        时钟 = () => DateTime.Now;
        探针_磁盘剩余字节 = null;
    }

    // ================= 纯函数（探针可直接断言） =================

    /// <summary>按时间段挑问候语（走话语表 `config/phrases.json`；表里没有这一段 → 内置兜底）。纯函数。</summary>
    public static (string 时段, string 语句) 问候语(DateTime 现在) => Soul.PhraseTable.问候(现在);

    /// <summary>磁盘余量是否偏低。纯函数。</summary>
    public static bool 磁盘算低(long 剩余字节) => 剩余字节 < (long)Math.Max(0, 磁盘剩余下限GB) * 1024 * 1024 * 1024;

    /// <summary>盘符（系统盘 + 程序所在盘，去重）。</summary>
    public static List<string> 关心盘符()
    {
        var 结果 = new List<string>();
        foreach (var 路径 in new[] { System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows), AppContext.BaseDirectory })
        {
            try
            {
                if (string.IsNullOrWhiteSpace(路径)) continue;
                var 根 = Path.GetPathRoot(路径);
                if (!string.IsNullOrWhiteSpace(根) && !结果.Contains(根)) 结果.Add(根);
            }
            catch { /* 忽略坏路径 */ }
        }
        return 结果;
    }

    // ================= 入口 =================

    /// <summary>任何交互都会调用（由 `StateMachine.NotifyInteraction` 转发）——**只用于「距上次交互」计时**。
    /// 问候已改成**启动时**打招呼（见 <see cref="启动问候"/>），不再靠交互触发。</summary>
    public static void 交互() => _距上次交互秒 = 0f;

    /// <summary>
    /// **启动问候**：每次启动打一次招呼（由 `StateMachine.入场完成` 在入场动画播完后调用）。
    /// 只**排上**，真正冒泡等回到 idle（不抢入场/打招呼的姿态）；一次进程只兑现一次。
    /// </summary>
    public static void 启动问候()
    {
        if (_启动已问候) return;
        if (!问候启用 || !StateMachine.设置.启用) return;
        _待问候 = true;
        _待问候_启动 = true;
        _待问候计时 = 0f;
    }

    // ================= 心跳（由 StateMachine.心跳 每拍调用） =================

    /// <summary>推进计时（计时始终推进；说不说另受闸门约束）。</summary>
    public static void 推进(float 秒)
    {
        if (_距上次交互秒 < 无交互) _距上次交互秒 += 秒;
        问候兑现(秒);
        磁盘检查(秒);
    }

    /// <summary>兑现排上的问候：等回到 idle（入场/互动反应播完）。等太久（120s）就作废。</summary>
    private static void 问候兑现(float 秒)
    {
        if (!_待问候) return;
        _待问候计时 += 秒;
        if (_待问候计时 > 120f || !问候启用) { _待问候 = false; _待问候_启动 = false; return; }
        if (StateMachine.CurrentState != StateMachine.Idle) return;   // 还在播入场/反应
        var 启动 = _待问候_启动;
        if (!启动 && StateMachine.主动预算剩余值 <= 0) return;         // 启动问候不占预算：不受这条拦

        var 现在 = 时钟();
        _待问候 = false;
        _待问候_启动 = false;
        _启动已问候 = true;
        var (时段, 语句) = 问候语(现在);
        探针_问候次数++;
        探针_最近语句 = 语句;
        冒泡($"问候（{时段}）", "问候", 语句, 占预算: !启动);
    }

    /// <summary>磁盘：每天查一次余量；偏低就记下，等不打扰窗口到了再冒泡（不硬闯）。</summary>
    private static void 磁盘检查(float 秒)
    {
        if (!磁盘提醒启用) return;
        var 今天 = 时钟().ToString("yyyy-MM-dd");
        if (_上次磁盘检查日 != 今天)
        {
            _上次磁盘检查日 = 今天;
            _磁盘待提醒 = 磁盘偏低();
            if (_磁盘待提醒) GD.Print("[DailyRoutine] 磁盘余量偏低 → 待提醒（等不打扰闸门）");
        }
        if (!_磁盘待提醒 || _上次磁盘提醒日 == 今天) return;
        if (!StateMachine.主动闸门开放) return;

        _磁盘待提醒 = false;
        _上次磁盘提醒日 = 今天;
        var (盘, 余量) = 磁盘信息();
        var 语句 = Soul.PhraseTable.取("磁盘", ("盘", 盘), ("余量", 余量));
        if (string.IsNullOrEmpty(语句)) 语句 = $"{盘} 盘只剩 {余量} GB，要不要清一清？";
        探针_磁盘提醒次数++;
        探针_最近语句 = 语句;
        冒泡("磁盘空间低", "磁盘空间低", 语句);
    }

    /// <summary>统一出口：占预算 + 记事件 + 冒泡 + 打招呼姿态（2.5s 回 idle）。</summary>
    private static void 冒泡(string 标签, string 事件类型, string 语句, bool 占预算 = true)
    {
        if (占预算) StateMachine.占一次主动预算();
        GD.Print($"[DailyRoutine] {标签}：{语句}");
        EventPool.记(事件类型, EventPool.归属.程序, 语句);
        Dialogue.显示临时标题(语句);
        StateMachine.SetState(StateMachine.Greet);
    }

    private static bool 磁盘偏低()
    {
        if (探针_磁盘剩余字节 != null) return 磁盘算低(探针_磁盘剩余字节());
        foreach (var 根 in 关心盘符())
        {
            try
            {
                var 盘 = new DriveInfo(根);
                if (!盘.IsReady) continue;
                var 余 = 盘.AvailableFreeSpace;
                if (余 >= 0 && 磁盘算低(余)) return true;
            }
            catch { /* 忽略读不到的盘 */ }
        }
        return false;
    }

    /// <summary>「哪个盘 + 剩多少」两段文本（给话语表的 {盘} / {余量} 占位符）。</summary>
    private static (string 盘, string 余量) 磁盘信息()
    {
        if (探针_磁盘剩余字节 != null)
        {
            var gb = 探针_磁盘剩余字节() / 1024.0 / 1024 / 1024;
            return ("系统", $"{gb:0.#}");
        }
        foreach (var 根 in 关心盘符())
        {
            try
            {
                var 盘 = new DriveInfo(根);
                if (!盘.IsReady) continue;
                var 余 = 盘.AvailableFreeSpace;
                if (余 >= 0 && 磁盘算低(余))
                    return (根.TrimEnd('\\'), $"{余 / 1024.0 / 1024 / 1024:0.#}");
            }
            catch { /* 忽略 */ }
        }
        return ("系统", "?");
    }
}
