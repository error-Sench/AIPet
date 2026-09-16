using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using Godot;
using desktop.script.UX;

namespace desktop.tests;

/// <summary>
/// NetSpeedProbe（headless）：验证网速监测桌面气泡 —— 格式化、采样差分、气泡文本与历史、位置持久化、无网卡降级。
/// <para>场景：`tests/NetSpeedProbe.tscn`。不联网、不读任何内容（只读本机网卡计数）。</para>
/// </summary>
public partial class NetSpeedProbe : Node
{
    private int _帧;
    private int _失败;
    private bool _保持;   // `-- hold` 时保持气泡可见（供外部截屏做视觉复核）
    private NetSpeedBubble _气泡;

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[NS] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[NS] FAIL  {描述}"); }
    }

    public override void _Ready()
    {
        _保持 = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "hold") >= 0;
        // 探针不碰真实数据（打包审计 B6）：配置写到临时文件，收尾删除
        NetSpeedBubble.探针_配置路径覆写 = ProjectSettings.GlobalizePath("user://_probe_netspeed.json");
        try { if (File.Exists(NetSpeedBubble.探针_配置路径覆写)) File.Delete(NetSpeedBubble.探针_配置路径覆写); } catch { }
        GD.Print($"=== NetSpeedProbe: 场景已实例化（保持模式={_保持}）===");
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 10: A组_格式化(); break;
            case 20: B组_采样(); break;
            case 30: C组_气泡链路(); break;
            case 40: D组_位置与降级(); break;
            case 50:
                GD.Print($"[NS] ===== 失败数 = {_失败} =====");
                if (_保持)   // 视觉复核模式：气泡留在屏上不退出（外部截桌面屏核对「气泡样式」）
                {
                    NetSpeedBubble.显示();
                    GD.Print("[NS] HOLDING（气泡保持在屏上，外部截屏后手动结束进程）");
                    break;
                }
                try { File.Delete(NetSpeedBubble.探针_配置路径覆写); } catch { }   // 收尾：不留探针文件
                NetSpeedBubble.探针_配置路径覆写 = null;
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                break;
        }
    }

    private void A组_格式化()
    {
        GD.Print("--- A 组：格式化（纯函数）---");
        断言(NetSpeedBubble.格式化(0) == "0B/s", $"0 → {NetSpeedBubble.格式化(0)}");
        断言(NetSpeedBubble.格式化(512) == "512B/s", $"512 → {NetSpeedBubble.格式化(512)}");
        断言(NetSpeedBubble.格式化(1536) == "2KB/s", $"1536 → {NetSpeedBubble.格式化(1536)}（1000 进制，紧凑）");
        断言(NetSpeedBubble.格式化(1024 * 1024 * 1.5).EndsWith("MB/s"), $"1.5MB → {NetSpeedBubble.格式化(1024 * 1024 * 1.5)}");
        断言(NetSpeedBubble.格式化(-5) == "0B/s" && NetSpeedBubble.格式化(double.NaN) == "0B/s",
            "负数/NaN 都收敛到 0（不显示乱码）");
    }

    private void B组_采样()
    {
        GD.Print("--- B 组：采样（纯本地读网卡计数）---");
        var (收, 发) = NetSpeedBubble.采样();
        断言(收 >= 0 && 发 >= 0, $"读到的收发字节非负（收 {收} / 发 {发}）");
    }

    private void C组_气泡链路()
    {
        GD.Print("--- C 组：气泡链路（注入采样 → 文本 → 历史）---");
        _气泡 = new NetSpeedBubble { Name = "NetSpeedBubbleProbe" };
        AddChild(_气泡);
        断言(NetSpeedBubble.存在, "气泡已创建（代码懒创建，不依赖 game.tscn 节点）");
        断言(_气泡.Size.Y <= 28 && _气泡.Size.X <= 150, $"气泡是单行小尺寸（{_气泡.Size.X}x{_气泡.Size.Y}）");
        断言(!NetSpeedBubble.可见, "默认不显示（零干扰）");

        // 第一采样只是打底（不算速度），第二采样才有差值
        _气泡.探针_注入采样(1_000_000, 500_000);
        _气泡.探针_注入采样(1_000_000 + 2_000_000, 500_000 + 1024 * 512, 1.0);
        断言(_气泡.探针_下行文本.StartsWith("↓") && _气泡.探针_下行文本.Contains("MB/s"), $"下行 2MB/s → 「{_气泡.探针_下行文本}」");
        断言(_气泡.探针_上行文本.Contains("KB/s"), $"上行 512KB/s → 「{_气泡.探针_上行文本}」");

        _气泡.探针_注入采样(1_000_000 + 2_000_000 + 3_000_000, 500_000 + 1024 * 512 + 4096, 1.0);
        断言(Math.Abs(NetSpeedBubble.探针_下行速度 - 3_000_000) < 1, $"速度按差值算（{NetSpeedBubble.探针_下行速度:0} B/s = 3MB/s）");

        var 历史 = _气泡.历史快照();
        断言(历史.Length == 60, $"历史固定 60 个样本（{历史.Length}）");
        断言(历史.Skip(历史.Length - 3).Sum() > 0, "最近的样本已进入历史（曲线有数据）");
    }

    private void D组_位置与降级()
    {
        GD.Print("--- D 组：位置持久化 + 显示/隐藏不炸 ---");
        NetSpeedBubble.显示();
        断言(NetSpeedBubble.可见, "显示() 之后可见");
        var 屏 = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
        var 位 = _气泡.Position;   // 气泡自己的位置（不是探针窗口的）
        if (屏.Size.X > 0)
            断言(位.X >= 屏.Position.X - 8 && 位.X < 屏.End.X && 位.Y >= 屏.Position.Y - 8 && 位.Y < 屏.End.Y,
                $"位置在屏幕内（{位.X},{位.Y}；屏 {屏.Position.X},{屏.Position.Y}–{屏.End.X},{屏.End.Y}）");
        else
            GD.Print("[NS] SKIP  位置断言（headless 没有真实屏幕区，Windows 无窗口管理器不认位置）");

        // 截图见 D组_截图()（等布局算完再拍，否则截到白板 —— 聊天面板也踩过同一个坑）
        NetSpeedBubble.隐藏();
        断言(!NetSpeedBubble.可见, "隐藏() 之后不可见");
        断言(File.Exists(NetSpeedBubble.探针_配置路径覆写),
            "位置与开关状态写入配置文件（探针走临时文件，不碰真实数据）");
        try
        {
            using var 文档 = JsonDocument.Parse(File.ReadAllText(NetSpeedBubble.探针_配置路径覆写));
            var 开 = 文档.RootElement.TryGetProperty("enabled", out var 字段) && 字段.GetBoolean();
            断言(!开, "隐藏后 enabled=false（下次启动不自动开）");
        }
        catch (Exception 异常) { _失败++; GD.PrintErr($"[NS] FAIL  读配置文件失败: {异常.Message}"); }
    }
}
