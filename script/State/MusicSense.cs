using System;
using Godot;
using desktop.script.UX;
using desktop.script.Util;

namespace desktop.script.State;

/// <summary>
/// 音乐反应（组③，增强互动）：系统在放声音（默认播放设备的峰值音量持续超过阈值）→ 起身跳舞
/// （`music` 状态，包裹段自管：A 起跳 → 主段舞蹈循环 → C 收尾），安静 `静音秒` 后收场。
/// <para>
/// 官方玩法对照（VPet `MainWindow.cs` Handle_Music / MusicTimer_Elapsed）：音量采样持续 3 秒识别 →
/// 起来跳舞；识别期平均音量再超 `MusicMax` → 换「Single」嗨档舞；安静后播 C_End 收场。
/// 我们同款参数化（阈值/刺激阈值/识别秒/静音秒），**只读峰值不读内容、不落盘、不联网**。
/// </para>
/// <para>
/// 与「主动行为」的区别：**不吃每小时预算**（对声音的反应不是打扰型行为），闸门 = `StateMachine.演出闸门开放`
/// （空闲 + 环境安静 + 入场完成）。探针：`探针_峰值覆写` 注入假音量（headless 可跑全流程）。
/// </para>
/// </summary>
public static class MusicSense
{
    // —— 配置（StateMachine.设置 注入） ——
    public static bool 启用 = true;
    public static float 音量阈值 = 0.02f;    // 峰值超过算「有声音」
    public static float 刺激阈值 = 0.25f;    // 识别期平均超过算「嗨」→ 换 Single 舞（官方 MusicMax）
    public static float 识别秒 = 3f;         // 连续有声多久才开跳（官方 3s）
    public static float 静音秒 = 6f;         // 安静多久收场
    public static float 采样间隔 = 0.5f;

    /// <summary>探针：注入假峰值（0~1；null = 读真实系统音量）。</summary>
    public static float? 探针_峰值覆写;

    /// <summary>探针：忽略演出闸门（headless 下鼠标/窗口尺寸不可靠）。</summary>
    public static bool 探针_忽略闸门;

    public static float 当前峰值 { get; private set; } = -1f;
    public static bool 音乐中 => StateMachine.CurrentState == StateMachine.Music;

    private static float _采样计时;
    private static float _有声秒;
    private static float _静音秒;
    private static double _峰值和;
    private static int _采样数;
    private static bool _已请求收场;   // 收场只请求一次（C 段退出期间状态仍是 music，别每帧重发）

    /// <summary>峰值算不算「有声音」（纯函数，探针直接断言）。</summary>
    public static bool 有声音(float 峰) => 峰 >= 0f && 峰 > 音量阈值;

    public static void 每帧(float delta)
    {
        if (!启用) return;
        _采样计时 += delta;
        if (_采样计时 >= 采样间隔)
        {
            _采样计时 = 0f;
            采样();
        }

        if (音乐中)
        {
            if (_静音秒 >= 静音秒 && !_已请求收场)
            {
                _已请求收场 = true;
                GD.Print("[MusicSense] 安静够了 → 收场（C 段）");
                StateMachine.SetState(StateMachine.Idle);   // 包裹段退出：先播 C 再落地
            }
            return;
        }

        _已请求收场 = false;   // 不在音乐里：标记复位，下次再跳

        // 没在跳：连续有声达识别秒 + 演出闸门开放 → 开跳
        if (_有声秒 >= 识别秒 && (探针_忽略闸门 || StateMachine.演出闸门开放))
        {
            var 嗨 = (_采样数 > 0 ? _峰值和 / _采样数 : 0) > 刺激阈值;
            开跳(嗨);
        }
    }

    private static void 采样()
    {
        var 峰 = 探针_峰值覆写 ?? AudioMeter.读峰值();
        当前峰值 = 峰;
        if (有声音(峰))
        {
            _有声秒 += 采样间隔;
            _静音秒 = 0f;
            _峰值和 += 峰;
            _采样数++;
        }
        else
        {
            _静音秒 += 采样间隔;
            _有声秒 = 0f;
        }
    }

    private static void 开跳(bool 嗨)
    {
        GD.Print($"[MusicSense] 识别到音乐（峰值 {当前峰值:0.00}，{(嗨 ? "嗨档 → Single" : "常规 → 舞蹈")}）→ 起跳");
        StateMachine.包裹主名指定 = 挑歌(嗨);
        StateMachine.SetState(StateMachine.Music);
        _有声秒 = 0f;
        _静音秒 = 0f;
        _峰值和 = 0;
        _采样数 = 0;
        _已请求收场 = false;
    }

    /// <summary>挑舞蹈主段：嗨档优先 Single（官方逻辑）；返回空 = 交给状态机默认挑（按三档组变体随机舞蹈）。</summary>
    private static string 挑歌(bool 嗨)
    {
        var 档 = 当前档();
        if (嗨 && CharAnim.有动画($"music-single-{档}")) return $"music-single-{档}";
        return "";
    }

    /// <summary>三档 → 音乐档位（关着 = 普通；普通档返回 nomal 组）。</summary>
    private static string 当前档()
    {
        if (!StateMachine.设置.三档状态启用) return "nomal";
        return StateMachine.设置.状态档位 switch { "开心" => "happy", "不良" => "poor", _ => "nomal" };
    }

    // ================= 探针专用 =================

    public static float 探针_有声秒 => _有声秒;
    public static float 探针_静音秒 => _静音秒;

    public static void 探针_重置()
    {
        探针_峰值覆写 = null;
        _采样计时 = 0f;
        _有声秒 = 0f;
        _静音秒 = 0f;
        _峰值和 = 0;
        _采样数 = 0;
        _已请求收场 = false;
    }
}
