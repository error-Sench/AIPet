using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Godot;

namespace desktop.script.Agent;

/// <summary>
/// 指令收件箱（能力层下行面 · **工具通道**）：
/// Agent 通过 MCP 工具 `pet_command` 发指令 → `aipet-mcp`（随包的 stdio 服务）把请求写进
/// `user://actions.jsonl` → 本类轮询读取、交给 `PetCommands` 校验执行、再把回执写回同一文件
/// （工具进程读回执返回给 Agent，于是 Agent 拿到的是**真实执行结果**）。
/// <para>
/// 与文本通道（回复内嵌围栏块）的关系：**两个入口、一个执行口** —— 白名单与参数校验都在 `PetCommands`，
/// 这里只做搬运。工具通道首选（正文干净、有回执）；围栏块是没有 MCP 的 Agent 的兼容通道。
/// </para>
/// <para>
/// 文件协议（append-only JSONL，双方各写自己的行）：
/// <code>
/// 请求 ← {"id":"…","t":"…","cmd":"set_state","state":"think"}
/// 回执 → {"repl":"&lt;id&gt;","ok":true,"note":"✓ set_state [state=think]"}
/// </code>
/// **不吃旧账**：启动时把读取偏移定位到文件末尾——桌宠不在运行时写下的指令不会事后突然诈尸。
/// 文件被截断/重写 → 偏移归零重读（回执行没有 cmd，会被自然跳过）。
/// </para>
/// </summary>
public static class ActionInbox
{
    /// <summary>探针：覆写收件箱路径（绝不碰真实用户数据）。</summary>
    public static string 探针_路径覆写 { get; set; } = "";

    public static string 收件箱路径 => string.IsNullOrEmpty(探针_路径覆写)
        ? ProjectSettings.GlobalizePath("user://actions.jsonl")
        : 探针_路径覆写;

    private const double 轮询间隔秒 = 0.25;

    private static long _偏移;
    private static double _计时;
    private static bool _已启动;

    /// <summary>启动轮询（Main._Ready 调用；重复调用无害）。</summary>
    public static void 启动()
    {
        if (_已启动) return;
        _已启动 = true;
        裁剪();
        try { _偏移 = File.Exists(收件箱路径) ? new FileInfo(收件箱路径).Length : 0; }
        catch { _偏移 = 0; }

        if (Engine.GetMainLoop() is not SceneTree 树 || 树.Root == null)
        {
            GD.PrintErr("[Inbox] 主场景树不可用 → 收件箱未启动");
            return;
        }
        var 驱动 = new ActionInboxDriver { Name = "ActionInboxDriver" };
        // 启动窗口期（Main._Ready 内）根节点正忙，直接 add_child 会被引擎静默拒绝 → 推迟一帧
        树.Root.CallDeferred(Node.MethodName.AddChild, 驱动);
        GD.Print($"[Inbox] 工具通道就绪（{收件箱路径}）");
    }

    /// <summary>节流轮询（由驱动节点每帧调用）。</summary>
    internal static void 心跳(double delta)
    {
        _计时 += delta;
        if (_计时 < 轮询间隔秒) return;
        _计时 = 0;
        读一轮();
    }

    private static void 读一轮()
    {
        try
        {
            var 文件 = 收件箱路径;
            if (!File.Exists(文件)) return;
            var 长度 = new FileInfo(文件).Length;
            if (长度 < _偏移) _偏移 = 0;                 // 截断/重写 → 重读
            if (长度 == _偏移) return;

            // 注意：FileAccess/FileMode 有 Godot 与 System 两个同名类型（AGENTS 坑 #16）→ 显式限定命名空间
            using var 流 = new FileStream(文件, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
            流.Seek(_偏移, SeekOrigin.Begin);
            var 待读 = (int)Math.Min(长度 - _偏移, 1_000_000);
            var 缓冲 = new byte[待读];
            var 读出 = 流.Read(缓冲, 0, 待读);
            if (读出 <= 0) return;

            var 文本 = Encoding.UTF8.GetString(缓冲, 0, 读出);
            var 末位换行 = 文本.LastIndexOf('\n');
            if (末位换行 < 0) return;                     // 还没有完整行（写到一半）→ 下一轮
            var 可消费 = 文本[..(末位换行 + 1)];
            _偏移 += Encoding.UTF8.GetByteCount(可消费);
            foreach (var 行 in 可消费.Split('\n'))
                if (行.Trim().Length > 0) 处理一行(行.Trim());
        }
        catch (Exception e) { GD.PrintErr($"[Inbox] 读收件箱失败: {e.Message}"); }
    }

    private static void 处理一行(string 行)
    {
        try
        {
            using var 文档 = JsonDocument.Parse(行);
            var 根 = 文档.RootElement;
            if (根.ValueKind != JsonValueKind.Object) return;

            string id = null, cmd = null;
            var 参数 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var 属性 in 根.EnumerateObject())
            {
                var 值 = 属性.Value.ValueKind switch
                {
                    JsonValueKind.String => 属性.Value.GetString() ?? "",
                    JsonValueKind.Number => 属性.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "",
                    _ => 属性.Value.GetRawText(),
                };
                switch (属性.Name)
                {
                    case "id": id = 值; break;
                    case "t": break;                       // 时间戳：只留档，不参与执行
                    case "cmd": cmd = 值; break;
                    default: 参数[属性.Name] = 值; break;
                }
            }

            if (string.IsNullOrEmpty(cmd)) return;         // 回执行等非请求行 → 跳过

            var 指令 = new PetCommand { Cmd = cmd.Trim(), 原文 = 行 };
            foreach (var kv in 参数) 指令.Args[kv.Key] = kv.Value;
            var 成功 = PetCommands.执行(new List<PetCommand> { 指令 }, out var 日志);
            var note = 日志.Count > 0 ? 日志[0] : "（无日志）";
            写回执(id, 成功 > 0, note);
        }
        catch (Exception e) { GD.PrintErr($"[Inbox] 指令处理失败（已跳过该行）: {e.Message}"); }
    }

    private static void 写回执(string id, bool ok, string note)
    {
        if (string.IsNullOrEmpty(id)) return;              // 没有 id 无从回执（指令照样已执行）
        try
        {
            var 行 = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["repl"] = id,
                ["ok"] = ok,
                ["note"] = note,
                ["at"] = DateTime.Now.ToString("HH:mm:ss.fff"),
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            using var 流 = new FileStream(收件箱路径, FileMode.Append, System.IO.FileAccess.Write, FileShare.ReadWrite);
            using var 写 = new StreamWriter(流, new UTF8Encoding(false));
            写.Write(行 + "\n");
        }
        catch (Exception e) { GD.PrintErr($"[Inbox] 写回执失败: {e.Message}"); }
    }

    /// <summary>启动时轻量裁剪：只留最近 500 行（收件箱不无限长大；回执行一并裁掉，无影响）。</summary>
    private static void 裁剪()
    {
        try
        {
            if (!File.Exists(收件箱路径)) return;
            var 行 = File.ReadAllLines(收件箱路径);
            if (行.Length <= 800) return;
            File.WriteAllLines(收件箱路径, 行[^500..], new UTF8Encoding(false));
        }
        catch { /* 忽略：裁不了就算了 */ }
    }
}

/// <summary>驱动节点：静态类没有 _Process，用一个隐身子节点跑收件箱轮询（与 Tts.TtsPlayer 同一模式）。</summary>
public partial class ActionInboxDriver : Node
{
    public override void _Process(double delta) => ActionInbox.心跳(delta);
}
