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
/// 语音输出（TTS）—— 两个引擎：**edge**（Edge 在线语音，默认 `zh-CN-XiaoxiaoNeural` 晓晓，自然；需网络）与 **sapi**（Windows 自带，离线但机械）。
/// <para>
/// **设计决策（主人 2026-09-16 → 后修订）：不内置任何模型/语音库**。主人原话「Windows 的合成语音听得我要窒息了」→ 默认用 edge；
/// edge 不可用（没装 edge-tts / 断网 / 合成失败）时**不出声** —— **不自动回退系统语音**（主人嫌它难听，回退目标＝无语音）；
/// sapi 只是**手动选项**（把 `config/tts.json` 的 引擎 改成 sapi）。任何异常都静默降级：只出气泡、不出声，绝不报错卡住。
/// </para>
/// <para>
/// **说什么**：桌宠的**气泡**（`Dialogue.显示临时标题`）—— 它"冒出来"的话就念出来；聊天面板里的长篇回复**不念**
/// （不打扰，也避免机器人在你耳边念小作文）。超过 `最大字数` 的泡泡不念。
/// </para>
/// <para>配置：`config/tts.json`（启用 / 引擎 / edge语音 / edge命令 / 声音 / 语速 / 音量 / 最大字数）。所有开关都可随时关掉。</para>
/// </summary>
public static class Tts
{
    // ================= 配置（config/tts.json，缺省用内置默认） =================

    public static bool 启用 { get; private set; } = true;
    /// <summary>引擎：`edge` = Edge 在线语音（自然好听、需要网络）｜`sapi` = Windows 自带（离线、机械）。</summary>
    public static string 引擎 { get; private set; } = "edge";
    /// <summary>Edge 语音名（默认 zh-CN-XiaoxiaoNeural = 晓晓）。</summary>
    public static string Edge语音 { get; private set; } = "zh-CN-XiaoxiaoNeural";
    /// <summary>edge-tts 可执行文件路径（空 = 自动查找：专用 venv / Hermes venv / PATH）。</summary>
    public static string Edge命令 { get; private set; } = "";
    /// <summary>日志/探针：最近一次实际使用的引擎（edge / sapi / 空）。</summary>
    public static string 最近引擎 { get; private set; } = "";
    /// <summary>系统语音名（空 = 自动挑：优先中文女声）。</summary>
    public static string 声音 { get; private set; } = "";
    /// <summary>SAPI 语速（-10 ~ 10）。</summary>
    public static int 语速 { get; private set; }
    /// <summary>0 ~ 100。</summary>
    public static int 音量 { get; private set; } = 100;
    /// <summary>超过这个字数的泡泡不念（0 = 不限）。</summary>
    public static int 最大字数 { get; private set; } = 80;

    private static SpeechSynthesizer _合成器;
    private static string _edge路径;
    private static bool _edge查找过;
    private static int _序号;                     // 每轮 +1：作废旧合成结果（打断用）
    private static TtsPlayer _播放节点;
    private static string 语音目录 => ProjectSettings.GlobalizePath("user://tts");
    private static bool _初始化失败;
    private static string _已选声音 = "";
    private static bool _探针_禁Edge;

    /// <summary>当前引擎是否可用：edge＝找到 edge-tts；sapi＝合成器初始化成功。（不再表示「任一引擎可用」—— 回退已取消）</summary>
    public static bool 可用 => 引擎 == "edge" ? !string.IsNullOrEmpty(Edge路径) : (_合成器 != null && !_初始化失败);

    /// <summary>探针：最近一次实际交给语音合成器的文本（清洗后的）。</summary>
    public static string 最近一次文本 { get; private set; } = "";

    /// <summary>探针：最近一次的跳过原因（空 = 真念了）。</summary>
    public static string 最近跳过原因 { get; private set; } = "";

    // ================= 配置加载 =================

    public static void 载入配置()
    {
        foreach (var 路径 in Util.ConfigFile.候选("tts.json").Concat(Util.ConfigFile.候选("config/tts.json")))
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
                if (根.TryGetProperty("引擎", out var f) && f.ValueKind == JsonValueKind.String) 引擎 = (f.GetString() ?? "edge").Trim().ToLowerInvariant();
                if (根.TryGetProperty("edge语音", out var g) && g.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(g.GetString())) Edge语音 = g.GetString()!.Trim();
                if (根.TryGetProperty("edge命令", out var h) && h.ValueKind == JsonValueKind.String) Edge命令 = h.GetString() ?? "";
                if (根.TryGetProperty("语速", out var c) && c.TryGetInt32(out var cv)) 语速 = Math.Clamp(cv, -10, 10);
                if (根.TryGetProperty("音量", out var d) && d.TryGetInt32(out var dv)) 音量 = Math.Clamp(dv, 0, 100);
                if (根.TryGetProperty("最大字数", out var e) && e.TryGetInt32(out var ev)) 最大字数 = Math.Max(0, ev);
                break;
            }
            catch (Exception ex) { GD.PrintErr($"[Tts] 读配置失败 {路径}: {ex.Message}"); }
        }
        GD.Print($"[Tts] 配置: 启用={启用} 引擎={引擎} edge语音={Edge语音} 声音={(string.IsNullOrEmpty(声音) ? "自动" : 声音)} 语速={语速} 音量={音量} 最大字数={最大字数}");
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

        最近一次文本 = 念念;
        if (引擎 == "edge")
        {
            if (!string.IsNullOrEmpty(Edge路径)) { 用Edge念(念念); return true; }
            最近跳过原因 = "edge 不可用（没装 edge-tts / 找不到）→ 不出声";
            GD.Print("[Tts] 跳过：edge 不可用 → 不出声（不自动回退系统语音）");
            return false;
        }
        return 用Sapi念(念念);
    }

    /// <summary>系统语音（SAPI5）分支 —— **仅手动把引擎设为 sapi 时使用**；edge 失败不再回退到这里。</summary>
    private static bool 用Sapi念(string 念念)
    {
        if (!初始化()) { 最近跳过原因 = "没有可用语音（edge 不可用，系统语音也没有）"; return false; }
        try
        {
            最近引擎 = "sapi";
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
        _序号++;                                   // 作废进行中的合成结果
        停播放();
        try { _合成器?.SpeakAsyncCancelAll(); } catch { /* 忽略 */ }
    }

    // ================= Edge 在线语音（主人指定：zh-CN-XiaoxiaoNeural） =================

    /// <summary>edge-tts 路径（首次访问时自动查找一次）。</summary>
    public static string Edge路径
    {
        get
        {
            if (!_edge查找过)
            {
                _edge查找过 = true;
                _edge路径 = 找Edge();
                GD.Print(_edge路径 != null ? $"[Tts] edge-tts: {_edge路径}" : "[Tts] 没找到 edge-tts → 语音将不出声（config/tts.json 的 edge命令 可指定路径；想用系统语音就把 引擎 改成 sapi）");
            }
            return _edge路径;
        }
    }

    private static string 找Edge()
    {
        if (_探针_禁Edge) return null;   // 探针模拟「没装 edge」
        var 候选 = new List<string>();
        if (!string.IsNullOrWhiteSpace(Edge命令)) 候选.Add(Edge命令.Trim());
        var 本地 = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(本地))
        {
            候选.Add(Path.Combine(本地, "AIPet", "tts-venv", "Scripts", "edge-tts.exe"));
            候选.Add(Path.Combine(本地, "hermes", "hermes-agent", "venv", "Scripts", "edge-tts.exe"));
        }
        foreach (var 段 in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            if (!string.IsNullOrWhiteSpace(段)) 候选.Add(Path.Combine(段.Trim().Trim('"'), "edge-tts.exe"));
        foreach (var 路径 in 候选)
        {
            try { if (File.Exists(路径)) return 路径; } catch { /* 忽略坏路径 */ }
        }
        return null;
    }

    /// <summary>
    /// 用 Edge 合成后播放：**后台线程合成 MP3 → 主线程播放**（不卡渲染）。
    /// 每轮 `_序号++`：新泡泡来了旧结果直接丢弃（不排队念小作文）；失败**不出声**（不回退系统语音）。
    /// </summary>
    private static void 用Edge念(string 文本)
    {
        var 序 = ++_序号;
        停播放();
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            string 文件 = null;
            try
            {
                Directory.CreateDirectory(语音目录);
                var 输入 = Path.Combine(语音目录, $"in_{序}.txt");
                System.IO.File.WriteAllText(输入, 文本, new System.Text.UTF8Encoding(false));
                文件 = Path.Combine(语音目录, $"voice_{序}.mp3");
                var 参 = new List<string>
                {
                    "--voice", Edge语音,
                    "--rate=" + (语速 * 10).ToString("+0;-0;+0") + "%",
                    "--volume=" + (音量 - 100).ToString("+0;-0;+0") + "%",
                    "--file", 输入,
                    "--write-media", 文件,
                };
                if (!跑进程(Edge路径, 参, 15000)) 文件 = null;
            }
            catch (Exception e) { GD.PrintErr($"[Tts] edge 合成异常: {e.Message}"); 文件 = null; }

            if (序 != _序号) return;                                   // 已被新泡泡打断 → 丢弃，不播
            if (文件 != null && System.IO.File.Exists(文件) && new FileInfo(文件).Length > 512)
            {
                最近引擎 = "edge";
                Callable.From(() =>
                {
                    try { 确保播放节点(); _播放节点?.播放(文件, 音量); }
                    catch (Exception e) { GD.PrintErr($"[Tts] 播放调度失败: {e.Message}"); }
                }).CallDeferred();
            }
            else
            {
                GD.PrintErr("[Tts] edge 合成失败（网络/命令）→ 不出声（不自动回退系统语音）");
                最近跳过原因 = "edge 合成失败 → 不出声";
            }
        });
    }

    /// <summary>跑子进程（参数走 ArgumentList，中文/引号安全）；超时杀掉并返回 false。</summary>
    private static bool 跑进程(string exe, List<string> 参数, int 超时毫秒)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in 参数) psi.ArgumentList.Add(a);
            using var 进程 = System.Diagnostics.Process.Start(psi);
            if (进程 == null) return false;
            进程.OutputDataReceived += (_, __) => { };
            进程.ErrorDataReceived += (_, __) => { };
            进程.BeginOutputReadLine();
            进程.BeginErrorReadLine();
            if (!进程.WaitForExit(超时毫秒)) { try { 进程.Kill(true); } catch { /* 忽略 */ } return false; }
            return 进程.ExitCode == 0;
        }
        catch (Exception e) { GD.PrintErr($"[Tts] 执行 {Path.GetFileName(exe)} 失败: {e.Message}"); return false; }
    }

    private static void 确保播放节点()
    {
        if (GodotObject.IsInstanceValid(_播放节点)) return;
        if (Engine.GetMainLoop() is not SceneTree 树 || 树.Root == null) return;
        _播放节点 = new TtsPlayer { Name = "TtsVoice" };
        树.Root.AddChild(_播放节点);
    }

    private static void 停播放()
    {
        try { if (GodotObject.IsInstanceValid(_播放节点)) _播放节点.停播放(); } catch { /* 忽略 */ }
    }

    // ================= 内部 =================

    private static bool 初始化()
    {
        // 注意：这里**不能**用 可用 做短路 —— 可用 反映的是「当前引擎」，与 SAPI 合成器是否建好无关
        //（曾在还没建合成器时就返回 true → SpeakAsync 空引用，探针抓到过一次）。
        if (_合成器 != null) return true;
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

    /// <summary>探针：`说()` 会走哪个分支（edge / sapi / silent）—— 纯判定，不发声。</summary>
    public static string 探针_引擎分支 => 引擎 == "edge" ? (string.IsNullOrEmpty(Edge路径) ? "silent" : "edge") : "sapi";

    /// <summary>探针：临时切换引擎（测分支用，不发声）。</summary>
    public static void 探针_设引擎(string 名) => 引擎 = (名 ?? "").Trim().ToLowerInvariant();

    /// <summary>探针：临时替换/清空 edge 命令（测查找链用）；传 null 清空。</summary>
    public static void 探针_设Edge命令(string 命令)
    {
        Edge命令 = 命令 ?? "";
        _edge查找过 = false;
        _edge路径 = null;
    }

    /// <summary>探针：强制「找不到 edge」（模拟没装），验证「不出声」路径；false 恢复查找。</summary>
    public static void 探针_禁Edge(bool 禁)
    {
        _探针_禁Edge = 禁;
        _edge查找过 = false;
        _edge路径 = null;
    }

    /// <summary>探针：edge-tts 实际解析到的路径（null = 没找到）。</summary>
    public static string 探针_Edge路径 => Edge路径;

    /// <summary>探针：edge 合成 **MP3 文件**（同步，无需声卡/无需播放）→ 返回字节数（0 = 失败）。</summary>
    public static long 探针_合成到文件_Edge(string 文本, string 输出路径)
    {
        var exe = Edge路径;
        if (string.IsNullOrEmpty(exe)) return 0;
        try
        {
            var 目录 = Path.GetDirectoryName(输出路径);
            if (!string.IsNullOrEmpty(目录)) Directory.CreateDirectory(目录);
            var 参 = new List<string> { "--voice", Edge语音, "--text", 文本, "--write-media", 输出路径 };
            if (!跑进程(exe, 参, 30000)) return 0;
            return System.IO.File.Exists(输出路径) ? new FileInfo(输出路径).Length : 0;
        }
        catch (Exception e) { GD.PrintErr($"[Tts] 探针 edge 合成失败: {e.Message}"); return 0; }
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

/// <summary>
/// TTS 播放节点（挂在主场景树的隐藏节点）：只负责把合成好的 MP3 放出来 + 被打断时停下。
/// 独立成 Node 是因为 `AudioStreamPlayer` 必须在场景树里才能发声，而 `Tts` 是静态类。
/// </summary>
public partial class TtsPlayer : Node
{
    private AudioStreamPlayer _播放器;

    public override void _Ready()
    {
        _播放器 = new AudioStreamPlayer { Name = "Voice" };
        AddChild(_播放器);
    }

    /// <summary>播放一个 MP3 文件（读进内存，文件随后可删）。</summary>
    public void 播放(string 路径, int 音量)
    {
        try
        {
            var 字节 = Godot.FileAccess.GetFileAsBytes(路径);
            if (字节 == null || 字节.Length == 0) { GD.PrintErr($"[Tts] MP3 空文件: {路径}"); return; }
            _播放器.Stream = new AudioStreamMP3 { Data = 字节 };
            _播放器.VolumeDb = Mathf.LinearToDb(Mathf.Clamp(音量 / 100f, 0.0001f, 1f));
            _播放器.Play();
            GD.Print($"[Tts] 播放 edge 语音（{字节.Length} 字节）");
        }
        catch (Exception e) { GD.PrintErr($"[Tts] 播放失败: {e.Message}"); }
    }

    public void 停播放() { try { _播放器?.Stop(); } catch { /* 忽略 */ } }

    public override void _ExitTree() { 停播放(); }
}
