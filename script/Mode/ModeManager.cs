using System;
using Godot;

namespace desktop.script.Mode;

/// <summary>
/// 模式层（接口期）：办公模式 ↔ 游戏模式 无缝切换，各自保留进度。
/// 桌宠本体即游戏的一部分：办公与游戏不严格隔离，办公行为可作游戏内容。
/// 未来横板动作游戏玩法挂在 Game 分支下；至今只实现接口，不实现玩法。
/// 切换锚点(待扩展): 点击桌宠 -> SwitchMode(Game); ESC/手势 -> 切回办公。
/// 约定：标识符英文，注释中文（见 AIPet-Agent.md §8）。
/// </summary>
public partial class ModeManager : Node
{
    public enum Mode { Office, Game }

    public static ModeManager Instance { get; private set; }
    public static Mode CurrentMode { get; private set; } = Mode.Office;

    /// <summary>模式切换事件（参数为目标模式）。场景/UI 监听做表现接管。</summary>
    public static event Action<Mode> ModeChanged;

    public override void _Ready()
    {
        Instance = this;
    }

    public static void SwitchMode(Mode target)
    {
        if (CurrentMode == target) return;
        SaveProgress(); // 切走前先存
        CurrentMode = target;
        // TODO(骨架): Game 模式 -> 加载游戏场景/隐藏办公UI; Office 模式 -> 反向
        // 桌宠本体(位置/动画)不销毁, 只换表现壳 (无缝切换的关键)
        ModeChanged?.Invoke(target);
    }

    public static void SaveProgress()
    {
        // TODO(骨架): 写 user://game/save.json，仅存游戏模式相关状态
        // (当前为接口期, 游戏无状态, 仅建目录/占位)
    }

    public static void LoadProgress()
    {
        // TODO(骨架): 读 user://game/save.json -> 应用到游戏场景
    }

    // TODO(骨架):
    // - GameSession: 游戏模式专用数据容器(关卡/血量/道具...), 不入灵魂表
    // - 无缝切换实现: ModeChanged 事件 -> 场景树挂载/卸载, 渐变过渡
    // - 办公即游戏: 办公动作(如完成待办)可产生游戏内奖励, 挂 Game 分支
}