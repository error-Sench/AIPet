using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using Godot;

namespace desktop.script.Agent;

/// <summary>
/// ACP (Agent Client Protocol) 后端实现：JSON-RPC 2.0 over stdio。
/// 定位：**某一种** Agent 接入方式（Hermes 是它的一个实例），非核心依赖。
/// 已实测（2026-09-15）：initialize -> authenticate -> session/new -> session/prompt 全通；
///   流式走 session/update 通知（sessionUpdate=agent_message_chunk, content.text）。
/// 线程模型：stdout 后台线程逐行读 -> ConcurrentQueue；主线程 Poll() 取（不阻塞渲染）。
/// 约定：标识符英文，注释中文（见 AIPet-Agent.md §8）。
/// </summary>
public sealed class AcpClient : IAgentBackend
{
    public const int ProtocolVersion = 1;

    public string Name => "hermes-acp";
    public bool IsRunning => _proc is { HasExited: false };
    public bool IsReady => !string.IsNullOrEmpty(SessionId);
    public string SessionId { get; private set; }

    public event Action<string> OnReplyChunk;
    public event Action<bool, string> OnHistoryChunk;
    public event Action<string> OnTurnEnd;
    public event Action<string> OnError;

    /// <summary>「本轮 prompt 进行中」标记：区分实时回复与「会话恢复时回放的历史」。</summary>
    private volatile bool _提示中;
    private volatile bool _本轮有内容;   // 本轮 prompt 是否真的收到过内容（自愈判定用）

    private Process _proc;
    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly object _writeLock = new();
    private readonly Dictionary<int, Action<JsonNode>> _pending = new();
    private int _nextId = 1;
    private volatile bool _disposing;
    private bool _强制新会话;

    public bool Start(AgentOptions options)
    {
        try
        {
            _强制新会话 = options.ForceNewSession;
            // cwd 必须是干净目录：含 AGENTS.md 的目录会把工程契约注入 system prompt。
            var cwd = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? "." : options.WorkingDirectory;
            var psi = new ProcessStartInfo
            {
                FileName = options.Executable,
                Arguments = string.IsNullOrWhiteSpace(options.Arguments) ? "acp" : options.Arguments,
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            _proc = Process.Start(psi);
            if (_proc == null) { OnError?.Invoke("无法启动 ACP 子进程"); return false; }

            new Thread(PumpStdout) { IsBackground = true, Name = "acp-stdout" }.Start();
            new Thread(PumpStderr) { IsBackground = true, Name = "acp-stderr" }.Start();

            SendRequest("initialize", new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["clientCapabilities"] = new JsonObject
                {
                    ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                },
                ["clientInfo"] = new JsonObject { ["name"] = "aipet", ["version"] = "0.1" },
            }, node =>
            {
                var methods = node?["result"]?["authMethods"] as JsonArray;
                var first = methods is { Count: > 0 } ? methods[0]?["id"]?.GetValue<string>() : null;
                if (!string.IsNullOrEmpty(first)) Authenticate(first, cwd);
                else 建立会话(cwd);
            });
            return true;
        }
        catch (Exception e)
        {
            OnError?.Invoke($"启动失败: {e.Message}");
            return false;
        }
    }

    private void Authenticate(string methodId, string cwd)
    {
        SendRequest("authenticate", new JsonObject { ["methodId"] = methodId }, _ => 建立会话(cwd));
    }

    /// <summary>建立会话：优先「恢复上次会话」，失败/无记录才新建（避免每次启动都失忆）。</summary>
    private void 建立会话(string cwd)
    {
        var 旧Id = _强制新会话 ? null : 读取会话ID();
        if (!string.IsNullOrEmpty(旧Id))
        {
            GD.Print($"[AcpClient] 尝试恢复上次会话: {旧Id}");
            SendRequest("session/resume", new JsonObject
            {
                ["sessionId"] = 旧Id,
                ["cwd"] = cwd,
                ["mcpServers"] = new JsonArray(),
            }, node =>
            {
                if (node?["error"] != null)
                {
                    GD.Print($"[AcpClient] 恢复失败（{node["error"]!.ToJsonString()}），改为新建会话");
                    新建会话(cwd);
                    return;
                }
                SessionId = node?["result"]?["sessionId"]?.GetValue<string>() ?? 旧Id;
                保存会话ID(SessionId);
                GD.Print($"[AcpClient] 已恢复会话: {SessionId}");
            });
            return;
        }
        新建会话(cwd);
    }

    private void 新建会话(string cwd)
    {
        SendRequest("session/new", new JsonObject
        {
            ["cwd"] = cwd,
            ["mcpServers"] = new JsonArray(),
        }, node =>
        {
            SessionId = node?["result"]?["sessionId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(SessionId)) { OnError?.Invoke("session/new 未返回 sessionId"); return; }
            保存会话ID(SessionId);
            GD.Print($"[AcpClient] 已新建会话: {SessionId}");
        });
    }

    // ———— 会话 ID 持久化（跨启动复用，避免每次新建会话） ————

    private static string 会话记录路径 => ProjectSettings.GlobalizePath("user://agent_session.json");

    private static string 读取会话ID()
    {
        try
        {
            if (!File.Exists(会话记录路径)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(会话记录路径));
            if (doc.RootElement.TryGetProperty("lastSessionId", out var v)) return v.GetString();
        }
        catch (Exception e) { GD.PrintErr($"[AcpClient] 读会话记录失败: {e.Message}"); }
        return null;
    }

    /// <summary>丢弃记录的会话（Agent 侧已失效时调用，下次发言会新建会话）。</summary>
    private static void 删除会话ID()
    {
        try { if (File.Exists(会话记录路径)) File.Delete(会话记录路径); }
        catch (System.Exception e) { GD.PrintErr($"[AcpClient] 删除会话记录失败: {e.Message}"); }
    }

    private static void 保存会话ID(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        try
        {
            var dir = Path.GetDirectoryName(会话记录路径);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(会话记录路径, $"{{\n  \"lastSessionId\": \"{id}\"\n}}\n", Encoding.UTF8);
        }
        catch (Exception e) { GD.PrintErr($"[AcpClient] 保存会话记录失败: {e.Message}"); }
    }

    public bool Ask(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (string.IsNullOrEmpty(SessionId)) { OnError?.Invoke("尚无会话，无法发送"); return false; }
        var prompt = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
        // 标记「本轮进行中」：只有在这之后到达的 agent_message_chunk 才算实时回复，
        // 其余（会话恢复时回放的历史）走 OnHistoryChunk。必须在发送前置位。
        _提示中 = true;
        _本轮有内容 = false;
        SendRequest("session/prompt", new JsonObject
        {
            ["sessionId"] = SessionId,
            ["prompt"] = prompt,
        }, node =>
        {
            _提示中 = false;
            // 自愈（实测 bug）：Agent 侧可能已经没有这个会话（换过数据目录/Agent 重装/会话过期）→
            // 响应里带 error（如 "prompt: session xxx not found"），此时**丢弃旧会话 ID**，
            // 下次发言自动新建会话；否则桌宠会从此"哑巴"（每轮都被拒，回复恒为空）。
            if (node?["error"] != null)
            {
                var 原因 = node["error"]!["message"]?.GetValue<string>() ?? node["error"]!.ToJsonString();
                GD.PrintErr($"[AcpClient] 发言被拒：{原因} → 丢弃失效会话，下次自动新建");
                删除会话ID();
                SessionId = "";
                OnError?.Invoke($"会话失效已重建（{原因}）");
                OnTurnEnd?.Invoke("session-invalid");
                return;
            }
            var reason = node?["result"]?["stopReason"]?.GetValue<string>() ?? "unknown";
            // 实测：Agent 侧会话不存在时，ACP 把它包成 `stopReason=refusal` **且回复为空**（不是 error 字段）。
            // 「refusal + 零内容」= 会话失效 → 丢弃，下次发言自动新建（否则桌宠从此哑巴）。
            if (reason == "refusal" && !_本轮有内容)
            {
                GD.PrintErr("[AcpClient] 本轮被拒且无任何内容 → 判定会话失效，丢弃旧会话（下次自动新建）");
                删除会话ID();
                SessionId = "";
                OnError?.Invoke("会话失效已重建（Agent 侧没有这个会话了）");
                OnTurnEnd?.Invoke("session-invalid");
                return;
            }
            OnTurnEnd?.Invoke(reason);
        });
        return true;
    }

    private void SendRequest(string method, JsonObject p, Action<JsonNode> onDone)
    {
        int id = Interlocked.Increment(ref _nextId);
        _pending[id] = onDone;
        WriteLine(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = p,
        }.ToJsonString());
    }

    private void WriteLine(string json)
    {
        lock (_writeLock)
        {
            try { _proc?.StandardInput.WriteLine(json); _proc?.StandardInput.Flush(); }
            catch (Exception e) { OnError?.Invoke($"写入失败: {e.Message}"); }
        }
    }

    /// <summary>主线程每帧调用：处理收到的响应与流式通知。</summary>
    public void Poll()
    {
        while (_incoming.TryDequeue(out var line))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode node;
            try { node = JsonNode.Parse(line); } catch { continue; }
            if (node == null) continue;

            var id = node["id"];
            if (id != null && node["method"] == null)
            {
                int rid = id.GetValue<int>();
                if (_pending.Remove(rid, out var cb))
                {
                    if (node["error"] != null) OnError?.Invoke(node["error"]!.ToJsonString());
                    cb(node);
                }
                continue;
            }

            if (node["method"]?.GetValue<string>() == "session/update")
            {
                var update = node["params"]?["update"];
                var kind = update?["sessionUpdate"]?.GetValue<string>();
                var text = update?["content"]?["text"]?.GetValue<string>();
                if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(text)) continue;

                var 是用户 = kind == "user_message_chunk";
                if (kind != "agent_message_chunk" && !是用户) continue; // thought/usage/commands 等一律不显示

                if (_提示中)
                {
                    // 本轮的实时回复（只显示助手侧；用户侧是我们自己发的，UI 已显示）
                    if (!是用户) { _本轮有内容 = true; OnReplyChunk?.Invoke(text); }
                }
                else
                {
                    // 会话恢复时回放的历史：必须与实时流式分开，否则会被合并成一大段
                    OnHistoryChunk?.Invoke(是用户, text);
                }
            }
        }
    }

    private void PumpStdout()
    {
        try
        {
            string line;
            while ((line = _proc.StandardOutput.ReadLine()) != null) _incoming.Enqueue(line);
        }
        catch { }
        finally { if (!_disposing && _proc is { HasExited: false }) OnError?.Invoke("ACP 子进程已断开"); }
    }

    private void PumpStderr()
    {
        try
        {
            string line;
            while ((line = _proc.StandardError.ReadLine()) != null) GD.Print($"[acp] {line}");
        }
        catch { }
    }

    public void Dispose()
    {
        _disposing = true;
        _提示中 = false;
        try
        {
            if (_proc is { HasExited: false })
            {
                try { _proc.StandardInput.Close(); } catch { }
                if (!_proc.WaitForExit(2000)) _proc.Kill(entireProcessTree: true);
            }
        }
        catch { }
        _proc?.Dispose();
        _proc = null;
    }
}