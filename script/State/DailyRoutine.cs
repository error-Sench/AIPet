using System;
using System.Collections.Generic;
using System.IO;
using desktop.script.UX;
using Godot;

namespace desktop.script.State;

/// <summary>
/// **时间驱动的主动行为**（Plan #11）——两部分：
/// ① **当天首次见面问好**（按时间段；深夜那句本质还是「我注意到你还在」）；
/// ② **主人不易察觉的系统状态**（磁盘余量悄悄变满）——这类才值得提醒。
/// <para>
/// **提醒的取舍原则（主人 2026-09-19 定，硬规则）**：
/// **只提醒「主人自己不容易注意到的事」**——比如磁盘在悄悄变满、连续坐着忘了时间（久坐提醒在 <see cref="StateMachine"/> 里）。
/// **不做**「主人自己知道的事」：喝水、吃饭、该睡了这类**生活管家式提醒**一律不做
/// （主人原话：「这种喝水提醒什么的不用写吧」）——桌宠不是健康 App，功能再多，不像它就是在扣分。
/// 加新提醒前先过这一条：**这件事主人会自己意识到吗？会 → 不做。**
/// </para>
/// <para>闸门：一天一次（问候＋磁盘各自）+ 问候需「距上次交互 ≥ 60s 且当前 idle」+ 全部受**每小时主动预算**约束；
/// 闸门关着时先记下，等不打扰窗口到了再补报（不硬闯）。</para>
/// <para>配置在 `config/behavior.json`：`问候启用` / `磁盘提醒启用` / `磁盘剩余下限GB`。</para>
/// </summary>
public static class DailyRoutine
{
    // ================= 配置（由 StateMachine.设置 加载后注入） =================

    /// <summary>当天第一次见到主人 → 按时间段问好。</summary>
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
    private static string _上次问候日 = "";
    private static bool _待问候;               // 已排上、等回到 idle 再兑现
    private static float _待问候计时;
    private static string _上次磁盘检查日 = "";
    private static string _上次磁盘提醒日 = "";
    private static bool _磁盘待提醒;

    // 探针
    public static int 探针_问候次数 { get; private set; }
    public static int 探针_磁盘提醒次数 { get; private set; }
    public static string 探针_最近语句 { get; private set; } = "";
    public static bool 探针_待问候 => _待问候;
    public static bool 探针_磁盘待提醒 => _磁盘待提醒;
    public static string 探针_上次问候日 => _上次问候日;
    public static float 探针_距上次交互秒 => _距上次交互秒;

    public static void 探针_重置()
    {
        探针_问候次数 = 0; 探针_磁盘提醒次数 = 0; 探针_最近语句 = "";
        _距上次交互秒 = 无交互; _上次问候日 = ""; _待问候 = false; _待问候计时 = 0f;
        _上次磁盘检查日 = ""; _上次磁盘提醒日 = ""; _磁盘待提醒 = false;
        时钟 = () => DateTime.Now;
        探针_磁盘剩余字节 = null;
    }

    // ================= 纯函数（探针可直接断言） =================

    /// <summary>按时间段挑问候语。纯函数。</summary>
    public static (string 时段, string 语句) 问候语(DateTime 现在)
    {
        var 时 = 现在.Hour;
        if (时 is >= 5 and < 11) return ("早上", 抽("早上好呀～", "早安，今天也一起加油吧～", "早呀，睡得好吗？"));
        if (时 is >= 11 and < 14) return ("中午", 抽("中午好～", "到饭点啦，我在这儿等你回来～"));
        if (时 is >= 14 and < 18) return ("下午", 抽("下午好～", "下午呀，我在这儿陪着～"));
        if (时 is >= 18 and < 23) return ("晚上", 抽("晚上好～", "晚上好呀，今天过得怎么样？"));
        return ("深夜", 抽("这么晚还在呀，我陪着你～", "夜深啦，我一直都在哦～", "这么晚了还没休息呀，我陪你到收工～"));
    }

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

    // ================= 交互入口 =================

    /// <summary>任何交互都会调用（由 `StateMachine.NotifyInteraction` 转发）：排上「当天首次见面」的问候。</summary>
    public static void 交互()
    {
        var 距上次 = _距上次交互秒;
        _距上次交互秒 = 0f;
        if (!问候启用 || !StateMachine.设置.启用) return;
        if (距上次 < 60f) return;                                   // 会话进行中，不打断
        if (StateMachine.CurrentState != StateMachine.Idle) return;  // 忙态/睡觉（唤醒流程有自己的招呼）
        if (_上次问候日 == 时钟().ToString("yyyy-MM-dd")) return;      // 一天一次
        if (StateMachine.主动预算剩余值 <= 0) return;                  // 受每小时主动预算约束

        _待问候 = true;   // 只排上：不抢这次互动的反应（摸头要先把动作播完）
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

    /// <summary>兑现排上的问候：等互动反应播完（回到 idle）且预算还够。等太久（120s）就作废。</summary>
    private static void 问候兑现(float 秒)
    {
        if (!_待问候) return;
        _待问候计时 += 秒;
        if (_待问候计时 > 120f || !问候启用) { _待问候 = false; return; }
        if (StateMachine.CurrentState != StateMachine.Idle) return;   // 互动反应还没播完
        if (StateMachine.主动预算剩余值 <= 0) return;

        var 现在 = 时钟();
        _待问候 = false;
        _上次问候日 = 现在.ToString("yyyy-MM-dd");
        var (时段, 语句) = 问候语(现在);
        探针_问候次数++;
        探针_最近语句 = 语句;
        冒泡($"问候（{时段}）", "问候", 语句);
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
        var 描述 = 磁盘描述();
        探针_磁盘提醒次数++;
        探针_最近语句 = $"{描述}，要不要清一清？";
        冒泡("磁盘空间低", "磁盘空间低", $"{描述}，要不要清一清？");
    }

    /// <summary>统一出口：占预算 + 记事件 + 冒泡 + 打招呼姿态（2.5s 回 idle）。</summary>
    private static void 冒泡(string 标签, string 事件类型, string 语句)
    {
        StateMachine.占一次主动预算();
        GD.Print($"[DailyRoutine] {标签}：{语句}");
        EventPool.记(事件类型, EventPool.归属.程序, 语句);
        Dialogue.显示临时标题(语句, 5000);
        StateMachine.SetState(StateMachine.Greet);
    }

    private static string 抽(params string[] 候选) => 候选[Random.Shared.Next(候选.Length)];

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

    private static string 磁盘描述()
    {
        if (探针_磁盘剩余字节 != null) return $"磁盘只剩 {探针_磁盘剩余字节() / 1024.0 / 1024 / 1024:0.#} GB";
        foreach (var 根 in 关心盘符())
        {
            try
            {
                var 盘 = new DriveInfo(根);
                if (!盘.IsReady) continue;
                var 余 = 盘.AvailableFreeSpace;
                if (余 >= 0 && 磁盘算低(余)) return $"{根.TrimEnd('\\')} 盘只剩 {余 / 1024.0 / 1024 / 1024:0.#} GB";
            }
            catch { /* 忽略 */ }
        }
        return "磁盘快满了";
    }
}
