using System;

namespace desktop.script.Agent;

/// <summary>
/// 能力层后端抽象：**不绑定任何具体 Agent**。
/// 设计约束（用户确认）：Hermes 只是「一种接入方式」，等同 mod；不得硬编码。
/// 任何 Agent（本地/远程/无）都通过实现本接口接入；桌宠核心只认这个接口。
/// 约定：标识符英文，注释中文（见 AGENTS.md §8）。
/// </summary>
public interface IAgentBackend
{
    /// <summary>后端名（用于配置选择，如 "hermes-acp" / "http" / "none"）。</summary>
    string Name { get; }

    /// <summary>进程/连接是否存活。</summary>
    bool IsRunning { get; }

    /// <summary>是否已就绪（会话建立完成，可接收 Ask）。</summary>
    bool IsReady { get; }

    /// <summary>流式回复块。</summary>
    event Action<string> OnReplyChunk;
    /// <summary>
    /// 历史回放块（是否为用户发言, 文本）。
    /// 会话恢复（resume/load）时 Agent 会先把整段历史回放出来，**必须与实时回复分开路由**：
    /// 否则 UI 会把多轮历史当成「一轮流式回复」累加、最终合并成一大段（实测踩过）。
    /// </summary>
    event Action<bool, string> OnHistoryChunk;
    /// <summary>一轮对话结束（reason）。</summary>
    event Action<string> OnTurnEnd;
    /// <summary>错误/断开。</summary>
    event Action<string> OnError;

    /// <summary>启动后端（建立连接/子进程）。</summary>
    bool Start(AgentOptions options);

    /// <summary>发送一句话。</summary>
    bool Ask(string text);

    /// <summary>主线程每帧调用（非阻塞处理入站消息）。</summary>
    void Poll();

    /// <summary>关闭并清理。</summary>
    void Dispose();
}

/// <summary>后端启动参数（由配置装配，避免后端自行读全局配置）。</summary>
public sealed class AgentOptions
{
    /// <summary>可执行文件路径（如 hermes.exe）。</summary>
    public string Executable { get; set; } = "";
    /// <summary>命令行参数（如 "acp" 或 "--profile aipet acp"）。</summary>
    public string Arguments { get; set; } = "";
    /// <summary>工作目录（**必须干净**：含 AGENTS.md 的目录会污染 system prompt）。</summary>
    public string WorkingDirectory { get; set; } = "";
    /// <summary>端口（HTTP 类后端用）。</summary>
    public int Port { get; set; } = 8765;

    /// <summary>强制新建会话（忽略上次会话记录）。默认 false = 复用上次会话。</summary>
    public bool ForceNewSession { get; set; }

    /// <summary>
    /// 实验性激进开关（**默认关**）：放宽指令白名单（当前只加 `open_url`）。
    /// 见 AGENTS.md §6 安全边界。注意：**刻意不实现**任何「执行本地命令」能力——那需要一个专门设计与主人明确授权。
    /// </summary>
    public bool AggressiveMode { get; set; }

    /// <summary>
    /// 是否在发给 Agent 的文本后附一行「桌宠当前状态」（P5 数值层 → 能力层）。默认 true。
    /// **只加数据，不加人格**：人格由 Agent 自己的 SOUL.md 决定。关掉即完全不注入。
    /// </summary>
    public bool InjectStats { get; set; } = true;
}