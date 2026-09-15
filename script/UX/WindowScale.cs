using Godot;

namespace desktop.script.UX;

/// <summary>
/// 滚轮调整桌宠大小（窗口会随之收缩/撑开，正好套住角色）。
/// 旧实现靠缩放整个窗口，与「窗口正好套住角色」的模型冲突，故改为缩放角色本身。
/// 约定：标识符英文，注释中文（见 AGENTS.md §8）。
/// </summary>
public partial class WindowScale : Node
{
    [Export] private float _speed = 0.05f;

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true } mouseEvent) return;
        if (!WindowDrag.IsInValidZone()) return;

        var 增量 = mouseEvent.ButtonIndex switch
        {
            MouseButton.WheelUp => _speed,
            MouseButton.WheelDown => -_speed,
            _ => 0f,
        };
        if (增量 == 0f) return;

        desktop.script.State.StateMachine.NotifyInteraction("scale");
        CharAnim.调整缩放(增量);
    }
}