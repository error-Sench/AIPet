using System;
using System.IO;
using System.Text;
using Godot;

namespace desktop.script.Soul;

/// <summary>
/// 上下文接口（**Agent 自主读取**，我们不做主动推送 —— 见 `script/Soul/README.md` 硬规则）。
/// <para>
/// 桌宠把「人格 + 数值 + 画像 + 最近记忆 + 指令协议」组装成**一份文件**写到磁盘：`user://context.md`。
/// Agent 想了解桌宠时**读这一个文件就够了**，不必到处翻目录；要深挖再顺着文件里给的路径去读源文件。
/// </para>
/// <para>
/// 分工（重要）：**源文件各由负责方写** —— 数值由程序写、画像/记忆由 Agent 自己写；
/// 本文件是**只读视图**（下次生成会覆盖，Agent 不要改这里）。
/// </para>
/// </summary>
public static class ContextTable
{
    // ================= 路径（唯一的真源，skill / 文档都引用这里） =================

    public const string 上下文文件名 = "context.md";
    public static string 上下文路径 => ProjectSettings.GlobalizePath($"user://{上下文文件名}");
    public static string 数值路径 => ProjectSettings.GlobalizePath("user://stats.json");
    public static string 画像路径 => ProjectSettings.GlobalizePath("user://soul/profile.md");
    public static string 记忆路径 => ProjectSettings.GlobalizePath("user://soul/memory.jsonl");

    /// <summary>最近记忆在上下文里带几条。</summary>
    public const int 记忆条数上限 = 12;

    // ================= 数据文件脚手架 =================

    private const string 画像模板 =
        "# 用户画像\n\n" +
        "> 本文件由 **Agent 维护**（桌宠只读它、只做展示；不做主动注入）。上限 5000 字符，超出请合并精简。\n" +
        "> 记什么：主人是谁、在意什么、长期目标、习惯与偏好、关系变化。别记流水账（流水账归 memory.jsonl）。\n\n" +
        "（还没写。）\n";

    private const string 记忆模板 =
        "{\"_schema\":\"每行一条 JSON：{\\\"t\\\":\\\"ISO时间\\\",\\\"kind\\\":\\\"事实|偏好|事件|承诺\\\",\\\"text\\\":\\\"一句话\\\",\\\"weight\\\":0.0~1.0}\"}\n" +
        "{\"_note\":\"由 Agent 自己追加；短期保留最近 6 条，每 6 条提炼一次成中期 6 条，永久记忆保留 20 条（提炼规则见 skill）\"}\n";

    /// <summary>确保数据文件存在（**只创建、绝不覆盖**已存在的内容）。启动时调用一次。</summary>
    public static void 确保数据文件()
    {
        try
        {
            var 目录 = Path.GetDirectoryName(画像路径);
            if (!string.IsNullOrEmpty(目录)) Directory.CreateDirectory(目录);
            if (!File.Exists(画像路径)) File.WriteAllText(画像路径, 画像模板, new UTF8Encoding(false));
            if (!File.Exists(记忆路径)) File.WriteAllText(记忆路径, 记忆模板, new UTF8Encoding(false));
        }
        catch (Exception e) { GD.PrintErr($"[Context] 数据文件脚手架失败: {e.Message}"); }
    }

    // ================= 组装 / 生成 =================

    /// <summary>重新组装并写入 `user://context.md`（启动时 + 数值存盘时调用）。</summary>
    public static void 生成()
    {
        try { File.WriteAllText(上下文路径, 组装(), new UTF8Encoding(false)); }
        catch (Exception e) { GD.PrintErr($"[Context] 生成失败: {e.Message}"); }
    }

    /// <summary>组装上下文文本（**纯函数**，探针可直接断言）。</summary>
    public static string 组装()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 桌宠上下文（给 Agent 读取）");
        sb.AppendLine();
        sb.AppendLine($"> 桌宠程序自动生成 · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · **本文件只读**：下次生成会覆盖，别在这里写东西。");
        sb.AppendLine("> 想深挖就顺着这些路径去读源文件（哪个文件归谁写也标了）：");
        sb.AppendLine($"  - 人格 soul.md → `{SoulTable.FilePath}`（主人 / 你）");
        sb.AppendLine($"  - 数值 stats.json → `{数值路径}`（程序写，你别改）");
        sb.AppendLine($"  - 用户画像 profile.md → `{画像路径}`（**你写**）");
        sb.AppendLine($"  - 记忆流水 memory.jsonl → `{记忆路径}`（**你写**）");
        sb.AppendLine();

        // —— 数值层 ——
        sb.AppendLine("## 此刻的状态（数值层）");
        sb.AppendLine($"- 心情 mood {StatsTable.心情整}/100 —— {StatsTable.当前心情文字}");
        sb.AppendLine($"- 精力 energy {StatsTable.精力整}/100");
        sb.AppendLine($"- 亲密 affection {StatsTable.亲密整}/999（只增不减，随相处累积）");
        sb.AppendLine("- 数值只影响桌宠的**表现与频率**（表情变体、走动节奏、打瞌睡早晚），不改变它的人格与说话方式。");
        sb.AppendLine();

        // —— 人格 ——
        sb.AppendLine("## 人格（soul.md 全文）");
        sb.AppendLine("```");
        sb.AppendLine(string.IsNullOrWhiteSpace(SoulTable.RawText) ? "（soul.md 还没写内容）" : SoulTable.RawText.TrimEnd());
        sb.AppendLine("```");
        sb.AppendLine();

        // —— 画像 ——
        sb.AppendLine("## 用户画像（profile.md）");
        sb.AppendLine("```");
        sb.AppendLine(读文件或占位(画像路径, "（还没建立画像）"));
        sb.AppendLine("```");
        sb.AppendLine();

        // —— 记忆 ——
        sb.AppendLine($"## 最近记忆（memory.jsonl 最后 {记忆条数上限} 条）");
        var 记忆 = 读末尾几行(记忆路径, 记忆条数上限);
        if (记忆.Length == 0) sb.AppendLine("（还没有记忆）");
        else foreach (var 行 in 记忆) sb.AppendLine($"- {行}");
        sb.AppendLine();

        // —— 指令协议 ——
        sb.AppendLine("## 你能指挥桌宠做什么（指令通道）");
        sb.AppendLine("在你的回复文本里内嵌一个 ```pet 围栏块，块内每行一条 JSON 指令，桌宠会执行并把围栏块从聊天里隐藏：");
        sb.AppendLine("- `{\"cmd\":\"set_state\",\"state\":\"think|idle|sleep|working|speak…\"}` —— 切状态");
        sb.AppendLine("- `{\"cmd\":\"speak\",\"text\":\"…\"}` —— 让它冒个气泡（≤200 字，别复述你正文）");
        sb.AppendLine("- `{\"cmd\":\"play_anim\",\"name\":\"…\"}` —— 播指定动画（如 `walk-left`、`edge_hide-left-keep`）");
        sb.AppendLine("- `{\"cmd\":\"set_mood\",\"mood\":65}` —— 改心情（0–100，或 happy / tired / sad…）");
        sb.AppendLine("- 每轮最多 6 条；写错了会被忽略并记日志。");
        return sb.ToString();
    }

    // ================= 读文件的小工具 =================

    private static string 读文件或占位(string 路径, string 占位)
    {
        try
        {
            if (!File.Exists(路径)) return 占位;
            var 文本 = File.ReadAllText(路径).Trim();
            return 文本.Length == 0 ? 占位 : 文本;
        }
        catch { return 占位; }
    }

    /// <summary>读文件末尾若干非空行（记忆是流水账，只带最近几条进上下文）。</summary>
    private static string[] 读末尾几行(string 路径, int 条数)
    {
        try
        {
            if (!File.Exists(路径)) return Array.Empty<string>();
            var 全部 = File.ReadAllLines(路径);
            var 结果 = new System.Collections.Generic.List<string>();
            for (var i = 全部.Length - 1; i >= 0 && 结果.Count < 条数; i--)
            {
                var 行 = 全部[i].Trim();
                if (行.Length > 0) 结果.Insert(0, 行);
            }
            return 结果.ToArray();
        }
        catch { return Array.Empty<string>(); }
    }
}
