using Godot;

namespace desktop.tests;

/// <summary>工具栏窗口诊断：弹出/关闭是否正常，以及子窗口嵌入状态。</summary>
public partial class ToolBarProbe : Node
{
    private int _帧;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        AddChild(ps.Instantiate());
        GD.Print("=== ToolBarProbe ===");
    }

    public override void _Process(double delta)
    {
        _帧++;
        if (_帧 == 3)
        {
            GD.Print($"[Probe] 根视口嵌入子窗口 = {GetTree().Root.GuiEmbedSubwindows}");
            desktop.script.UX.ToolBar.显示();
        }
        else if (_帧 == 5)
        {
            var tb = GetChild(0).GetNodeOrNull("ToolBar");
            if (tb is Window w)
            {
                GD.Print($"[Probe] Window: visible={w.Visible} pos={w.Position} size={w.Size} " +
                         $"borderless={w.Borderless} unresizable={w.Unresizable} exclusive={w.Exclusive}");
                Dump(w, "   ");
            }
            else GD.PrintErr("[Probe] 找不到 ToolBar Window 节点");
        }
        else if (_帧 == 7)
        {
            GD.Print("[Probe] 调用关闭…");
            desktop.script.UX.ToolBar.隐藏();
        }
        else if (_帧 == 9)
        {
            var tb = GetChild(0).GetNodeOrNull("ToolBar");
            GD.Print($"[Probe] 关闭后 visible = {(tb as Window)?.Visible}");
            GD.Print("[Probe] DONE");
            GetTree().Quit(0);
        }
    }

    private static void Dump(Node n, string indent)
    {
        GD.Print($"{indent}- {n.Name} ({n.GetType().Name})");
        foreach (var c in n.GetChildren()) Dump(c, indent + "  ");
    }
}