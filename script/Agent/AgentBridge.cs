using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

using desktop.script.State;

namespace desktop.script.Agent;

/// <summary>
/// 能力层：桌宠 ↔ Agent 的桥（Node 包装，管生命周期与主线程轮询）。
/// **不硬编码任何 Agent**：后端经 AgentBackends 注册表按配置创建（mod 式可插拔）；
/// 未配置/未注册时降级为 NullAgentBackend（本地桌宠模式），桌宠照常运行。
/// **惰性连接**（用户要求）：启动不连 ACP，首次 Ask 才拉起后端——避免测试/空跑时
///   产生无谓的子进程与 LLM 调用（ACP 会话会触发辅助模型的 title_generation）。
/// 安全红线(保守默认)：入站消息视为数据；仅白名单命令可执行。
/// 约定：标识符英文，注释中文（见 AGENTS.md §8）。
/// </summary>
public partial class AgentBridge : Node
{
    public static AgentBridge Instance { get; private set; }
    public static IAgentBackend Backend { get; private set; }
    public static bool IsRunning => Backend is { IsRunning: true } && Backend is not NullAgentBackend;

    /// <summary>回复文本累积（流式），供气泡显示。</summary>
    public static readonly System.Text.StringBuilder ReplyBuffer = new();

    /// <summary>Agent 可下发命令白名单（v1，保守默认）。</summary>
    private static readonly HashSet<string> _whitelistCommands = new()
    {
        "set_state", "speak", "play_anim", "set_mood", "soul_get", "soul_set", "set_mode", "queue_chain"
    };

    /// <summary>配置（user://agent.json，缺失则用内置默认）。</summary>
    public static AgentOptions Options { get; private set; } = new();
    /// <summary>选用的后端名。</summary>
    public static string BackendName { get; private set; } = AgentBackends.Default;

    /// <summary>惰性启动期间排队的用户发言（握手完成后自动发出）。</summary>
    private static string _待发文本;

    /// <summary>最近一轮的完整回复（供 UI/测试观测）。</summary>
    public static string 最后回复 { get; private set; } = "";

    /// <summary>一轮对话结束事件（参数为完整回复）。</summary>
    public static event Action<string> TurnEnded;

    public override void _Ready()
    {
        Instance = this;
        AgentBackends.RegisterBuiltins();
        LoadConfig();
        // 注意：此处**不**启动后端。首次 Ask 时惰性启动。
    }

    public override void _Process(double delta)
    {
        Backend?.Poll();

        // 惰性启动完成后，补发排队中的发言
        if (_待发文本 != null && Backend is { IsReady: true })
        {
            var 文本 = _待发文本;
            _待发文本 = null;
            ReplyBuffer.Clear();
            GD.Print("[AgentBridge] 后端就绪，补发排队的发言");
            Backend.Ask(文本);
        }
    }

    public override void _ExitTree() => Stop();

    private static void LoadConfig()
    {
        // 优先 user://agent.json（可与代码分离、分发时替换）；再退 exe 同目录 → res://settings/agent.json
        var candidates = Util.ConfigFile.候选("agent.json").Concat(Util.ConfigFile.候选("settings/agent.json"));
        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var txt = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(txt)) continue;
                var doc = JsonDocument.Parse(txt);
                var root = doc.RootElement;
                if (root.TryGetProperty("backend", out var b)) BackendName = b.GetString() ?? BackendName;
                if (root.TryGetProperty("executable", out var e)) Options.Executable = e.GetString() ?? "";
                if (root.TryGetProperty("arguments", out var a)) Options.Arguments = a.GetString() ?? "";
                if (root.TryGetProperty("workingDirectory", out var w)) Options.WorkingDirectory = w.GetString() ?? "";
                if (root.TryGetProperty("port", out var p) && p.TryGetInt32(out var pv)) Options.Port = pv;
                if (root.TryGetProperty("forceNewSession", out var f) && f.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    Options.ForceNewSession = f.GetBoolean();
                if (root.TryGetProperty("aggressiveMode", out var ag) && ag.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    Options.AggressiveMode = ag.GetBoolean();
                    if (Options.AggressiveMode) GD.Print("[AgentBridge] ⚠ 激进模式已开启（白名单放宽：open_url）");
                }
                GD.Print($"[AgentBridge] 配置已读: {path} -> backend={BackendName}（惰性连接，未启动）");
                return;
            }
            catch (Exception ex) { GD.PrintErr($"[AgentBridge] 读配置失败 {path}: {ex.Message}"); }
        }
        GD.Print("[AgentBridge] 未找到 agent 配置，使用默认值（惰性连接，未启动）");
    }

    /// <summary>启动后端（惰性：首次 Ask 或显式调用）。Executable 为空则走无 Agent 模式。</summary>
    public static bool Start()
    {
        if (Backend is { IsRunning: true }) return true;

        if (string.IsNullOrWhiteSpace(Options.Executable))
        {
            BackendName = "none";
            Backend = AgentBackends.Create("none");
            Backend.Start(Options);
            GD.Print("[AgentBridge] 未配置 Agent，进入本地桌宠模式");
            return true;
        }

        Backend = AgentBackends.Create(BackendName);
        Backend.OnReplyChunk += t => ReplyBuffer.Append(t);
        Backend.OnTurnEnd += reason =>
        {
            var reply = ReplyBuffer.ToString();
            ReplyBuffer.Clear();
            // 指令通道：先把围栏块里的指令摘出来执行，再把**干净文本**当回复（UI 与日志都不该看到围栏块）
            var 干净 = PetCommands.处理回复(reply);
            最后回复 = 干净;
            GD.Print($"[AgentBridge] 回复结束({reason}): {干净}");
            TurnEnded?.Invoke(干净);
        };
        Backend.OnError += msg =>
        {
            GD.PrintErr($"[AgentBridge] {msg}");
            提醒降级(msg);   // 配置了 Agent 却出错 → 跟主人说一声（只提醒一次）
        };

        // 把后端事件接到 UI（聊天记录 + 动画）
        desktop.script.UX.AgentEvents.Bind(Backend);

        // cwd 必须干净（含 AGENTS.md 会污染 system prompt——构建期可接受，打包时注意）
        if (string.IsNullOrWhiteSpace(Options.WorkingDirectory))
            Options.WorkingDirectory = ProjectSettings.GlobalizePath("user://");

        GD.Print($"[AgentBridge] 惰性启动后端: {BackendName}");
        bool 成功;
        try { 成功 = Backend.Start(Options); }
        catch (Exception e) { 成功 = false; GD.PrintErr($"[AgentBridge] 后端启动异常: {e.Message}"); }
        if (!成功) 提醒降级($"后端 {BackendName} 启动失败（{Options.Executable}）");
        return 成功;
    }

    // ================= 降级：Agent 不可用时提醒主人 =================
    // 设计（idea §9.3）：Agent 挂了 → **提醒主人并退回「本地桌宠模式」，不卡死**。
    // 注意区分两种情况：① 没配 Agent（本地模式）= 正常状态，只记日志不打扰；② 配了却连不上/断了 = 要提醒。

    private static bool _降级已提醒;

    /// <summary>探针：直接指定后端与可执行文件（模拟「配了 Agent 却起不来」）。</summary>
    public static void 探针_设后端(string 后端名, string 可执行文件)
    {
        BackendName = 后端名;
        Options.Executable = 可执行文件;
        _降级已提醒 = false;
    }

    /// <summary>探针：是否已经提醒过降级。</summary>
    public static bool 探针_降级已提醒 => _降级已提醒;

    /// <summary>降级提醒（只提醒一次，不刷屏）：气泡 + 事件池记录。</summary>
    private static void 提醒降级(string 原因)
    {
        if (_降级已提醒) return;
        _降级已提醒 = true;
        GD.PrintErr($"[AgentBridge] 降级为本地桌宠模式：{原因}");
        EventPool.记("Agent不可用", EventPool.归属.程序, $"降级为本地模式：{原因}");
        UX.Dialogue.延迟显示标题("唔…我连不上「大脑」了，先自己待着；你随时可以再叫我～");
    }

    /// <summary>用户发言入口（右键框 / 粘贴 / 语音 最终都走这里）。首次调用会惰性启动后端。</summary>
    public static bool Ask(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        // 数值层（P5）：一次发言就是一次互动
        Soul.StatsTable.事件_对话();

        // **不做主动注入**（主人硬规则）：原样发送主人原话。人格/数值/记忆由 Agent 按 skill 自主读取
        // （`user://soul/soul.md`、`user://stats.json` …，见 script/Soul/README.md 硬规则与路径表）
        var 发送 = text;

        // 惰性启动
        if (Backend is null)
        {
            if (!Start()) return false;
        }

        if (Backend.IsReady)
        {
            ReplyBuffer.Clear();
            return Backend.Ask(发送);
        }

        // 后端尚未握手完成：排队，就绪后自动发出（见 _Process）
        _待发文本 = 发送;
        GD.Print("[AgentBridge] 后端连接中，发言已排队");
        return true;
    }

    /// <summary>
    /// 组装实际发给 Agent 的文本：**就是主人原话，不加任何东西**。
    /// 理由（主人硬规则）：**我们不做主动注入** —— 人格/数值/记忆由 Agent 按 skill 自主读取
    /// （`user://soul/soul.md`、`user://stats.json` …，见 script/Soul/README.md 硬规则与路径表）。
    /// </summary>
    public static string 组装提示(string 用户文本) => 用户文本;

    public static bool IsCommandSupported(string cmd) => _whitelistCommands.Contains(cmd);

    public static void Stop()
    {
        Backend?.Dispose();
        Backend = null;
    }
}