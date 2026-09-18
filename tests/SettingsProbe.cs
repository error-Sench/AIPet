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

    /// <summary>切到指定页签（走 SettingsWindow 的 API，避免探针自己爬节点路径）。</summary>
    private void 切页(int 序号) => desktop.script.UX.SettingsWindow.探针_切页(序号);

    /// <summary>把配置窗当前画面存成 PNG（Window 本身是 Viewport，可直接取纹理）。</summary>
    private void 截页(int 序号, string 文件名)
    {
        var w = GetChild(0).GetNodeOrNull("SettingsWindow") as Window;
        if (w == null) { GD.PrintErr("[SP] 配置窗节点缺失"); return; }
        var 图 = w.GetTexture()?.GetImage();
        if (图 == null) { GD.PrintErr("[SP] 截图失败：纹理为空"); return; }
        var 路径 = ProjectSettings.GlobalizePath($"user://{文件名}");
        图.SavePng(路径);
        GD.Print($"[SP] 截图[{序号}] {文件名}: {路径} ({图.GetWidth()}x{图.GetHeight()})");
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
            // 逐页截屏：常规 / 行为 / 语音 / 高级（各存一张 PNG，供人工与视觉复核）
            // 切页与截图**必须隔一帧**：TabContainer 换页在下一帧才生效，同帧截图会拍到上一页（实测）
            截页(0, "settings_tab0_general.png");
            desktop.script.UX.ChatBox.显示();
        }
        else if (_frames == 18) { 切页(1); }
        else if (_frames == 20) { 截页(1, "settings_tab1_behavior.png"); }
        else if (_frames == 24) { 切页(2); }
        else if (_frames == 26) { 截页(2, "settings_tab2_voice.png"); }
        else if (_frames == 30) { 切页(3); }
        else if (_frames == 32) { 截页(3, "settings_tab3_advanced.png"); }
        else if (_frames == 36) { 切页(0); }
        else if (_frames == 40)
        {
            GD.Print("[SP] 截图就绪，保持打开 15 秒（可对桌面整屏复核）");
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