using System;
using System.IO;
using System.Linq;
using Godot;
using desktop.script.Agent;
using desktop.script.State;
using desktop.script.UX;
using desktop.script.Audio;

namespace desktop.tests;

/// <summary>
/// DegradeProbe（headless）：验证「Agent 不可用时的降级路径」（idea §9.3）。
/// <para>
/// 两种情况必须**区分对待**：① 没配 Agent（本地模式）= 正常状态，只记日志、不打扰主人；
/// ② 配了 Agent 却连不上/启动失败 = **要提醒主人**（气泡 + 事件池），并且桌宠照常运行不卡死。
/// </para>
/// </summary>
public partial class DegradeProbe : Node
{
    private int _帧;
    private int _失败;
    private readonly string _临时池 = Path.Combine(Path.GetTempPath(), "aipet_degrade_probe.jsonl");

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[DG] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[DG] FAIL  {描述}"); }
    }

    public override void _Ready()
    {
        EventPool.探针_路径覆写 = _临时池;
        EventPool.探针_清空();
        Tts.探针_设配置(true, 1);   // 探针静音（超过 1 字就不念），但链路照走
        GD.Print("=== DegradeProbe: 场景已实例化 ===");
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 10: A组_启动失败要提醒(); break;
            case 20: B组_没配Agent不打扰(); break;
            case 30:
                EventPool.探针_清空();
                EventPool.探针_路径覆写 = "";
                GD.Print($"[DG] ===== 失败数 = {_失败} =====");
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                break;
        }
    }

    private void A组_启动失败要提醒()
    {
        GD.Print("--- A 组：配了 Agent 但起不来 → 必须提醒主人（气泡 + 事件池）---");
        AgentBridge.探针_设后端("hermes-acp", Path.Combine(Path.GetTempPath(), "这个可执行文件不存在_aipet.exe"));
        AgentBridge.Options.WorkingDirectory = Path.GetTempPath();

        var 成功 = AgentBridge.Start();

        断言(!成功, "启动失败被如实返回 false（不假装成功）");
        断言(AgentBridge.探针_降级已提醒, "触发了降级提醒（不会静默失败）");
        断言(Dialogue.探针_最近请求文本.Length > 0 && Dialogue.探针_最近请求文本.Length < 60,
            $"跟主人说了一句人话（「{Dialogue.探针_最近请求文本}」）");
        断言(!Dialogue.探针_最近请求文本.Contains("An error") && !Dialogue.探针_最近请求文本.Contains("Exception") &&
            !Dialogue.探针_最近请求文本.Contains("系统找不到"),
            "气泡里**没有**原始英文异常（只进日志；也就不会被 TTS 念出来）");
        var 事件 = EventPool.读();
        断言(事件.Any(r => r["kind"] == "Agent不可用"), "事件池里留了「Agent不可用」一条（可回溯）");
        断言(!Tts.最近一次文本.Contains("大脑"), "（探针静音设定生效：这句没真的念出来）");
    }

    private void B组_没配Agent不打扰()
    {
        GD.Print("--- B 组：压根没配 Agent = 本地桌宠模式（正常状态，不该打扰）---");
        AgentBridge.探针_设后端("none", "");
        AgentBridge.Options.WorkingDirectory = Path.GetTempPath();
        EventPool.探针_清空();

        var 成功 = AgentBridge.Start();

        断言(成功, "无 Agent 也照常「启动成功」（桌宠能独立运行）");
        断言(!EventPool.读().Any(r => r["kind"] == "Agent不可用"),
            "本地模式**不**写「Agent不可用」事件（那只是配置选择，不是故障）");
    }
}
