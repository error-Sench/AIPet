using System;
using System.IO;
using System.Text;
using System.Text.Json;
using desktop.script.Agent;
using desktop.script.State;
using Godot;

namespace desktop.tests;

/// <summary>
/// 工具通道探针（headless）：MCP `pet_command` → `user://actions.jsonl` → ActionInbox 执行 → 回执。
/// 全链路走**文件协议**（不需要真的起 MCP 服务进程）：
///  A. 启动前写入的旧指令不被执行（不吃旧账）
///  B. 合法指令执行 + 回执写入（ok=true，note 带执行日志）
///  C. 越权 / 坏参数被拒 + 回执 ok=false（原因在 note）
///  D. 坏 JSON 行不炸，前后行照常处理
/// 用法：Godot_..._console.exe --headless --path D:/Games/Github/AIPet res://tests/ToolChannelProbe.tscn
/// </summary>
public partial class ToolChannelProbe : Node
{
    private int _帧;
    private int _失败;
    private readonly string _临时目录 = Path.Combine(Path.GetTempPath(), "aipet_toolchannel_probe");
    private string 收件箱 => Path.Combine(_临时目录, "actions.jsonl");

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[TC] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[TC] FAIL  {描述}"); }
    }

    public override void _Ready()
    {
        try { if (Directory.Exists(_临时目录)) Directory.Delete(_临时目录, true); } catch { }
        Directory.CreateDirectory(_临时目录);
        // 启动前先塞一条「旧指令」——不该被执行（不吃旧账）
        File.WriteAllText(收件箱, "{\"id\":\"old\",\"cmd\":\"set_state\",\"state\":\"sleep\"}\n", new UTF8Encoding(false));

        ActionInbox.探针_路径覆写 = 收件箱;
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== ToolChannelProbe: 场景已实例化 ===");
    }

    private void 追加(string 行) => File.AppendAllText(收件箱, 行 + "\n", new UTF8Encoding(false));

    private bool 读回执(string id, out bool ok, out string note)
    {
        ok = false; note = "";
        try
        {
            foreach (var 行 in File.ReadAllLines(收件箱))
            {
                var 修剪 = 行.Trim();
                if (修剪.Length == 0 || !修剪.StartsWith("{")) continue;
                try
                {
                    using var 文档 = JsonDocument.Parse(修剪);
                    var 根 = 文档.RootElement;
                    if (!根.TryGetProperty("repl", out var r) || r.GetString() != id) continue;
                    ok = 根.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                    note = 根.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "";
                    return true;
                }
                catch { /* 坏行跳过 */ }
            }
        }
        catch { }
        return false;
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 6:
                // A：旧指令没被执行、也没有回执
                断言(StateMachine.CurrentState != StateMachine.Sleep, "A1 启动前的旧指令不执行（不吃旧账）");
                断言(!读回执("old", out _, out _), "A2 旧指令也没有回执（压根没进处理）");
                StateMachine.SetState(StateMachine.Idle);
                break;
            case 8:
                追加("{\"id\":\"t1\",\"t\":\"x\",\"cmd\":\"set_state\",\"state\":\"think\"}");
                追加("{坏JSON 一行");
                追加("{\"id\":\"t2\",\"cmd\":\"run_shell\",\"cmdline\":\"rm -rf /\"}");
                追加("{\"id\":\"t3\",\"cmd\":\"set_state\",\"state\":\"nope\"}");
                break;
            case 90: // ≈1.5s：轮询（0.25s 间隔）足够消费四行
            {
                GD.Print("--- B/C/D 组：执行与回执 ---");
                断言(StateMachine.CurrentState == StateMachine.Think, "B1 t1（set_state=think）执行成功");

                var 有 = 读回执("t1", out var ok1, out var note1);
                断言(有 && ok1 && note1.Contains("set_state"), $"B2 t1 回执 ok=true 且带日志（{(有 ? note1 : "无回执")}）");

                有 = 读回执("t2", out var ok2, out var note2);
                断言(有 && !ok2 && note2.Contains("白名单"), $"C1 t2（run_shell）被拒且回执 ok=false（{(有 ? note2 : "无回执")}）");

                有 = 读回执("t3", out var ok3, out var note3);
                断言(有 && !ok3 && note3.Contains("未知状态"), $"C2 t3（非法状态）被拒且原因在 note（{(有 ? note3 : "无回执")}）");

                断言(StateMachine.CurrentState == StateMachine.Think, "C3 被拒指令没有改变状态（仍是 think）");
                break;
            }
            case 96:
                ActionInbox.探针_路径覆写 = "";
                try { Directory.Delete(_临时目录, true); } catch { }
                GD.Print($"[TC] ===== 失败数 = {_失败} =====");
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                break;
        }
    }
}
