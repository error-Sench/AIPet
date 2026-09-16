using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;

namespace desktop.script.Audio;

/// <summary>
/// 语音输出（TTS）—— 默认调用 **Windows 自带语音**（`System.Speech` / SAPI5）。
/// <para>
/// **设计决策（主人 2026-09-16）：不内置任何模型/语音库**，直接用系统已装语音；没有可用语音（或非 Windows）时
/// **静默降级**（只出气泡、不出声，绝不报错卡住）。
/// </para>
/// <para>
/// **说什么**：桌宠的**气泡**（`Dialogue.显示临时标题`）—— 它"冒出来"的话就念出来；聊天面板里的长篇回复**不念**
/// （不打扰，也避免机器人在你耳边念小作文）。超过 `最大字数` 的泡泡不念。
/// </para>
/// <para>配置：`settings/tts.json`（启用 / 声音 / 语速 / 音量 / 最大字数）。所有开关都可随时关掉。</para>
/// </summary>
public static class Tts
{
    // ================= 配置（settings/tts.json，缺省用内置默认） =================

    public static bool 启用 { get; private set; } = true;
    /// <summary>系统语音名（空 = 自动挑：优先中文女声）。</summary>
    public static string 声音 { get; private set; } = "";
    /// <summary>SAPI 语速（-10 ~ 10）。</summary>
    public static int 语速 { get; private set; }
    /// <summary>0 ~ 100。</summary>
    public static int 音量 { get; private set; } = 100;
    /// <summary>超过这个字数的泡泡不念（0 = 不限）。</summary>
    public static int 最大字数 { get; private set; } = 80;

    private static SpeechSynthesizer _合成器;
    private static bool _初始化失败;
    private static string _已选声音 = "";

    /// <summary>有没有能用的语音（Windows + 至少一个已启用语音 + 合成器初始化成功）。</summary>
    public static bool 可用 => _合成器 != null && !_初始化失败;

    /// <summary>探针：最近一次实际交给语音合成器的文本（清洗后的）。</summary>
    public static string 最近一次文本 { get; private set; } = "";

    /// <summary>探针：最近一次的跳过原因（空 = 真念了）。</summary>
    public static string 最近跳过原因 { get; private set; } = "";

    // ================= 配置加载 =================

    public static void 载入配置()
    {
        foreach (var 路径 in Util.ConfigFile.候选("tts.json").Concat(Util.ConfigFile.候选("settings/tts.json")))
        {
            try
            {
                if (!File.Exists(路径)) continue;
                var 文本 = File.ReadAllText(路径);
                if (string.IsNullOrWhiteSpace(文本)) continue;
                using var 文档 = JsonDocument.Parse(文本);
                var 根 = 文档.RootElement;
                if (根.TryGetProperty("启用", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False) 启用 = a.GetBoolean();
                if (根.TryGetProperty("声音", out var b) && b.ValueKind == JsonValueKind.String) 声音 = b.GetString() ?? "";
                if (根.TryGetProperty("语速", out var c) && c.TryGetInt32(out var cv)) 语速 = Math.Clamp(cv, -10, 10);
                if (根.TryGetProperty("音量", out var d) && d.TryGetInt32(out var dv)) 音量 = Math.Clamp(dv, 0, 100);
                if (根.TryGetProperty("最大字数", out var e) && e.TryGetInt32(out var ev)) 最大字数 = Math.Max(0, ev);
                break;
            }
            catch (Exception ex) { GD.PrintErr($"[Tts] 读配置失败 {路径}: {ex.Message}"); }
        }
        GD.Print($"[Tts] 配置: 启用={启用} 声音={(string.IsNullOrEmpty(声音) ? "自动" : 声音)} 语速={语速} 音量={音量} 最大字数={最大字数}");
    }

    // ================= 说话 =================

    /// <summary>念一句话（桌宠气泡用）。返回是否真的念了。任何异常都吞掉并降级 —— 没声音也不影响桌宠运行。</summary>
    public static bool 说(string 文本)
    {
        最近跳过原因 = "";
        var 念念 = 清洗(文本);
        if (!启用) { 最近跳过原因 = "配置关了语音"; return false; }
        if (念念.Length == 0) { 最近跳过原因 = "清洗后没内容"; return false; }
        if (最大字数 > 0 && 念念.Length > 最大字数) { 最近跳过原因 = $"超过最大字数（{念念.Length} > {最大字数}）"; return false; }
        if (!初始化()) { 最近跳过原因 = "没有可用系统语音"; return false; }

        try
        {
            最近一次文本 = 念念;
            _合成器.SpeakAsyncCancelAll();   // 新泡泡来了就打断旧的（不排队念小作文）
            _合成器.SpeakAsync(念念);
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Tts] 说话失败: {ex.Message}");
            _初始化失败 = true;
            最近跳过原因 = ex.Message;
            return false;
        }
    }

    /// <summary>闭嘴（退出/静音时用）。</summary>
    public static void 停()
    {
        try { _合成器?.SpeakAsyncCancelAll(); } catch { /* 忽略 */ }
    }

    // ================= 内部 =================

    private static bool 初始化()
    {
        if (可用) return true;
        if (_初始化失败) return false;
        if (!OperatingSystem.IsWindows()) { GD.Print("[Tts] 非 Windows 平台 → 不做语音输出（静默降级）"); _初始化失败 = true; return false; }
        try
        {
            _合成器 = new SpeechSynthesizer { Volume = 音量, Rate = 语速 };
            var 候选 = _合成器.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
            if (候选.Count == 0) { GD.Print("[Tts] 系统里没有可用语音 → 静默降级"); _初始化失败 = true; return false; }

            var 选 = 候选.FirstOrDefault(v => v.Name == 声音)                        // 1. 配置指定的
                     ?? 候选.FirstOrDefault(v => v.Culture?.Name?.StartsWith("zh") == true && v.Gender == VoiceGender.Female)  // 2. 中文女声
                     ?? 候选.FirstOrDefault(v => v.Culture?.Name?.StartsWith("zh") == true)                                   // 3. 中文
                     ?? 候选[0];                                                                                              // 4. 任意
            _合成器.SelectVoice(选.Name);
            _已选声音 = 选.Name;
            GD.Print($"[Tts] 使用系统语音: {选.Name}（{选.Culture} / {选.Gender}）；共 {候选.Count} 个可用: {string.Join(", ", 候选.Select(v => v.Name))}");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Tts] 初始化系统语音失败（静默降级）: {ex.Message}");
            _初始化失败 = true;
            return false;
        }
    }

    /// <summary>清洗：去掉 BBCode 与围栏/换行，留下能念的纯文本。纯函数，探针可直接断言。</summary>
    public static string 清洗(string 文本)
    {
        if (string.IsNullOrWhiteSpace(文本)) return "";
        var s = Regex.Replace(文本, @"\[[^\]]*\]", "");          // BBCode 标签（[color=#xxx] 等）
        s = Regex.Replace(s, @"```[\s\S]*?```", " ");            // 代码围栏（含 ```pet 指令块）
        s = s.Replace("*", "").Replace("#", "");                  // 简单 markdown 记号
        s = Regex.Replace(s, @"\s+", " ");                        // 换行/多空格 → 单空格
        return s.Trim();
    }

    // ================= 探针专用 =================

    /// <summary>探针：当前（或自动挑选后）会使用的系统语音名。</summary>
    public static string 探针_当前声音
    {
        get { 初始化(); return _已选声音; }
    }

    /// <summary>探针：系统里能用的语音名列表。</summary>
    public static List<string> 探针_可用声音列表()
    {
        try
        {
            using var 临时 = new SpeechSynthesizer();
            return 临时.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>探针：把一句话**合成到 WAV 文件**（不需要声卡，headless 也能跑）→ 验证整条链路真的能出声。</summary>
    public static long 探针_合成到文件(string 文本, string 输出路径)
    {
        try
        {
            using var 临时 = new SpeechSynthesizer();
            var 候选 = 临时.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
            if (候选.Count == 0) return -1;
            var 选 = 候选.FirstOrDefault(v => v.Culture?.Name?.StartsWith("zh") == true && v.Gender == VoiceGender.Female) ?? 候选[0];
            临时.SelectVoice(选.Name);
            临时.SetOutputToWaveFile(输出路径);
            临时.Speak(清洗(文本));
            临时.SetOutputToNull();
            return File.Exists(输出路径) ? new FileInfo(输出路径).Length : -1;
        }
        catch (Exception ex) { GD.PrintErr($"[Tts] 探针合成失败: {ex.Message}"); return -1; }
    }

    /// <summary>探针：直接注入配置（不改文件）。</summary>
    public static void 探针_设配置(bool 启用值, int 最大字数_值)
    {
        启用 = 启用值; 最大字数 = 最大字数_值;
    }
}
