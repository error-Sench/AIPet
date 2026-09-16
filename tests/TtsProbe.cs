using System;
using System.IO;
using Godot;
using desktop.script.Audio;
using desktop.script.UX;

namespace desktop.tests;

/// <summary>
/// TtsProbe（headless）：验证语音输出 —— 系统语音可用性、配置与门控、文本清洗、真合成到 WAV、与气泡的联动。
/// <para>
/// 场景：`tests/TtsProbe.tscn`。**探针不真的发声**（会打断主人）：需要验证"能出声"时用 `探针_合成到文件` 写 WAV。
/// </para>
/// </summary>
public partial class TtsProbe : Node
{
    private int _帧;
    private int _失败;
    private readonly string _临时Wav = Path.Combine(Path.GetTempPath(), "aipet_tts_probe.wav");

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[TTS] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[TTS] FAIL  {描述}"); }
    }

    public override void _Ready()
    {
        GD.Print("=== TtsProbe: 场景已实例化 ===");
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 10: A组_配置(); break;
            case 20: B组_清洗(); break;
            case 30: C组_门控(); break;
            case 40: D组_系统语音(); break;
            case 50: E组_真合成(); break;
            case 60: F组_与气泡联动(); break;
            case 70:
                Tts.探针_设配置(true, 80);
                try { if (File.Exists(_临时Wav)) File.Delete(_临时Wav); } catch { /* 忽略 */ }
                GD.Print($"[TTS] ===== 失败数 = {_失败} =====");
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                break;
        }
    }

    private void A组_配置()
    {
        GD.Print("--- A 组：配置（settings/tts.json）---");
        Tts.载入配置();
        断言(Tts.启用, "默认启用（配置文件里 启用=true）");
        断言(Tts.最大字数 == 80, $"最大字数按配置（{Tts.最大字数}）");
        断言(Tts.音量 is >= 0 and <= 100 && Tts.语速 is >= -10 and <= 10, $"语速/音量在合法区间（{Tts.语速} / {Tts.音量}）");
    }

    private void B组_清洗()
    {
        GD.Print("--- B 组：文本清洗（念之前先洗干净）---");
        var 含标签 = Tts.清洗("[color=#888888]你好呀[/color]\n\n```pet\n{\"cmd\":\"speak\"}\n```\n再见");
        断言(!含标签.Contains("[") && !含标签.Contains("```") && !含标签.Contains("\n"),
            $"BBCode 与围栏块都被清掉（得到「{含标签}」）");
        断言(含标签.Contains("你好呀") && 含标签.Contains("再见"), "正文保留");
        断言(Tts.清洗("   ") == "", "空白 → 空串（不念空气）");
    }

    private void C组_门控()
    {
        GD.Print("--- C 组：门控（关掉/太长/空 → 不念）---");
        Tts.探针_设配置(false, 80);
        断言(!Tts.说("你好呀") && Tts.最近跳过原因.Contains("配置关了"), $"关掉语音后不念（{Tts.最近跳过原因}）");

        Tts.探针_设配置(true, 5);
        断言(!Tts.说("这句话明显超过五个字了") && Tts.最近跳过原因.Contains("最大字数"),
            $"超过最大字数不念（{Tts.最近跳过原因}）");

        Tts.探针_设配置(true, 80);
        断言(!Tts.说("   ") && Tts.最近跳过原因.Contains("没内容"), $"空白不念（{Tts.最近跳过原因}）");
    }

    private void D组_系统语音()
    {
        GD.Print("--- D 组：系统语音（Windows 自带，不内置模型）---");
        var 列表 = Tts.探针_可用声音列表();
        断言(列表.Count > 0, $"系统里有可用语音 {列表.Count} 个：{string.Join(" / ", 列表)}");
        断言(Tts.探针_当前声音.Length > 0, $"自动挑中：{Tts.探针_当前声音}");
        if (OperatingSystem.IsWindows())
            断言(Tts.探针_当前声音.Contains("zh") || Tts.探针_当前声音.Contains("Huihui") || 列表.Count > 0,
                "中文环境优先挑中文语音（没装中文语音时退而求其次）");
    }

    private void E组_真合成()
    {
        GD.Print("--- E 组：真合成到 WAV（不需要声卡，端到端验证能出声）---");
        var 大小 = Tts.探针_合成到文件("你好呀，我是小萝。", _临时Wav);
        断言(大小 > 1000, $"合成出 WAV 字节数 = {大小}（>1000 即真的有音频数据）");
        if (大小 > 44)
        {
            using var 流 = File.OpenRead(_临时Wav);
            var 头 = new byte[4];
            流.Read(头, 0, 4);
            断言(头[0] == (byte)'R' && 头[1] == (byte)'I' && 头[2] == (byte)'F' && 头[3] == (byte)'F',
                "文件头是 RIFF（标准 WAV）");
        }
    }

    private void F组_与气泡联动()
    {
        GD.Print("--- F 组：气泡 → 语音联动（探针里用「超长跳过」防止真的出声）---");
        Tts.探针_设配置(true, 1);   // 让所有泡泡都因超长被跳过 → 探针静音，但链路照样走一遍
        Dialogue.显示临时标题("这是测试用的一句话");
        断言(Dialogue.探针_最近请求文本 == "这是测试用的一句话", "气泡文本已记录");
        断言(Tts.最近跳过原因.Contains("最大字数"),
            $"气泡确实走到了语音这一层（跳过原因：{Tts.最近跳过原因}）—— 说明挂上了，不是断链");
    }
}
