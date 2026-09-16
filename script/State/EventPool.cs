using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Godot;

namespace desktop.script.State;

/// <summary>
/// 事件池（行为事件流水）—— `idea.md` §8「信息池」的落地形态：**单一 JSONL 流水 + 保留策略 + 隐私边界**。
/// <para>
/// 两个用途（主人决策：「主动行为清单」与「被动触发器」本质是一回事，合并为「行为事件」）：
/// 1. **行为日志**：程序侧事件（久坐提醒 / 主人回来 / 离开 / 任务完成…）按时间追加，事后可回溯「它当时为什么这么做」。
/// 2. **Agent 事件（pull，不做推送）**：归属 `Agent` 的事件写进池子（`status=pending`）**等 Agent 自己来读**
///    （上下文接口 `user://context.md` 里会列出未处理事件）；Agent 处理完**追加一条 ack 行**即算清账。
/// </para>
/// <para>
/// **隐私边界（写死的硬约束）**：只记「桌宠自己的观察与行为」——时间、时长、我们自己的状态机事件；
/// **不记窗口标题、进程名、键鼠内容、屏幕内容**（与 `EnvironmentSense` 的边界一致）。
/// </para>
/// </summary>
public static class EventPool
{
    public enum 归属 { 程序, Agent }

    public static string 路径 => ProjectSettings.GlobalizePath("user://events.jsonl");

    /// <summary>保留策略：最多留这么多行（超出裁掉最旧的）。</summary>
    public static int 上限条数 { get; set; } = 500;

    /// <summary>探针用：临时覆写文件路径（不碰真文件）。</summary>
    public static string 探针_路径覆写 { get; set; } = "";

    private static string 有效路径 => string.IsNullOrEmpty(探针_路径覆写) ? 路径 : 探针_路径覆写;

    // ================= 写 =================

    /// <summary>记一条事件（自动裁剪超限的旧行）。</summary>
    public static void 记(string 类型, 归属 归属, string 文本)
    {
        try
        {
            var 行 = new Dictionary<string, object>
            {
                ["t"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["kind"] = 类型,
                ["owner"] = 归属 == 归属.Agent ? "agent" : "program",
                ["text"] = 文本,
                ["status"] = 归属 == 归属.Agent ? "pending" : "logged",
            };
            File.AppendAllText(有效路径, JsonSerializer.Serialize(行, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n", new UTF8Encoding(false));
            裁剪();
        }
        catch (Exception e) { GD.PrintErr($"[EventPool] 记事件失败: {e.Message}"); }
    }

    /// <summary>Agent 处理完一条事件后追加的确认行（Agent 也可以自己往文件里追加同样的行）。</summary>
    public static void 确认程序侧(string 事件时间, string 备注 = "") =>
        追加(new Dictionary<string, object>
        {
            ["t"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["kind"] = "ack",
            ["ref"] = 事件时间,
            ["note"] = 备注,
        });

    private static void 追加(Dictionary<string, object> 行)
    {
        try
        {
            File.AppendAllText(有效路径,
                JsonSerializer.Serialize(行, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n",
                new UTF8Encoding(false));
            裁剪();
        }
        catch (Exception e) { GD.PrintErr($"[EventPool] 追加失败: {e.Message}"); }
    }

    // ================= 读 =================

    /// <summary>全部可解析的行（坏行静默跳过，不炸）。</summary>
    public static List<Dictionary<string, string>> 读()
    {
        var 结果 = new List<Dictionary<string, string>>();
        try
        {
            if (!File.Exists(有效路径)) return 结果;
            foreach (var 行 in File.ReadAllLines(有效路径))
            {
                var 修剪 = 行.Trim();
                if (修剪.Length == 0) continue;
                try
                {
                    using var 文档 = JsonDocument.Parse(修剪);
                    var 字典 = new Dictionary<string, string>();
                    foreach (var 属性 in 文档.RootElement.EnumerateObject())
                        字典[属性.Name] = 属性.Value.ValueKind == JsonValueKind.String
                            ? 属性.Value.GetString() ?? ""
                            : 属性.Value.ToString();
                    结果.Add(字典);
                }
                catch { /* 坏行跳过 */ }
            }
        }
        catch (Exception e) { GD.PrintErr($"[EventPool] 读失败: {e.Message}"); }
        return 结果;
    }

    /// <summary>未处理的 Agent 事件（owner=agent 且没有对应 ack）。</summary>
    public static List<Dictionary<string, string>> 未处理()
    {
        var 全部 = 读();
        var 已确认 = new HashSet<string>(全部.Where(x => x.TryGetValue("kind", out var k) && k == "ack")
            .Select(x => x.TryGetValue("ref", out var r) ? r : "").Where(r => r.Length > 0));
        return 全部.Where(x =>
            x.TryGetValue("owner", out var o) && o == "agent" &&
            x.TryGetValue("kind", out var k) && k != "ack" &&
            x.TryGetValue("t", out var t) && !已确认.Contains(t)).ToList();
    }

    public static int 未处理数 => 未处理().Count;

    /// <summary>某人话摘要（给上下文接口用）。</summary>
    public static string 未处理摘要(int 条数上限 = 5)
    {
        var 待办 = 未处理();
        if (待办.Count == 0) return "（没有待你处理的事件）";
        var sb = new StringBuilder();
        foreach (var 事件 in 待办.Take(条数上限))
            sb.AppendLine($"- [{事件.GetValueOrDefault("t", "?")}] {事件.GetValueOrDefault("kind", "?")}：{事件.GetValueOrDefault("text", "")}");
        if (待办.Count > 条数上限) sb.AppendLine($"- …还有 {待办.Count - 条数上限} 条（自己读文件）");
        return sb.ToString().TrimEnd();
    }

    // ================= 保留策略 =================

    /// <summary>超过上限就砍最旧的（保留策略：只留最近 `上限条数` 行）。</summary>
    public static void 裁剪()
    {
        try
        {
            if (!File.Exists(有效路径)) return;
            var 全部 = File.ReadAllLines(有效路径).Where(l => l.Trim().Length > 0).ToArray();
            if (全部.Length <= 上限条数) return;
            File.WriteAllLines(有效路径, 全部.Skip(全部.Length - 上限条数), new UTF8Encoding(false));
        }
        catch (Exception e) { GD.PrintErr($"[EventPool] 裁剪失败: {e.Message}"); }
    }

    /// <summary>探针：清空（只对覆写路径生效，避免误删真日志）。</summary>
    public static void 探针_清空()
    {
        if (string.IsNullOrEmpty(探针_路径覆写)) return;
        try { if (File.Exists(探针_路径覆写)) File.Delete(探针_路径覆写); } catch { /* 忽略 */ }
    }
}
