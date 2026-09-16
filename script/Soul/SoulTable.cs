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
    private const string TemplateResPath = "res://settings/soul_template.md";
    private const string Fallback = "# 我是谁\n（人格未配置）\n";

    public static string RawText { get; private set; } = "";
    public static string PromptSection { get; private set; } = "";
    public static bool IsLoaded { get; private set; }

    public static string FilePath => ProjectSettings.GlobalizePath("user://soul/soul.md");

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
        // 优先 exe 同目录（分发包形态）→ user:// → res://（开发期；`Godot.FileAccess` 能读 pck 里的资源）
        try
        {
            var 外部 = Util.ConfigFile.找("settings/soul_template.md");
            if (!string.IsNullOrEmpty(外部)) return File.ReadAllText(外部, Encoding.UTF8);
        }
        catch { /* 忽略，继续走 pck 资源 */ }
        if (Godot.FileAccess.FileExists(TemplateResPath))
        {
            using var f = Godot.FileAccess.Open(TemplateResPath, Godot.FileAccess.ModeFlags.Read);
            if (f != null) return f.GetAsText();
        }
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