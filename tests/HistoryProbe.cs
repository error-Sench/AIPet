using desktop.script.Agent;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 会话恢复（历史回放）回归探针：**验证历史消息被切成独立消息、没有被合并成一大段**。
/// 依据实测流量：resume 时 hermes 会先把整段历史以 user_message_chunk / agent_message_chunk 回放，
/// 每条历史 = 一个 chunk。旧 bug 会把「好」「西瓜」「连通」累加成一个缓冲区 → 合并成 `好西瓜连通`。
/// 用法：Godot_..._console.exe --headless --path D:/Games/Github/AIPet res://tests/HistoryProbe.tscn
/// 注意：本探针会真实连接 Agent（会复用桌宠自己的会话）。
/// </summary>
public partial class HistoryProbe : Node
{
    private int _帧;
    private int _失败;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== HistoryProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[HS] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[HS] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        if (_帧 == 10)
        {
            GD.Print($"[HS] 启动后端（触发会话恢复）… 启动前记录长度={ChatBox.记录文本_只读.Length}");
            AgentBridge.Start();
        }
        else if (_帧 == 1200) // ≈20s：回放已在 t≈10s 完成，留足余量
        {
            var 记录 = ChatBox.记录文本_只读;
            var 小萝数 = System.Text.RegularExpressions.Regex.Matches(记录, "小萝").Count;
            GD.Print($"[HS] 记录长度={记录.Length} 「小萝」出现次数={小萝数} 含分隔线={记录.Contains("以下为上次会话")}");
            GD.Print("[HS] ---- 记录原文（转义换行）----");
            GD.Print(记录.Replace("\n", "\\n"));
            GD.Print("[HS] ---- 结束 ----");

            断言(记录.Contains("以下为上次会话"), "历史区块有分隔提示");
            断言(小萝数 >= 2, "历史里的多条助手回复被切成独立消息（「小萝」出现 ≥2 次）");
            断言(!记录.Contains("好西瓜"), "没有出现合并产物「好西瓜」");
            ChatBox.显示();
            GD.Print("[HS] ---- 记录原文（<NL>=换行）----");
            GD.Print(">>>" + 记录.Replace("\n", "<NL>") + "<<<");
            GD.Print("[HS] ---- 结束 ----");
            if (_失败 == 0) GD.Print("[HS] PASS"); else GD.PrintErr("[HS] FAIL");
            GD.Print($"[HS] ===== 失败数 = {_失败} =====");
        }
        else if (_帧 == 1230)
        {
            // 抓面板画面供人工/视觉复核（必须非 headless 才有像素；headless 的 dummy 渲染器取图会报错噪音）
            if (!OS.HasFeature("headless"))
            {
                var w = GetChild(0).GetNodeOrNull("ChatBox") as Window;
                var 图 = w?.GetTexture()?.GetImage();
                if (图 != null)
                {
                    var 路径 = ProjectSettings.GlobalizePath("user://history_panel.png");
                    图.SavePng(路径);
                    GD.Print($"[HS] 面板截图: {路径} ({图.GetWidth()}x{图.GetHeight()})");
                }
                else GD.Print("[HS] 面板截图跳过（无像素）");
            }
            else GD.Print("[HS] 面板截图跳过（headless）");
            AgentBridge.Stop();
            GetTree().Quit(_失败 == 0 ? 0 : 1);
        }
        else if (_帧 % 300 == 0) // 每 5s 打印进度
        {
            GD.Print($"[HS] t={_帧 / 60f:0.0}s 记录长度={ChatBox.记录文本_只读.Length} 后端就绪={AgentBridge.Backend?.IsReady}");
        }
        else if (_帧 > 1800)
        {
            GD.PrintErr("[HS] 超时 FAIL");
            GetTree().Quit(2);
        }
    }
}