using System;
using System.Collections.Generic;
using Godot;

namespace desktop.script.Agent;

/// <summary>
/// 能力层后端注册表：mod 式可插拔。
/// 设计约束：桌宠核心不硬编码任何具体 Agent；后端按名字注册 + 配置选择。
/// 第三方 mod 可在 `_Ready` 时 Register("myname", () => new MyBackend()) 接入自家 Agent。
/// 约定：标识符英文，注释中文（见 AGENTS.md §8）。
/// </summary>
public static class AgentBackends
{
    private static readonly Dictionary<string, Func<IAgentBackend>> _factories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>默认后端名（配置缺失时使用）。</summary>
    public const string Default = "hermes-acp";

    /// <summary>注册一个后端工厂（重复注册以「后者覆盖」为准，便于 mod 替换内置实现）。</summary>
    public static void Register(string name, Func<IAgentBackend> factory)
    {
        if (string.IsNullOrWhiteSpace(name) || factory == null) return;
        _factories[name.Trim()] = factory;
    }

    /// <summary>按名创建后端；未注册则回退到「无 Agent」后端（优雅降级）。</summary>
    public static IAgentBackend Create(string name)
    {
        if (!string.IsNullOrWhiteSpace(name) && _factories.TryGetValue(name.Trim(), out var f))
            return f();
        GD.PrintErr($"[AgentBackends] 未注册的后端 '{name}'，降级为无 Agent 模式");
        return new NullAgentBackend();
    }

    public static bool IsRegistered(string name) => _factories.ContainsKey(name ?? "");

    public static IEnumerable<string> Names => _factories.Keys;

    /// <summary>注册内置后端（启动时调用一次）。</summary>
    public static void RegisterBuiltins()
    {
        Register("hermes-acp", () => new AcpClient());
        Register("none", () => new NullAgentBackend());
    }
}