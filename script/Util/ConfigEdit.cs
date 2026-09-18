using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace desktop.script.Util;

/// <summary>
/// 配置写回（配置窗用）。三个约定：
/// ① **保留未知键与 `_comment`**——读 JSON → 只改目标键 → 原样写回（缩进 2），不整文件重写；
/// ② **中文不转义**（`UnsafeRelaxedJsonEscaping`），否则文件会变成一片 `\uXXXX`，主人没法手改；
/// ③ 写入位置沿用读取顺序（<see cref="ConfigFile.候选"/>）：改**已存在**的那个文件；都不存在时写
/// `<exe 同目录>/相对路径`（分发包形态：用户配置在 exe 旁边），开发期落到 `res://` 源文件。
/// </summary>
public static class ConfigEdit
{
    /// <summary>探针：把写入重定向到临时目录（不碰真配置）。空 = 正常写。</summary>
    public static string 探针_根目录 = "";

    private static readonly JsonSerializerOptions 排版 = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>实际会写到哪个文件（不存在则给出建议路径；探针可直接断言）。</summary>
    public static string 写路径(string 相对路径)
    {
        var 规范 = 相对路径.Replace('\\', '/').TrimStart('/');
        if (!string.IsNullOrEmpty(探针_根目录))
            return Path.Combine(探针_根目录, 规范.Replace('/', Path.DirectorySeparatorChar));

        var 已存在 = ConfigFile.找(规范);
        if (!string.IsNullOrEmpty(已存在)) return 已存在;

        var exe目录 = Path.GetDirectoryName(OS.GetExecutablePath());
        if (!string.IsNullOrEmpty(exe目录))
        {
            var 旁路 = Path.Combine(exe目录, 规范.Replace('/', Path.DirectorySeparatorChar));
            try { if (Directory.Exists(Path.GetDirectoryName(旁路))) return 旁路; } catch { /* 忽略 */ }
        }
        return ProjectSettings.GlobalizePath($"res://{规范}");
    }

    /// <summary>改一个键并写回（bool / int / double / string）。返回是否成功。</summary>
    public static bool 写(string 相对路径, string 键, object 值)
    {
        try
        {
            var 路径 = 写路径(相对路径);
            JsonObject 根;
            if (File.Exists(路径))
            {
                var 文本 = File.ReadAllText(路径);
                根 = string.IsNullOrWhiteSpace(文本)
                    ? new JsonObject()
                    : JsonNode.Parse(文本) as JsonObject ?? new JsonObject();
            }
            else 根 = new JsonObject();

            JsonNode 节点 = 值 switch
            {
                null => null,
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                float f => JsonValue.Create(f),
                double d => JsonValue.Create(d),
                _ => JsonValue.Create(值.ToString()),
            };
            if (节点 == null) 根.Remove(键);
            else 根[键] = 节点;

            var 目录 = Path.GetDirectoryName(路径);
            if (!string.IsNullOrEmpty(目录)) Directory.CreateDirectory(目录);
            File.WriteAllText(路径, 根.ToJsonString(排版) + "\n", new UTF8Encoding(false));
            return true;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[ConfigEdit] 写配置失败 {相对路径} · {键}: {e.Message}");
            return false;
        }
    }

    /// <summary>读一个键的原始文本（配置窗回填控件用；读不到给兜底）。探针重定向同样生效。</summary>
    public static string 读文本(string 相对路径, string 键, string 兜底 = "")
    {
        try
        {
            var 路径 = !string.IsNullOrEmpty(探针_根目录) ? 写路径(相对路径) : ConfigFile.找(相对路径);
            if (string.IsNullOrEmpty(路径) || !File.Exists(路径)) return 兜底;
            var 根 = JsonNode.Parse(File.ReadAllText(路径)) as JsonObject;
            if (根 == null || !根.TryGetPropertyValue(键, out var 节点) || 节点 == null) return 兜底;
            return 节点.ToJsonString().Trim('"');
        }
        catch { return 兜底; }
    }
}
