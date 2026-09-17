using System;
using System.IO;
using System.Text.Json;
using Godot;
using desktop.script.Loader;
using desktop.script.UX;

namespace desktop.tests;

/// <summary>
/// ToolBarProbe（headless）：工具栏窗口 + mod 工具格 + 内置动作 + 组件自包含。
/// 验证：扫描 mods/toolbar 找到「网速监控」（action=netspeed）；点击开/关网速气泡
/// （按钮标题「·开 / ·关」）；配置 enabled 持久化；启动恢复；`command:` 派发机制（保留）；
/// 组件目录解析与「配置/状态随组件目录」（真实写入仅在没有真实 state.json 时做，验完删除）。
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
            case 10: D组_启动恢复_请求(); break;
            case 14: D组_启动恢复_断言(); break;
            case 18: D组_启动恢复_负例断言(); E组_自包含(); break;
            case 22:
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

        GD.Print("--- A2 组：指令派发机制（command: 接线，保留给 mod 用）---");
        断言(CommandLoader.按名查找("language") != null, "指令查找：language 命中");
        断言(CommandLoader.按名查找("config") != null, "指令查找：config 命中");
        断言(CommandLoader.按名查找("__不存在__") == null, "指令查找：未知名 → null");
        // 旧工具（任务管理器/游览/复制）已按主人要求整体删除（mods 目录与工具栏格一起）。
        // 派发机制保留：mod 的 info.json 声明 action="command:<指令名>" 即可把已有指令挂上工具栏。
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

    private void D组_启动恢复_请求()
    {
        GD.Print("--- D 组：启动恢复（推迟一帧生效——启动窗口期根节点正忙） ---");
        File.WriteAllText(临时配置, "{\"x\":120,\"y\":120,\"enabled\":true}");
        NetSpeedBubble.启动恢复();
    }

    private void D组_启动恢复_断言()
    {
        断言(NetSpeedBubble.可见, "enabled=true → 启动恢复自动显示（正例）");
        NetSpeedBubble.隐藏();          // 写回 enabled=false
        NetSpeedBubble.启动恢复();      // 负例：不应再打开
    }

    private void D组_启动恢复_负例断言()
    {
        断言(!NetSpeedBubble.可见, "enabled=false → 启动恢复不开（负例）");
    }

    private void E组_自包含()
    {
        GD.Print("--- E 组：组件自包含（配置 / 状态随组件目录）---");
        var 目录 = ToolBar.动作目录("netspeed");
        断言(!string.IsNullOrEmpty(目录) && 目录.Replace('\\', '/').EndsWith("mods/toolbar/netspeed"),
            $"动作目录解析到组件本身（{目录}）");
        断言(!string.IsNullOrEmpty(目录) && File.Exists(Path.Combine(目录, "config.json")),
            "外观配置在组件目录里（config.json）");

        var 状态 = string.IsNullOrEmpty(目录) ? null : Path.Combine(目录, "state.json");
        if (状态 == null || File.Exists(状态))
        {
            GD.Print($"[TB] 跳过真实写入检查（{(状态 == null ? "组件目录不可用" : "state.json 已存在：真实用户数据，不能动")}）");
            return;
        }
        // 临时清掉覆写 → 走真实路径写一次，验完删除 —— 证明「状态落在组件目录」
        NetSpeedBubble.探针_配置路径覆写 = null;
        NetSpeedBubble.显示();
        断言(File.Exists(状态), "状态写在组件目录里（state.json）");
        NetSpeedBubble.隐藏();
        try { File.Delete(状态); } catch { }
        NetSpeedBubble.探针_配置路径覆写 = 临时配置;   // 恢复（case 14 收尾还要删它）
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
