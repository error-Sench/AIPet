using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace desktop.script.Soul;

/// <summary>
/// 数值层（P5）：`心情 / 精力 / 亲密`——桌宠自己的「身体感受」，与人格（soul.md）**分开存**。
/// <para>
/// 设计要点（主人决策）：
/// 1. **数值不是灵魂**。soul.md 只放人格；数值独立成 `user://stats.json`，可可视化、可随手改。
/// 2. **有惯性**：数值随时间自然漂移（心情向基线回落、精力随时间消耗），不是只有互动才变。
/// 3. **离线也要活着**：存盘带时间戳，下次启动按「离开了多久」补算离线漂移——你不在时它也会累、也会想你。
/// 4. **驱动表达而非驱动人格**：数值只影响**表现与主动行为的频率**，绝不改写人格与说话方式（那归 soul.md）。
/// </para>
/// 约定：标识符英文，注释中文（见 AGENTS.md §8）。
/// </summary>
public static class StatsTable
{
    public const string 心情 = "mood";
    public const string 精力 = "energy";
    public const string 亲密 = "affection";

    /// <summary>数值区间（亲密上限更高，因为它只增不减）。</summary>
    private const int 心情上限 = 100;
    private const int 精力上限 = 100;
    private const int 亲密上限 = 999;

    /// <summary>心情的自然基线：不互动时会慢慢回落到这里（不是回落到底）。</summary>
    private const float 心情基线 = 60f;

    // —— 漂移速率 ——
    private const float 心情回落速率 = 4f;   // 每小时向基线靠拢的**点数**（线性，可预测：差 30 点 → 约 7.5 小时走完）
    private const float 精力消耗速率 = 3f;   // 清醒时每小时消耗
    private const float 睡眠恢复速率 = 14f;  // 睡眠时每小时回充

    // —— 事件增量 ——
    private const int 摸加心情 = 6;
    private const int 对话加心情 = 4;
    private const int 打招呼加心情 = 2;
    private const int 摸加亲密 = 1;
    private const int 对话加亲密 = 1;
    private const int 走动耗精力 = 1;

    /// <summary>摸头加心情的节流（秒）：防止主人按住狂摸把心情刷满。</summary>
    private const float 摸节流秒 = 60f;

    public static float 当前心情 { get; private set; } = 心情基线;
    public static float 当前精力 { get; private set; } = 80f;
    public static float 当前亲密 { get; private set; }

    /// <summary>数值变化事件（参数为变化后的快照字符串），供 UI/日志订阅。</summary>
    public static event Action<string> StatsChanged;

    public static int 心情整 => (int)MathF.Round(当前心情);
    public static int 精力整 => (int)MathF.Round(当前精力);
    public static int 亲密整 => (int)MathF.Round(当前亲密);

    /// <summary>一句话状态（供 UI 显示，不做人格判断）。</summary>
    public static string 概述 => $"心情 {心情整} · 精力 {精力整} · 亲密 {亲密整}";

    private static bool _已载入;
    private static double _存盘累计;
    private static float _摸冷却;
    private static readonly string 存盘路径 = "user://stats.json";

    // ================= 载入 / 存盘 =================

    /// <summary>载入（含按离线时长补算漂移）。重复调用只生效一次。</summary>
    public static void 载入()
    {
        if (_已载入) return;
        _已载入 = true;
        try
        {
            var 全路径 = ProjectSettings.GlobalizePath(存盘路径);
            if (System.IO.File.Exists(全路径))
            {
                var 文档 = JsonDocument.Parse(System.IO.File.ReadAllText(全路径));
                var r = 文档.RootElement;
                if (r.TryGetProperty("mood", out var m)) 当前心情 = m.GetSingle();
                if (r.TryGetProperty("energy", out var e)) 当前精力 = e.GetSingle();
                if (r.TryGetProperty("affection", out var a)) 当前亲密 = a.GetSingle();

                // 离线漂移：按「上次存盘到现在」的小时数补算（离线不清零、也不是原地不动）
                var 离线小时 = 0f;
                if (r.TryGetProperty("savedAtUnix", out var t))
                {
                    var 离线秒 = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t.GetInt64();
                    if (离线秒 > 60) 离线小时 = 离线秒 / 3600f;
                }
                漂移(离线小时, 睡眠中: false);
                GD.Print($"[Stats] 已载入（离线 {离线小时:0.0}h）→ {概述}");
            }
            else
            {
                GD.Print($"[Stats] 首次运行，用初始值 → {概述}");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Stats] 载入失败（用默认值继续）: {ex.Message}");
        }
        夹取并广播();
    }

    /// <summary>存盘（带时间戳，供下次补算离线漂移）。</summary>
    public static void 存盘()
    {
        try
        {
            var 数据 = new Dictionary<string, object>
            {
                ["mood"] = MathF.Round(当前心情, 2),
                ["energy"] = MathF.Round(当前精力, 2),
                ["affection"] = MathF.Round(当前亲密, 2),
                ["savedAtUnix"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["_comment"] = "桌宠数值（P5）。与人格 soul.md 分离：这里只放可变的感受，不放人格。",
            };
            System.IO.File.WriteAllText(ProjectSettings.GlobalizePath(存盘路径),
                JsonSerializer.Serialize(数据, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { GD.PrintErr($"[Stats] 存盘失败: {ex.Message}"); }
    }

    // ================= 心跳（由 StateMachine 的心跳统一驱动） =================

    /// <summary>节律驱动：每帧推进（内部按小时速率换算），每 30s 自动存盘一次。</summary>
    public static void 心跳(float 秒, bool 睡眠中)
    {
        if (!_已载入) 载入();

        漂移(秒 / 3600f, 睡眠中);

        if (_摸冷却 > 0f) _摸冷却 = MathF.Max(0f, _摸冷却 - 秒);

        _存盘累计 += 秒;
        if (_存盘累计 >= 30.0)
        {
            _存盘累计 = 0;
            存盘();
        }
    }

    private static void 漂移(float 小时, bool 睡眠中)
    {
        if (小时 <= 0f) return;

        // 心情：**线性**向基线靠拢（差多少走多少，每小时走 心情回落速率 点，不会过冲）
        var 差 = 心情基线 - 当前心情;
        当前心情 += Math.Clamp(差, -心情回落速率 * 小时, 心情回落速率 * 小时);

        // 精力：睡觉回充，清醒消耗
        当前精力 += (睡眠中 ? 睡眠恢复速率 : -精力消耗速率) * 小时;
    }

    // ================= 事件（互动带来的变化） =================

    /// <summary>被摸摸。</summary>
    public static void 事件_摸摸()
    {
        if (_摸冷却 > 0f)
        {
            GD.Print($"[Stats] 摸摸（节流中 {_摸冷却:0}s，只加亲密）");
            当前亲密 += 摸加亲密;
            夹取并广播();
            return;
        }
        _摸冷却 = 摸节流秒;
        当前心情 += 摸加心情;
        当前亲密 += 摸加亲密;
        夹取并广播("摸摸");
    }

    /// <summary>与 Agent 对话（一轮）。</summary>
    public static void 事件_对话()
    {
        当前心情 += 对话加心情;
        当前亲密 += 对话加亲密;
        夹取并广播("对话");
    }

    /// <summary>打招呼/被唤醒。</summary>
    public static void 事件_打招呼()
    {
        当前心情 += 打招呼加心情;
        夹取并广播("打招呼");
    }

    /// <summary>走动一次（消耗精力）。</summary>
    public static void 事件_走动()
    {
        当前精力 -= 走动耗精力;
        夹取并广播();
    }

    /// <summary>外部设定心情（Agent 指令 `set_mood` 走这里）。</summary>
    public static void 设心情(float 值, bool 直接 = true)
    {
        if (直接) 当前心情 = 值;
        else 当前心情 += 值;
        夹取并广播("外部设定");
    }

    /// <summary>当前是否「心情低落」（供状态机降低主动行为频率用）。</summary>
    public static bool 心情低落 => 当前心情 < 30f;

    /// <summary>心情 → 文字状态（界面上不出现任何数字 —— 主人决策）。UI 与上下文接口共用这一份，避免两处漂移。</summary>
    public static string 心情文字(float 值) => 值 switch
    {
        >= 85f => "超开心",
        >= 70f => "心情不错",
        >= 50f => "平平静静",
        >= 35f => "有点蔫",
        >= 20f => "不太开心",
        _ => "很低落",
    };

    /// <summary>当前心情的文字状态。</summary>
    public static string 当前心情文字 => 心情文字(当前心情);

    /// <summary>当前是否「精力不济」。</summary>
    public static bool 精力不济 => 当前精力 < 20f;

    private static void 夹取并广播(string 来源 = "")
    {
        当前心情 = Math.Clamp(当前心情, 0f, 心情上限);
        当前精力 = Math.Clamp(当前精力, 0f, 精力上限);
        当前亲密 = Math.Clamp(当前亲密, 0f, 亲密上限);
        if (来源.Length > 0) GD.Print($"[Stats] {来源} → {概述}");
        StatsChanged?.Invoke(概述);
    }

    // ================= 探针专用 =================

    /// <summary>探针：直接注入数值（跳过节流/事件），并强制夹取。</summary>
    public static void 探针_设值(float 心情值, float 精力值, float 亲密值)
    {
        当前心情 = 心情值; 当前精力 = 精力值; 当前亲密 = 亲密值;
        夹取并广播();
    }

    /// <summary>探针：把节流冷却清零，便于连续测事件。</summary>
    public static void 探针_清节流() => _摸冷却 = 0f;

    /// <summary>探针：按小时做一次漂移（不存档）。</summary>
    public static void 探针_漂移(float 小时, bool 睡眠中) => 漂移(小时, 睡眠中);

    /// <summary>探针：重置载入标志（便于测试载入路径）。</summary>
    public static void 探针_重置载入标志() { _已载入 = false; _存盘累计 = 0; }

    /// <summary>探针：当前存盘文件路径（已全局化）。</summary>
    public static string 探针_存盘路径 => ProjectSettings.GlobalizePath(存盘路径);
}