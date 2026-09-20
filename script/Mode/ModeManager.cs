using System;
using desktop.script.Game;
using Godot;

namespace desktop.script.Mode;

/// <summary>
/// 模式层：办公模式 ↔ 游戏模式 无缝切换，各自保留进度。
/// 桌宠本体即游戏的一部分：办公与游戏不严格隔离，办公行为可作游戏内容。
/// 横板动作玩法本体在 `script/Game/` 开发区；本类保持轻量接口（切换 + 进度存取）。
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
        // 游戏进度存盘（GameSession 独立容器 → user://game/save.json；切换前自动调）
        GameSession.存盘();
    }

    public static void LoadProgress()
    {
        // 读档进 GameSession（把进度铺到游戏场景是挂载侧的事，见 script/Game/）
        GameSession.载入();
    }

    // TODO(骨架):
    // - 无缝切换实现: ModeChanged 事件 -> 场景树挂载/卸载, 渐变过渡
    // - 办公即游戏: 办公动作(如完成待办)可产生游戏内奖励, 挂 Game 分支
}