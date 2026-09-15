using Godot;

namespace desktop.tests;

/// <summary>音频输入设备诊断：列出 Godot 能看到的麦克风设备。</summary>
public partial class AudioProbe : Node
{
    public override void _Ready()
    {
        GD.Print("=== 音频输入设备诊断 ===");
        var 设备 = AudioServer.GetInputDeviceList();
        GD.Print($"[Audio] 输入设备数量 = {设备.Length}");
        foreach (var d in 设备) GD.Print($"[Audio]   - {d}");
        GD.Print($"[Audio] 当前输入设备 = '{AudioServer.InputDevice}'");
        GD.Print($"[Audio] 输出设备数 = {AudioServer.GetOutputDeviceList().Length}");
        GD.Print($"[Audio] 混合率 = {AudioServer.GetMixRate()}");
        GetTree().Quit(0);
    }
}