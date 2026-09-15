using Godot;

namespace desktop.tests;

/// <summary>
/// 配置窗探针（**需非 headless 运行**，否则拿不到真实窗口几何与画面）：
/// 加载 game.tscn -> 唤出配置窗 -> 打印真实几何 -> 把窗口画面存成 PNG 供人工/视觉复核
/// -> 保持打开约 15 秒（便于对桌面做整屏截图，复核窗口装饰/标题栏）-> 退出。
/// 用法：Godot_v4.7.2-stable_mono_win64_console.exe --path D:/Games/Github/AIPet res://tests/SettingsProbe.tscn
/// </summary>
public partial class SettingsProbe : Node
{
    private int _frames;
    private const int 退出帧 = 900; // 约 15 秒

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== SettingsProbe: 场景已实例化 ===");
    }

    public override void _Process(double delta)
    {
        _frames++;
        if (_frames == 4)
        {
            desktop.script.UX.SettingsWindow.显示();
            var w = GetChild(0).GetNodeOrNull("SettingsWindow") as Window;
            if (w == null) { GD.PrintErr("[SP] 配置窗节点缺失"); GetTree().Quit(1); return; }
            GD.Print($"[SP] 配置窗 几何: pos={w.Position} size={w.Size} min={w.MinSize} 无边框={w.Borderless}");
            GD.Print($"[SP] 语言项={desktop.script.UX.SettingsWindow.语言项.Count} 目录行={desktop.script.UX.SettingsWindow.路径文本.Count}");
            foreach (var p in desktop.script.UX.SettingsWindow.路径文本) GD.Print($"[SP]   目录: {p}");
        }
        else if (_frames == 12)
        {
            // 配置窗画面 -> PNG（Window 本身是 Viewport，可直接取纹理）
            var w = GetChild(0).GetNodeOrNull("SettingsWindow") as Window;
            if (w != null)
            {
                var 图 = w.GetTexture()?.GetImage();
                if (图 != null)
                {
                    var 路径 = ProjectSettings.GlobalizePath("user://settings_window.png");
                    图.SavePng(路径);
                    GD.Print($"[SP] 配置窗截图: {路径} ({图.GetWidth()}x{图.GetHeight()})");
                }
                else GD.PrintErr("[SP] 配置窗截图失败：纹理为空");
            }
            desktop.script.UX.ChatBox.显示();
        }
        else if (_frames == 20)
        {
            var c = GetChild(0).GetNodeOrNull("ChatBox") as Window;
            if (c != null)
            {
                var 图 = c.GetTexture()?.GetImage();
                if (图 != null)
                {
                    var 路径 = ProjectSettings.GlobalizePath("user://chat_window.png");
                    图.SavePng(路径);
                    GD.Print($"[SP] 聊天窗截图: {路径} ({图.GetWidth()}x{图.GetHeight()})");
                }
            }
            GD.Print("[SP] 截图就绪，保持打开 15 秒（可对桌面整屏复核）");
        }
        else if (_frames == 退出帧)
        {
            GD.Print("[SP] DONE");
            GetTree().Quit(0);
        }
    }
}