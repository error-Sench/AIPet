using System;
using System.IO;
using System.Text.Json;
using Godot;
using desktop.script.Loader;
using desktop.script.UX;

namespace desktop.tests;

/// <summary>
/// ToolBarProbe（headless）：工具栏窗口 + mod 工具格 + 内置动作。
/// 验证：扫描 mods/toolbar 找到「网速监控」（action=netspeed）；点击开/关网速气泡
/// （按钮标题「·开 / ·关」）；配置 enabled 持久化；启动恢复。
/// 探针不碰真实数据：网速配置走临时文件（打包审计 B6）。
/// </summary>
public partial class ToolBarProbe : Node
{
    private int _帧;
    private int _失败;
    private Window _工具栏;

    private static string 临时配置 => ProjectSettings.GlobalizePath("user://_probe_netspeed.json");

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[TB] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[TB] FAIL  {描述}"); }
    }

    public override void _Ready()
    {
        GD.Print("=== ToolBarProbe ===");
        // 先接管配置路径，再实例化场景（Main._Ready 的启动恢复读它 → enabled=false，不显示）
        NetSpeedBubble.探针_配置路径覆写 = 临时配置;
        try { if (File.Exists(临时配置)) File.Delete(临时配置); } catch { }
        File.WriteAllText(临时配置, "{\"x\":120,\"y\":120,\"enabled\":false}");

        var ps = GD.Load<PackedScene>("res://game.tscn");
        AddChild(ps.Instantiate());
        _工具栏 = GetChild(0).GetNodeOrNull("ToolBar") as Window;
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 2:
                GD.Print($"[TB] 根视口嵌入子窗口 = {GetTree().Root.GuiEmbedSubwindows}");
                ToolBar.显示();
                break;
            case 4: A组_工具格与动作(); break;
            case 6: B组_点击开(); break;
            case 8: C组_再点关(); break;
            case 10: D组_启动恢复(); break;
            case 12:
                ToolBar.隐藏();
                断言(_工具栏 is { Visible: false }, "工具栏已关闭");
                GD.Print($"[TB] ===== 失败数 = {_失败} =====");
                try { File.Delete(临时配置); } catch { }   // 收尾：不留探针文件
                NetSpeedBubble.探针_配置路径覆写 = null;
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                break;
        }
    }

    private void A组_工具格与动作()
    {
        GD.Print("--- A 组：工具格扫描与动作注册 ---");
        断言(_工具栏 != null, "找到 ToolBar 窗口节点");
        断言(ToolBar.动作存在("netspeed"), "扫描到 action=netspeed 的 mod（mods/toolbar/netspeed）");
        var 按钮 = 找按钮(_工具栏, "网速监控");
        断言(按钮 != null, "工具栏出现「网速监控」按钮");
        if (按钮 != null) 断言(按钮.Text.Contains("·关"), $"默认状态为关（标题「{按钮.Text}」）");

        GD.Print("--- A2 组：旧工具（command: 派发接线）---");
        断言(找按钮(_工具栏, "任务管理器") != null, "出现「任务管理器」按钮（action=command:task）");
        断言(找按钮(_工具栏, "游览") != null, "出现「游览」按钮（action=command:visit）");
        断言(找按钮(_工具栏, "复制") != null, "出现「复制」按钮（action=command:copy-info）");
        断言(CommandLoader.按名查找("task") != null, "指令查找：task 命中");
        断言(CommandLoader.按名查找("visit") != null, "指令查找：visit 命中");
        断言(CommandLoader.按名查找("copy-info") != null, "指令查找：copy-info 命中");
        断言(CommandLoader.按名查找("__不存在__") == null, "指令查找：未知名 → null");
        // 故意不点这三个按钮：会真的打开任务管理器/浏览器/对话框（探针只验接线，不触发副作用）
    }

    private void B组_点击开()
    {
        GD.Print("--- B 组：点击 → 气泡开 ---");
        var 按钮 = 找按钮(_工具栏, "网速监控");
        if (按钮 == null) { _失败++; GD.PrintErr("[TB] FAIL  找不到按钮，跳过"); return; }
        按钮.EmitSignal(BaseButton.SignalName.Pressed);

        断言(NetSpeedBubble.可见, "点击后气泡可见（动作已执行）");
        断言(读启用() == true, "配置 enabled=true 已持久化");
        var 新按钮 = 找按钮(_工具栏, "网速监控");
        断言(新按钮 != null && 新按钮.Text.Contains("·开"), $"按钮标题变「·开」（现在是「{新按钮?.Text}」）");
    }

    private void C组_再点关()
    {
        GD.Print("--- C 组：再点 → 气泡关 ---");
        var 按钮 = 找按钮(_工具栏, "网速监控");
        if (按钮 == null) { _失败++; GD.PrintErr("[TB] FAIL  找不到按钮，跳过"); return; }
        按钮.EmitSignal(BaseButton.SignalName.Pressed);

        断言(!NetSpeedBubble.可见, "再点后气泡隐藏");
        断言(读启用() == false, "配置 enabled=false 已持久化");
        var 新按钮 = 找按钮(_工具栏, "网速监控");
        断言(新按钮 != null && 新按钮.Text.Contains("·关"), $"按钮标题回「·关」（现在是「{新按钮?.Text}」）");
    }

    private void D组_启动恢复()
    {
        GD.Print("--- D 组：启动恢复（enabled + 工具栏入口仍在） ---");
        File.WriteAllText(临时配置, "{\"x\":120,\"y\":120,\"enabled\":true}");
        NetSpeedBubble.启动恢复();
        断言(NetSpeedBubble.可见, "enabled=true → 启动恢复自动显示（正例）");

        NetSpeedBubble.隐藏();
        NetSpeedBubble.启动恢复();
        断言(!NetSpeedBubble.可见, "enabled=false → 启动恢复不开（负例）");
    }

    private static bool? 读启用()
    {
        try
        {
            using var 文档 = JsonDocument.Parse(File.ReadAllText(临时配置));
            return 文档.RootElement.TryGetProperty("enabled", out var 字段) ? 字段.GetBoolean() : null;
        }
        catch { return null; }
    }

    private static Button 找按钮(Node 根, string 文本)
    {
        if (根 is Button b && b.Text.Contains(文本)) return b;
        foreach (var c in 根.GetChildren())
        {
            var r = 找按钮(c, 文本);
            if (r != null) return r;
        }
        return null;
    }
}
