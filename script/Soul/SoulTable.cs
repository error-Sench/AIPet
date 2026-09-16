using System.IO;
using System.Text;
using Godot;

namespace desktop.script.Soul;

/// <summary>
/// 灵魂层：读取「人格文件」soul.md，提供 Agent **自主读取**的人格文本（本类不做任何注入）。
/// 关键认知：LLM 不读文件学习、权重不变；人格 = prompt 资产，
/// 靠「每轮注入 + few-shot 范式锚定 + 低 temperature」逼近稳定。
/// 职责边界（互不混写）：
///   SoulTable -> soul.md      (人格, 主人手改)
///   Profile   -> profile.md   (用户画像, Agent 写)
///   Memory    -> memory.jsonl (记忆流水, Agent 追加)
///   StatsTable-> stats.json   (数值, 程序高频写)
/// </summary>
public static class SoulTable
{
    private const string Fallback = "# 我是谁\n（人格未配置）\n";

    public static string RawText { get; private set; } = "";
    public static string PromptSection { get; private set; } = "";
    public static bool IsLoaded { get; private set; }

    public static string FilePath => ProjectSettings.GlobalizePath("user://soul/soul.md");

    /// <summary>显示名缺省值（soul.md frontmatter 没写 name 时用）。</summary>
    public const string 默认名字 = "小萝";

    /// <summary>桌宠显示名：读 soul.md frontmatter 的 `name:`（**改人格文件即可改名**，聊天窗标题/说话人标签都用它）。</summary>
    public static string 名字
    {
        get
        {
            var 匹配 = System.Text.RegularExpressions.Regex.Match(RawText ?? "", @"(?m)^\s*name\s*:\s*(.+?)\s*$");
            if (!匹配.Success) return 默认名字;
            var 值 = 匹配.Groups[1].Value.Trim().Trim('"', '\'');
            return 值.Length == 0 ? 默认名字 : 值;
        }
    }

    /// <summary>启动时读取人格文件；缺失时回退模板并落一份到 user://。</summary>
    public static void Load()
    {
        var path = FilePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(path))
        {
            RawText = File.ReadAllText(path, Encoding.UTF8);
            IsLoaded = true;
        }
        else
        {
            RawText = ReadTemplate();
            IsLoaded = false;
            try { File.WriteAllText(path, RawText, Encoding.UTF8); }
            catch (System.Exception e) { GD.PrintErr($"[SoulTable] 写入模板失败: {e.Message}"); }
        }

        PromptSection = StripFrontmatter(RawText);
        GD.Print($"[SoulTable] 人格已加载 ({(IsLoaded ? "用户文件" : "模板")}), {PromptSection.Length} 字符");
    }

    /// <summary>保存（仅当程序确需修改人格时；常规由主人手改文件）。</summary>
    public static void Save()
    {
        try { File.WriteAllText(FilePath, RawText, Encoding.UTF8); }
        catch (System.Exception e) { GD.PrintErr($"[SoulTable] 保存失败: {e.Message}"); }
    }

    /// <summary>热重载：主人手改 soul.md 后调用。</summary>
    public static void Reload() => Load();

    private static string ReadTemplate()
    {
        // 走 ConfigFile（native 路径：user:// → exe 同目录 → res://）。
        // 注意：config/ 被 .gdignore 忽略，**不要**用 Godot.FileAccess 直接读 res://config/...
        try
        {
            var 路径 = Util.ConfigFile.找("config/soul_template.md");
            if (!string.IsNullOrEmpty(路径)) return File.ReadAllText(路径, Encoding.UTF8);
        }
        catch { /* 忽略，用内置兜底 */ }
        return Fallback;
    }

    /// <summary>去掉开头的 YAML frontmatter（--- ... ---），只留人格正文。</summary>
    private static string StripFrontmatter(string text)
    {
        if (!text.StartsWith("---")) return text.Trim();
        var end = text.IndexOf("\n---", 3, System.StringComparison.Ordinal);
        if (end < 0) return text.Trim();
        var nl = text.IndexOf('\n', end + 1);
        return (nl >= 0 ? text[(nl + 1)..] : "").Trim();
    }
}