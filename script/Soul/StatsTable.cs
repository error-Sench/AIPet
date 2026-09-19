using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace desktop.script.Soul;

/// <summary>
/// 数值层：**`mood` = 主人情绪读数**（0–100，中性 50）。
/// <para>
/// 设计要点（主人 2026-09-20 定稿，对齐 idea.md §2 / §4.3 / §4.5）：
/// 1. **只影响回复策略，不加入互动**：数值不因摸摸/对话/打招呼等互动变化，也不驱动动画/行为；
///    它只作为「主人此刻情绪」的输入，供 Agent 决定怎么回应（策略在 Agent 侧）。
/// 2. **由 LLM 判断写入**：Agent 从对话里判断主人情绪 → 经指令通道 `set_mood` 写入；程序只负责**衰减与存储**。
/// 3. **衰减**：每 30 秒向中性（50）靠拢 5 点，防止过时情绪长期残留（§4.3）；离线按同一规则补算。
/// 4. **精力 / 亲密 / 等级等属 mod 扩展范围**，核心不实现（亲密归空置的「关系层」）。
/// </para>
/// 文件：`user://state/stats.json`（§2.2）。约定：标识符英文，注释中文（AIPet-Agent.md §8）。
/// </summary>
public static class StatsTable
{
    /// <summary>中性值：情绪倾向不明显时向它回落。</summary>
    public const float 中性 = 50f;
    private const float 下限 = 0f;
    private const float 上限 = 100f;

    /// <summary>衰减节拍：每 30 秒向中性靠拢 5 点（§4.3 原话）。</summary>
    private const float 衰减间隔秒 = 30f;
    private const float 衰减点数 = 5f;

    /// <summary>主人情绪读数（0–100）。</summary>
    public static float 当前心情 { get; private set; } = 中性;

    /// <summary>数值变化事件（参数为变化后的快照字符串），供 UI/日志订阅。</summary>
    public static event Action<string> StatsChanged;

    public static int 心情整 => (int)MathF.Round(当前心情);

    /// <summary>一句话状态（供日志/UI）。</summary>
    public static string 概述 => $"主人情绪 {心情整}/100 —— {当前心情文字}";

    private static bool _已载入;
    private static double _存盘累计;
    private static float _衰减累计;

    /// <summary>默认存盘文件（主人真实数据所在；§2.2：数值放 state/ 子目录）。</summary>
    public const string 默认存盘路径 = "user://state/stats.json";

    /// <summary>
    /// 探针用：临时覆盖存盘路径（**隔离测试，别碰主人的真实存档**）。
    /// 2026-09-20 修 bug：StatsProbe 曾在真实路径上写测试值、收尾 `File.Delete` 把主人的数值档删了
    /// （回归每跑一次删一次）。null = 用默认。
    /// </summary>
    public static string 探针_覆盖存盘路径;

    private static string 存盘路径 => 探针_覆盖存盘路径 ?? 默认存盘路径;

    // ================= 载入 / 存盘 =================

    /// <summary>载入（含按离线时长补算衰减）。重复调用只生效一次。</summary>
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

                // 离线补算：按「上次存盘到现在」经过了几个 30s 衰减节拍（离线久了自然回中性）
                var 离线秒 = 0d;
                if (r.TryGetProperty("savedAtUnix", out var t))
                    离线秒 = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t.GetInt64();
                向中性((float)(Math.Max(0d, 离线秒) / 衰减间隔秒) * 衰减点数);
                GD.Print($"[Stats] 已载入（离线 {离线秒 / 3600d:0.0}h）→ {概述}");
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

    /// <summary>存盘（带时间戳，供下次补算离线衰减）。</summary>
    public static void 存盘()
    {
        try
        {
            var 数据 = new Dictionary<string, object>
            {
                ["mood"] = MathF.Round(当前心情, 2),
                ["savedAtUnix"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["_comment"] = "主人情绪读数（0–100，中性 50）。由 Agent 经 set_mood 写入；程序只做衰减与存储。",
            };
            var 全路径 = ProjectSettings.GlobalizePath(存盘路径);
            var 目录 = System.IO.Path.GetDirectoryName(全路径);
            if (!string.IsNullOrEmpty(目录)) System.IO.Directory.CreateDirectory(目录);
            System.IO.File.WriteAllText(全路径,
                JsonSerializer.Serialize(数据, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { GD.PrintErr($"[Stats] 存盘失败: {ex.Message}"); }
    }

    // ================= 心跳（由 StateMachine 的心跳统一驱动） =================

    /// <summary>节律驱动：每帧推进衰减节拍，每 30s 自动存盘一次。</summary>
    public static void 心跳(float 秒)
    {
        if (!_已载入) 载入();

        _衰减累计 += 秒;
        while (_衰减累计 >= 衰减间隔秒)
        {
            _衰减累计 -= 衰减间隔秒;
            向中性(衰减点数);
        }

        _存盘累计 += 秒;
        if (_存盘累计 >= 30.0)
        {
            _存盘累计 = 0;
            存盘();
        }
    }

    /// <summary>向中性（50）靠拢最多 `点数` 点（不会过冲）。</summary>
    private static void 向中性(float 点数)
    {
        if (点数 <= 0f) return;
        var 差 = 中性 - 当前心情;
        当前心情 += Math.Clamp(差, -点数, 点数);
    }

    /// <summary>外部写入主人情绪读数（Agent 指令 `set_mood` 走这里；LLM 判断 → 程序存储）。</summary>
    public static void 设心情(float 值)
    {
        当前心情 = 值;
        夹取并广播("set_mood");
    }

    /// <summary>心情 → 文字状态（界面上不出现任何数字）。UI 与上下文接口共用这一份，避免两处漂移。</summary>
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

    private static void 夹取并广播(string 来源 = "")
    {
        当前心情 = Math.Clamp(当前心情, 下限, 上限);
        if (来源.Length > 0) GD.Print($"[Stats] {来源} → {概述}");
        StatsChanged?.Invoke(概述);
    }

    // ================= 探针专用 =================

    /// <summary>
    /// 探针：直接注入数值（跳过衰减），并强制夹取。
    /// **同时置「已载入」**：防止第一次心跳（帧 1）的懒载入把探针钉好的值覆盖回存档
    /// （2026-09-20 修：InteractProbe 帧 0 钉 60 后被帧 1 的懒载入改回 82 → 断言随机失败）。
    /// </summary>
    public static void 探针_设值(float 心情值)
    {
        _已载入 = true;
        当前心情 = 心情值;
        夹取并广播();
    }

    /// <summary>探针：推进衰减 `秒` 秒（不存档），便于断言「每 30s 回 5 点」。</summary>
    public static void 探针_衰减(float 秒)
    {
        _衰减累计 += 秒;
        while (_衰减累计 >= 衰减间隔秒)
        {
            _衰减累计 -= 衰减间隔秒;
            向中性(衰减点数);
        }
        夹取并广播();
    }

    /// <summary>探针：重置载入标志与计时（便于测试载入路径）。</summary>
    public static void 探针_重置载入标志() { _已载入 = false; _存盘累计 = 0; _衰减累计 = 0; }

    /// <summary>探针：当前存盘文件路径（已全局化）。</summary>
    public static string 探针_存盘路径 => ProjectSettings.GlobalizePath(存盘路径);
}
