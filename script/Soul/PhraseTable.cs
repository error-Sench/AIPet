using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace desktop.script.Soul;

/// <summary>
/// **本地话语表**（`config/phrases.json`）—— 桌宠*自己*说的小句子：没接 Agent 的时候也有话可说。
/// <para>
/// 分类：`问候`（按时间段：早上/中午/下午/晚上/深夜）/ `被摸` / `久坐` / `磁盘` / `降级`。
/// 每类一个字符串数组，随机抽一条（**不会连着重复**）；`磁盘` 支持 `{盘}` `{余量}` 占位符；
/// 空数组 = 该类不说话。表缺失/坏掉 → 用**内置兜底**（与旧硬编码文案一致），保证不会哑。
/// </para>
/// <para>
/// 边界（主人定的分工）：**有 Agent 时日常对话归 Agent**；这张表负责
/// 「没接 Agent」「程序自己该说话」（问候/久坐/磁盘/降级）这几类场合。
/// </para>
/// </summary>
public static class PhraseTable
{
    private const string 配置路径 = "config/phrases.json";

    /// <summary>分类 → 候选句（`问候` 这类嵌套结构拍平成 `问候/早上`）。</summary>
    private static readonly Dictionary<string, string[]> _表 = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> _上次索引 = new(StringComparer.Ordinal);

    /// <summary>内置兜底（表缺失/坏掉/某类被清空时用；与旧硬编码文案一致）。</summary>
    private static readonly Dictionary<string, string[]> 兜底表 = new(StringComparer.Ordinal)
    {
        ["问候/早上"] = new[] { "早上好呀～", "早安，今天也一起加油吧～", "早呀，睡得好吗？" },
        ["问候/中午"] = new[] { "中午好～", "到饭点啦，我在这儿等你回来～" },
        ["问候/下午"] = new[] { "下午好～", "下午呀，我在这儿陪着～" },
        ["问候/晚上"] = new[] { "晚上好～", "晚上好呀，今天过得怎么样？" },
        ["问候/深夜"] = new[] { "这么晚还在呀，我陪着你～", "夜深啦，我一直都在哦～" },
        ["被摸"] = new[] { "嘿嘿～", "摸摸头，舒服～" },
        ["久坐"] = new[] { "坐太久啦，起来伸个懒腰嘛～", "已经连着忙好久咯，喝口水再继续？", "腰要哭啦，站起来走两步吧～" },
        ["磁盘"] = new[] { "{盘} 盘只剩 {余量} GB 了，要不要清一清？" },
        ["降级"] = new[] { "唔…我连不上「大脑」了，先自己待着；你随时可以再叫我～" },
    };

    public static bool 已载入 { get; private set; }

    /// <summary>可注入随机（探针用；默认 Random.Shared）。</summary>
    public static Func<int, int> 随机 = Random.Shared.Next;

    /// <summary>启动时载入；缺失/坏掉 → 内置兜底（不抛、不哑）。</summary>
    public static void 加载()
    {
        _表.Clear();
        _上次索引.Clear();
        foreach (var 相对 in new[] { "phrases.json", 配置路径 })
        {
            foreach (var 路径 in Util.ConfigFile.候选(相对))
            {
                try
                {
                    if (!File.Exists(路径)) continue;
                    var 文本 = File.ReadAllText(路径);
                    if (string.IsNullOrWhiteSpace(文本)) continue;
                    解析(文本, 路径);
                    if (_表.Count > 0) { 已载入 = true; GD.Print($"[PhraseTable] 载入 {路径}：{_表.Count} 个分类"); return; }
                }
                catch (Exception e) { GD.PrintErr($"[PhraseTable] 读 {路径} 失败: {e.Message}"); }
            }
        }
        已载入 = false;
        GD.Print("[PhraseTable] 没找到话语表 → 用内置兜底");
    }

    /// <summary>探针：直接用一段文本装表（验证解析/占位符/坏数据兜底）。</summary>
    public static void 探针_载入文本(string 文本)
    {
        _表.Clear();
        _上次索引.Clear();
        try { 解析(文本, "探针"); 已载入 = _表.Count > 0; }
        catch (Exception e) { GD.PrintErr($"[PhraseTable] 探针文本解析失败: {e.Message}"); 已载入 = false; }
    }

    /// <summary>探针：恢复成从配置文件载入。</summary>
    public static void 探针_恢复() => 加载();

    private static void 解析(string 文本, string 来源)
    {
        using var 文档 = JsonDocument.Parse(文本);
        if (文档.RootElement.ValueKind != JsonValueKind.Object) return;
        foreach (var 项 in 文档.RootElement.EnumerateObject())
        {
            if (项.Name.StartsWith("_", StringComparison.Ordinal)) continue;   // _comment 之类
            if (项.Value.ValueKind == JsonValueKind.Array)
            {
                装(项.Name, 项.Value);
            }
            else if (项.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var 子 in 项.Value.EnumerateObject())
                {
                    if (子.Name.StartsWith("_", StringComparison.Ordinal)) continue;
                    if (子.Value.ValueKind == JsonValueKind.Array) 装($"{项.Name}/{子.Name}", 子.Value);
                }
            }
        }
    }

    private static void 装(string 分类, JsonElement 数组)
    {
        var 句 = 数组.EnumerateArray()
                     .Where(e => e.ValueKind == JsonValueKind.String)
                     .Select(e => e.GetString())
                     .Where(s => !string.IsNullOrWhiteSpace(s))
                     .Select(s => s.Trim())
                     .ToArray();
        if (句.Length > 0) _表[分类] = 句;
    }

    // ================= 取话 =================

    /// <summary>该分类有没有话（表里没有 → 看兜底）。</summary>
    public static bool 有(string 分类) => 表(分类).Length > 0;

    /// <summary>该分类候选句数量（探针/调试）。</summary>
    public static int 行数(string 分类) => 表(分类).Length;

    /// <summary>随机取一条（不会连着重复）；没有话 → 空串。可带占位符替换。</summary>
    public static string 取(string 分类, params (string 键, string 值)[] 替换)
    {
        var 句 = 表(分类);
        if (句.Length == 0) return "";
        int i;
        if (句.Length == 1) i = 0;
        else
        {
            var 上一个 = _上次索引.TryGetValue(分类, out var v) ? v : -1;
            do { i = Math.Abs(随机(句.Length)); } while (i == 上一个 && 句.Length > 1);
            _上次索引[分类] = i;
        }
        var 结果 = 句[i];
        foreach (var (键, 值) in 替换) 结果 = 结果.Replace("{" + 键 + "}", 值);
        return 结果;
    }

    /// <summary>按时间段挑问候语（纯函数式：同一时刻多次调用会抽到不同句子，时段判定是确定的）。</summary>
    public static (string 时段, string 语句) 问候(DateTime 现在)
    {
        var 时段 = 时段名(现在);
        var 句 = 取($"问候/{时段}");
        return (时段, string.IsNullOrEmpty(句) ? "你好呀～" : 句);
    }

    /// <summary>时段名（纯函数；与问候语分档一致：5-11 早 / 11-14 中 / 14-18 下 / 18-23 晚 / 其余深夜）。</summary>
    public static string 时段名(DateTime 现在) => 现在.Hour switch
    {
        >= 5 and < 11 => "早上",
        >= 11 and < 14 => "中午",
        >= 14 and < 18 => "下午",
        >= 18 and < 23 => "晚上",
        _ => "深夜",
    };

    /// <summary>表优先、兜底其次。</summary>
    private static string[] 表(string 分类)
    {
        if (_表.TryGetValue(分类, out var 句) && 句.Length > 0) return 句;
        if (兜底表.TryGetValue(分类, out var 兜)) return 兜;
        return Array.Empty<string>();
    }
}
