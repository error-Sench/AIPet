using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace desktop.script.Game;

/// <summary>
/// 游戏模式：会话数据容器（横板玩法进度）。
/// <para>
/// 设计（见 `script/Game/README.md` / `script/Mode/README.md`）：
/// 1. **独立容器**：游戏进度归这里管理（`user://game/save.json`），不进灵魂层数据；
///    办公模式表现不受游戏影响，「办公即游戏」钩子（完成任务 → 游戏内奖励）后续挂本分支。
/// 2. **载入 / 存盘**：`ModeManager.SaveProgress/LoadProgress` 转发到本类，切换模式时自动存盘。
///    懒载入（首次用到才读档）；`存盘()` 会先补载入，避免「还没载入就拿默认值覆盖旧档」。
/// 3. **坏档不炸**：缺档 / 坏 JSON / 高版本档（程序不认识的未来结构）一律退回默认进度继续。
/// </para>
/// 约定：标识符英文，注释中文（AIPet-Agent.md §8）。
/// </summary>
public static class GameSession
{
    /// <summary>存档 schema 版本（结构变更时 +1；载入时高于本值 = 未来档，不半读）。</summary>
    public const int 当前版本 = 1;

    /// <summary>默认关卡 id（最小可玩的第一关；关卡内容见 script/Game/）。</summary>
    public const string 默认关卡 = "level_1";

    /// <summary>血量上限（0–上限夹取）。</summary>
    public const int 血量上限 = 100;

    /// <summary>默认存盘文件（主人真实游戏存档所在）。</summary>
    public const string 默认存盘路径 = "user://game/save.json";

    /// <summary>
    /// 探针用：临时覆盖存盘路径（**隔离测试，别碰主人的真实游戏存档**）。null = 用默认。
    /// 与 StatsTable 同一约定：探针先切临时档，收尾只删临时档。
    /// </summary>
    public static string 探针_覆盖存盘路径;

    private static string 存盘路径 => 探针_覆盖存盘路径 ?? 默认存盘路径;

    // ================= 进度数据 =================

    /// <summary>当前关卡 id。</summary>
    public static string 关卡 { get; private set; } = 默认关卡;

    /// <summary>当前血量（0–血量上限）。</summary>
    public static int 血量 { get; private set; } = 血量上限;

    /// <summary>检查点：切回游戏时的落点（进度保留的关键）。</summary>
    public static float 检查点X { get; private set; }
    public static float 检查点Y { get; private set; }

    /// <summary>已收集道具 id 列表（去重，保持获得顺序）。</summary>
    private static readonly List<string> _道具 = new();
    public static IReadOnlyList<string> 道具 => _道具;

    private static bool _已载入;

    // ================= 载入 / 存盘 =================

    /// <summary>
    /// 载入存档（懒载入，重复调用只生效一次）。
    /// 缺档 → 默认进度；坏档 / 高版本档 → 退回默认进度并在日志里说明。
    /// </summary>
    public static void 载入()
    {
        if (_已载入) return;
        _已载入 = true;
        try
        {
            var 全路径 = ProjectSettings.GlobalizePath(存盘路径);
            if (!System.IO.File.Exists(全路径))
            {
                GD.Print("[Game] 首次运行，用初始进度");
                return;
            }
            using var 文档 = JsonDocument.Parse(System.IO.File.ReadAllText(全路径));
            var r = 文档.RootElement;
            if (r.TryGetProperty("version", out var v) && v.GetInt32() > 当前版本)
            {
                // 未来版本的档：结构不认识，不猜、不半读 —— 退回默认进度
                GD.PrintErr($"[Game] 存档版本 {v.GetInt32()} 高于程序 {当前版本}，退回默认进度");
                重置();
                return;
            }
            if (r.TryGetProperty("level", out var lv)) 关卡 = lv.GetString() ?? 默认关卡;
            if (r.TryGetProperty("hp", out var hp)) 血量 = Math.Clamp(hp.GetInt32(), 0, 血量上限);
            if (r.TryGetProperty("checkpointX", out var cx)) 检查点X = cx.GetSingle();
            if (r.TryGetProperty("checkpointY", out var cy)) 检查点Y = cy.GetSingle();
            _道具.Clear();
            if (r.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in items.EnumerateArray())
                {
                    var id = it.GetString();
                    if (!string.IsNullOrEmpty(id) && !_道具.Contains(id)) _道具.Add(id);
                }
            }
            GD.Print($"[Game] 已载入进度：关卡 {关卡} / 血量 {血量} / 道具 {_道具.Count} 件");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Game] 载入失败（退回默认进度继续）: {ex.Message}");
            重置();
        }
    }

    /// <summary>存盘（切换模式时由 ModeManager 自动调；也可由玩法侧主动存）。</summary>
    public static void 存盘()
    {
        载入(); // 未载入先补载入，避免默认值覆盖旧档
        try
        {
            var 数据 = new Dictionary<string, object>
            {
                ["version"] = 当前版本,
                ["level"] = 关卡,
                ["hp"] = 血量,
                ["items"] = _道具,
                ["checkpointX"] = MathF.Round(检查点X, 2),
                ["checkpointY"] = MathF.Round(检查点Y, 2),
                ["_comment"] = "游戏模式进度（横板玩法）。独立容器，不进灵魂层数据；见 script/Game/README.md。",
            };
            var 全路径 = ProjectSettings.GlobalizePath(存盘路径);
            var 目录 = System.IO.Path.GetDirectoryName(全路径);
            if (!string.IsNullOrEmpty(目录)) System.IO.Directory.CreateDirectory(目录);
            System.IO.File.WriteAllText(全路径, JsonSerializer.Serialize(数据,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文不转义，文件保持可读
                }));
        }
        catch (Exception ex) { GD.PrintErr($"[Game] 存盘失败: {ex.Message}"); }
    }

    // ================= 写（玩法侧调用） =================

    /// <summary>设当前关卡（空值回默认关卡）。</summary>
    public static void 设关卡(string 值) => 关卡 = string.IsNullOrWhiteSpace(值) ? 默认关卡 : 值;

    /// <summary>设血量（0–上限夹取）。</summary>
    public static void 设血量(int 值) => 血量 = Math.Clamp(值, 0, 血量上限);

    /// <summary>设检查点落点。</summary>
    public static void 设检查点(float x, float y) { 检查点X = x; 检查点Y = y; }

    /// <summary>收进一件道具（重复 id 只记一次）。</summary>
    public static void 加道具(string id)
    {
        if (!string.IsNullOrEmpty(id) && !_道具.Contains(id)) _道具.Add(id);
    }

    /// <summary>是否已有某道具。</summary>
    public static bool 有道具(string id) => _道具.Contains(id);

    /// <summary>重置为新开局（清空全部进度）。已载入标记置位，避免随后被旧档拉回。</summary>
    public static void 重置()
    {
        _已载入 = true;
        关卡 = 默认关卡;
        血量 = 血量上限;
        检查点X = 0f;
        检查点Y = 0f;
        _道具.Clear();
    }

    // ================= 探针专用 =================

    /// <summary>探针：重置载入标志（便于断言载入路径）。</summary>
    public static void 探针_重置载入标志() => _已载入 = false;

    /// <summary>探针：当前存盘文件路径（已全局化）。</summary>
    public static string 探针_存盘路径 => ProjectSettings.GlobalizePath(存盘路径);
}
