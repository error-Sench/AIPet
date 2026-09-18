using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using desktop.script.Mode;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.script.Agent;

/// <summary>一条来自 Agent 的指令（已解析、尚未校验）。</summary>
public sealed class PetCommand
{
    public string Cmd = "";
    public Dictionary<string, string> Args = new(StringComparer.OrdinalIgnoreCase);
    public string 原文 = "";

    public string 取值(string 键) => Args.TryGetValue(键, out var v) ? v : "";
}

/// <summary>
/// 指令通道（能力层的**下行面**）：两条通道（MCP 工具 / 回复内嵌块），桌宠解析校验后执行。
/// <para>
/// **下行有两条通道，一个执行口**（白名单与参数校验都在本类）：
/// 1. **工具通道（首选）**：Agent 调 MCP 工具 `pet_command` → `aipet-mcp` 写 `user://actions.jsonl`
///    → `ActionInbox` 轮询执行并写回执（正文保持干净；Agent 还能拿到真实执行结果）。
/// 2. **文本通道（兼容）**：Agent 在回复里内嵌围栏块 —— 没有 MCP 的 Agent 用这条，任何环境都能兜
///    （ACP 的 `session/prompt` 响应没有自定义字段通道，所以文本是最后的公共通道）。
/// </para>
/// <para>
/// **语法**（围栏块，块内一行一条指令；两种行格式都收）：
/// <code>
/// 好的，我这就去看看喵~
/// ```pet
/// {"cmd":"set_state","state":"think"}
/// set_state state=working
/// speak text=查到了！
/// ```
/// </code>
/// 围栏块**永不显示**在对话里（含流式中途未闭合的块）。
/// </para>
/// <para>
/// **安全（保守默认，见 AGENTS.md §6）**：Agent 文本是**数据**，不是指令。
/// 只有白名单内 + 参数校验通过的才执行，其余一律记录并拒绝。
/// 唯一放宽口是 `agent.aggressive_mode`（默认关，只加 `open_url`；**不实现**任何本地命令执行）。
/// </para>
/// </summary>
public static class PetCommands
{
    public const string 围栏标记 = "pet";

    private const int 每轮上限 = 6;
    private const int 说话字数上限 = 200;
    private const float 链步长上限 = 30f;
    private const int 链步数上限 = 5;
    private const int 网址长度上限 = 200;

    /// <summary>保守白名单（始终可用）。</summary>
    private static readonly HashSet<string> _白名单 = new()
    {
        "set_state", "speak", "play_anim", "set_mode", "queue_chain",
        "set_mood", "soul_get", "soul_set", // 已登记，P3 灵魂层落地前为「接受但未实现」
    };

    /// <summary>激进模式额外放开（默认关，见 AGENTS.md §6 安全边界）。</summary>
    private static readonly HashSet<string> _激进白名单 = new() { "open_url" };

    /// <summary>P3 之前尚未接入实现的命令（接受但明确记「未实现」，不假装成功）。</summary>
    private static readonly HashSet<string> _未实现 = new() { "soul_get", "soul_set" };

    /// <summary>`set_mood` 也接受关键词（不同 Agent 偏好不同写法）。</summary>
    private static readonly Dictionary<string, float> _心情词表 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["excited"] = 95f, ["兴奋"] = 95f,
        ["happy"] = 80f, ["开心"] = 80f, ["高兴"] = 80f,
        ["normal"] = 60f, ["一般"] = 60f, ["平静"] = 60f,
        ["tired"] = 40f, ["累"] = 40f, ["疲惫"] = 40f,
        ["sad"] = 20f, ["难过"] = 20f, ["低落"] = 20f,
    };

    /// <summary>键名别名 → 规范名（不同 Agent 写法不一，宽松收进来）。</summary>
    private static readonly Dictionary<string, string> _键别名 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["command"] = "cmd", ["命令"] = "cmd", ["op"] = "cmd", ["action"] = "cmd",
        ["状态"] = "state",
        ["文本"] = "text", ["say"] = "text", ["msg"] = "text", ["message"] = "text", ["说话"] = "text",
        ["animation"] = "anim", ["动画"] = "anim",
        ["模式"] = "mode",
        ["链"] = "steps", ["chain"] = "steps",
        ["url"] = "url", ["链接"] = "url",
    };

    /// <summary>最近一次执行的日志（探针/测试可读）。</summary>
    public static List<string> 最近日志 { get; private set; } = new();

    /// <summary>累计执行成功的指令数（诊断用）。</summary>
    public static int 累计执行 { get; private set; }

    // ======================= 解析 =======================

    /// <summary>
    /// 拆出「可显示文本」与「指令列表」。
    /// 显示文本已剔除围栏块；**未闭合的块**（流式中途）同样被截掉，所以可直接用于流式显示。
    /// </summary>
    public static (string 显示文本, List<PetCommand> 指令) 解析(string 原文, bool 记日志 = true)
    {
        var 指令 = new List<PetCommand>();
        if (string.IsNullOrEmpty(原文)) return ("", 指令);

        var 显示 = new StringBuilder();
        var 块内 = false;
        foreach (var 行 in 原文.Replace("\r\n", "\n").Split('\n'))
        {
            var 修剪 = 行.Trim();
            if (!块内)
            {
                if (是围栏开头(修剪)) { 块内 = true; continue; } // 围栏行本身不显示
                显示.Append(行).Append('\n');
            }
            else
            {
                if (修剪.StartsWith("```", StringComparison.Ordinal)) { 块内 = false; continue; } // 闭合
                if (修剪.Length == 0) continue;
                var c = 解析一行(修剪, 记日志);
                if (c != null) 指令.Add(c);
                else if (记日志) GD.Print($"[PetCmd] ✗ 无法解析的指令行（已忽略）: {修剪}");
            }
        }
        return (显示.ToString(), 指令);
    }

    /// <summary>
    /// 只要显示文本（流式显示用）。**静默解析**：流式每来一个 chunk 都会整段重解析，
    /// 此时块内 JSON 往往是半截的——必须不报错、不刷日志（实测会刷一屏解析失败）。
    /// </summary>
    public static string 过滤显示(string 原文) => 解析(原文, false).显示文本;

    // 注：原先这里有个「剥掉状态行」的辅助函数（回放时剥离我们注入给 Agent 的 `[桌宠状态]` 行）。
    // 主人 2026-09-16 定稿「**不做主动注入**」后，我们发给 Agent 的就是主人原话 → 该函数与相关逻辑一并删除。

    private static bool 是围栏开头(string 修剪)
        => 修剪.StartsWith("```", StringComparison.Ordinal) &&
           修剪[3..].Trim().Equals(围栏标记, StringComparison.OrdinalIgnoreCase);

    private static PetCommand 解析一行(string 行, bool 记日志 = true)
    {
        var c = new PetCommand { 原文 = 行 };

        if (行.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(行);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    var 值 = p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString() ?? "",
                        JsonValueKind.Number => p.Value.GetRawText(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Null => "",
                        _ => p.Value.GetRawText(),
                    };
                    归位键(c, p.Name, 值);
                }
            }
            catch (JsonException e)
            {
                if (记日志) GD.Print($"[PetCmd]  JSON 行解析失败（已忽略）: {e.Message}");
                return null;
            }
            return string.IsNullOrEmpty(c.Cmd) ? null : c;
        }

        // 键值行：set_state state=think（也容忍 `speak 你好` 这种裸参数 → 落到 text）
        var 段 = 行.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (段.Length == 0) return null;
        c.Cmd = 段[0].Trim();
        var 裸参数 = new StringBuilder();
        for (var i = 1; i < 段.Length; i++)
        {
            var kv = 段[i].Split('=', 2);
            if (kv.Length == 2) 归位键(c, kv[0], kv[1]);
            else 裸参数.Append(裸参数.Length > 0 ? " " : "").Append(段[i]);
        }
        if (裸参数.Length > 0 && c.取值("text").Length == 0) c.Args["text"] = 裸参数.ToString();
        return c;
    }

    private static void 归位键(PetCommand c, string 键, string 值)
    {
        var 规范 = _键别名.TryGetValue(键.Trim(), out var n) ? n : 键.Trim().ToLowerInvariant();
        if (规范 == "cmd") c.Cmd = 值.Trim();
        else c.Args[规范] = 值.Trim();
    }

    // ======================= 执行 =======================

    /// <summary>执行指令列表（白名单 + 参数校验）。返回成功条数。</summary>
    public static int 执行(List<PetCommand> 指令) => 执行(指令, out _);

    /// <summary>执行指令列表，并给出逐条日志。</summary>
    public static int 执行(List<PetCommand> 指令, out List<string> 日志)
    {
        var log = new List<string>();
        var 成功 = 0;
        var 序号 = 0;

        if (指令 == null || 指令.Count == 0) { 日志 = log; 最近日志 = log; return 0; }

        foreach (var c in 指令)
        {
            if (序号++ >= 每轮上限)
            {
                log.Add($"✗ 超过每轮上限（{每轮上限}）→ 忽略：{c.原文}");
                continue;
            }

            var 可用 = _白名单.Contains(c.Cmd) ||
                       (AgentBridge.Options.AggressiveMode && _激进白名单.Contains(c.Cmd));
            if (!可用)
            {
                log.Add($"✗ 不在白名单 → 拒绝：{c.原文}");
                continue;
            }

            if (_未实现.Contains(c.Cmd))
            {
                log.Add($" 未实现（P3 灵魂层）→ 跳过：{c.Cmd}");
                continue;
            }

            string 结果 = null;
            switch (c.Cmd)
            {
                case "set_state":
                {
                    var s = c.取值("state");
                    if (!StateMachine.状态有效(s)) { 结果 = $"未知状态「{s}」"; break; }
                    StateMachine.SetState(s);
                    break;
                }
                case "speak":
                {
                    var t = c.取值("text");
                    if (string.IsNullOrWhiteSpace(t)) { 结果 = "文本为空"; break; }
                    if (t.Length > 说话字数上限) { 结果 = $"文本过长（{t.Length}>{说话字数上限}）"; break; }
                    // 气泡走 BBCode：转义方括号，避免 Agent 文本注入样式/链接
                    Dialogue.显示临时标题(t.Replace("[", "［").Replace("]", "］"));
                    break;
                }
                case "play_anim":
                {
                    var a = c.取值("anim");
                    if (!CharAnim.有动画(a)) { 结果 = $"未知动画「{a}」"; break; }
                    CharAnim.PlayNamed(a);
                    break;
                }
                case "set_mode":
                {
                    var m = c.取值("mode").ToLowerInvariant();
                    if (m is not ("office" or "game")) { 结果 = $"未知模式「{m}」"; break; }
                    ModeManager.SwitchMode(m == "game" ? ModeManager.Mode.Game : ModeManager.Mode.Office);
                    break;
                }
                case "queue_chain":
                {
                    var raw = c.取值("steps");
                    var 段s = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (段s.Length == 0) { 结果 = "缺 steps"; break; }
                    if (段s.Length > 链步数上限) { 结果 = $"链过长（{段s.Length}>{链步数上限}）"; break; }
                    var steps = new List<StateMachine.ChainStep>();
                    foreach (var p in 段s)
                    {
                        var kv = p.Split(':');
                        var st = kv[0].Trim();
                        if (!StateMachine.状态有效(st)) { 结果 = $"链内未知状态「{st}」"; break; }
                        var 秒 = 0f;
                        if (kv.Length > 1 && float.TryParse(kv[1], out var d)) 秒 = Math.Clamp(d, 0f, 链步长上限);
                        steps.Add(new StateMachine.ChainStep(st, 秒));
                    }
                    if (结果 != null) break;
                    StateMachine.EnqueueChain(steps.ToArray());
                    break;
                }
                case "set_mood":
                {
                    var raw = c.取值("mood");
                    if (string.IsNullOrWhiteSpace(raw)) { 结果 = "缺 mood"; break; }
                    if (float.TryParse(raw, out var 数)) { Soul.StatsTable.设心情(Math.Clamp(数, 0f, 100f)); break; }
                    if (!_心情词表.TryGetValue(raw.Trim(), out var 目标)) { 结果 = $"无法识别的 mood「{raw}」"; break; }
                    Soul.StatsTable.设心情(目标);
                    break;
                }
                case "open_url":
                {
                    var u = c.取值("url");
                    if (!(u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                          u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    { 结果 = "只允许 http/https"; break; }
                    if (u.Length > 网址长度上限) { 结果 = $"网址过长（{u.Length}）"; break; }
                    GD.Print($"[PetCmd] ⚠ 激进模式放行 open_url: {u}");
                    OS.ShellOpen(u);
                    break;
                }
                default:
                    结果 = "未接入的分支";
                    break;
            }

            if (结果 != null)
            {
                log.Add($"✗ 参数校验未过 → 拒绝：{c.Cmd}（{结果}）");
                continue;
            }

            成功++;
            累计执行++;
            log.Add($"✓ {c.Cmd} [{string.Join(", ", c.Args)}]");
        }

        foreach (var l in log) GD.Print($"[PetCmd] {l}");
        日志 = log;
        最近日志 = log;
        return 成功;
    }

    /// <summary>便利入口：直接从一段回复文本解析并执行，返回可显示的干净文本。</summary>
    public static string 处理回复(string 回复)
    {
        var (显示, 指令) = 解析(回复);
        if (指令.Count > 0) 执行(指令);
        return 显示.TrimEnd();
    }
}