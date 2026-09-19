using System;
using Godot;

namespace desktop.script.Agent;

/// <summary>
/// 「无 Agent」降级后端：未配置/未注册任何 Agent 时使用。
/// 设计约束（用户确认）：分发场景下别人可能没有 Agent——桌宠必须照常运行，不卡死。
/// 行为：不建立任何连接；Ask 时回一句本地兜底话术，桌宠退回「本地桌宠模式」。
/// 约定：标识符英文，注释中文（见 AIPet-Agent.md §8）。
/// </summary>
public sealed class NullAgentBackend : IAgentBackend
{
    public string Name => "none";
    public bool IsRunning => false;
    public bool IsReady => true; // 「就绪」但无能力——Ask 走本地兜底

    public event Action<string> OnReplyChunk;
    public event Action<bool, string> OnHistoryChunk;
    public event Action<string> OnTurnEnd;
    public event Action<string> OnError;

    /// <summary>本地兜底话术（可被 mod 覆盖）。</summary>
    public static string FallbackReply { get; set; } = "（我这边没有连上大脑呢…先陪你待着吧）";

    public bool Start(AgentOptions options)
    {
        GD.Print("[NullAgentBackend] 无 Agent 模式（本地桌宠模式）");
        return true;
    }

    public bool Ask(string text)
    {
        OnReplyChunk?.Invoke(FallbackReply);
        OnTurnEnd?.Invoke("no_agent");
        return false;
    }

    public void Poll() { }
    public void Dispose() { }
}