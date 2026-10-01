using System;
using Godot;
using desktop.script.UX;
using desktop.script.Util;

namespace desktop.script.State;

/// <summary>
/// 音乐反应（组③，增强互动）：系统在放声音（默认播放设备的峰值音量持续超过阈值）→ 起身跳舞
/// （`music` 状态，包裹段自管：A 起跳 → 主段舞蹈循环 → C 收尾），安静 `静音秒` 后收场。
/// <para>
/// **两级阈值**（2026-10-01 照抄官方 `MainWindow.cs:1251-1268` MusicTimer_Elapsed）：
/// 一级 `音量阈值`（官方 MusicCatch）= 常规舞（B_Loop，同档 B 组内**每圈随机重掷**）；
/// 二级 `刺激阈值`（官方 MusicMax）= 嗨档舞（Single）。跳舞期间**持续复评**（每 `复评秒` 一轮），
/// 档位一变立刻换舞——这就是官方「_1/_2 变体偶尔插入防单调」的来源。
/// **只读峰值不读内容、不落盘、不联网**。
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
    public static float 音量阈值 = 0.02f;    // 一级：峰值超过算「有声音」（官方 MusicCatch）
    public static float 刺激阈值 = 0.5f;    // 二级：复评期平均超过算「嗨」→ 换 Single 舞（官方 MusicMax）
    public static float 识别秒 = 3f;         // 连续有声多久才开跳（官方 3s）
    public static float 静音秒 = 2f;         // 安静多久收场
    public static float 采样间隔 = 0.5f;
    /// <summary>跳舞期间的档位复评周期（秒）。官方 = MusicTimer 200ms × 10 次采样 = **2s** 一轮。</summary>
    public static float 复评秒 = 2f;

    /// <summary>探针：注入假峰值（0~1；null = 读真实系统音量）。</summary>
    public static float? 探针_峰值覆写;

    /// <summary>探针：忽略演出闸门（headless 下鼠标/窗口尺寸不可靠）。</summary>
    public static bool 探针_忽略闸门;

    public static float 当前峰值 { get; private set; } = -1f;
    public static bool 音乐中 => StateMachine.CurrentState == StateMachine.Music;
    /// <summary>当前是否嗨档（二级阈值命中 → 主段用 Single）。探针可断言换档。</summary>
    public static bool 嗨档 { get; private set; }

    private static float _采样计时;
    private static float _有声秒;
    private static float _静音秒;
    private static double _峰值和;      // 识别期累加（只算有声样本）→ 定开跳初始档
    private static int _采样数;
    private static double _舞期和;      // 跳舞期累加（**无条件**加，照官方 MusicTimer）→ 复评档位
    private static int _舞期数;
    private static bool _已请求收场;   // 收场只请求一次（C 段退出期间状态仍是 music，别每帧重发）

    /// <summary>峰值算不算「有声音」（纯函数，探针直接断言）。</summary>
    public static bool 有声音(float 峰) => 峰 >= 0f && 峰 > 音量阈值;

    /// <summary>复评需要攒几次采样（纯函数）：`复评秒 / 采样间隔`，至少 1 次。</summary>
    public static int 复评采样数() => Math.Max(1, (int)Math.Round(复评秒 / Math.Max(0.001f, 采样间隔)));

    /// <summary>平均音量 → 是否嗨档（纯函数，探针直接断言两级阈值分界）。</summary>
    public static bool 算嗨档(double 平均) => 平均 > 刺激阈值;

    public static void 每帧(float delta)
    {
        if (!启用) return;
        _采样计时 += delta;
        if (_采样计时 >= 采样间隔)
        {
            _采样计时 = 0f;
            采样();
            if (音乐中) 复评档位();
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
        if (音乐中) { _舞期和 += Math.Max(0f, 峰); _舞期数++; }   // 跳舞期无条件累加（官方同款）
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

    /// <summary>
    /// 跳舞期间的**档位复评**（照抄官方 MusicTimer_Elapsed）：攒够 `复评采样数` 次采样算一轮平均，
    /// 平均超二级阈值 = 嗨档；**档位变化立刻换主段**（官方 `if (bef != CurrMusicType) Display_Music()`）。
    /// 算完按官方口径衰减（`sum/=4; count/=4`）——保留 1/4 历史让平均有惯性，音量抖动不会疯狂换档。
    /// </summary>
    private static void 复评档位()
    {
        if (_舞期数 < 复评采样数()) return;
        var 平均 = _舞期和 / _舞期数;
        _舞期和 /= 4; _舞期数 /= 4;          // 官方衰减（整数除法：4→1，保留惯性）
        var 嗨 = 算嗨档(平均);
        if (嗨 == 嗨档) return;
        嗨档 = 嗨;
        GD.Print($"[MusicSense] 档位切换（平均 {平均:0.00} vs 二级 {刺激阈值:0.00}）→ {(嗨 ? "嗨档 Single" : "常规 B_Loop")}");
        StateMachine.重掷music主段();      // 立刻换舞（不等这一圈播完）
    }

    private static void 开跳(bool 嗨)
    {
        嗨档 = 嗨;
        GD.Print($"[MusicSense] 识别到音乐（峰值 {当前峰值:0.00}，{(嗨 ? "嗨档 → Single" : "常规 → 舞蹈")}）→ 起跳");
        StateMachine.包裹主名指定 = 挑歌();
        StateMachine.SetState(StateMachine.Music);
        _有声秒 = 0f;
        _静音秒 = 0f;
        _峰值和 = 0;
        _采样数 = 0;
        _舞期和 = 0;
        _舞期数 = 0;
        _已请求收场 = false;
    }

    /// <summary>
    /// 挑当前该播的舞蹈主段（`StateMachine.重掷music主段` 每圈调 → **B 段每圈重掷变体**，照抄官方
    /// `Display_Music` 每圈重新 `FindGraph`）：嗨档优先 `music-single-{档}`（官方 Single，缺则回退 B_Loop，
    /// 同官方 `mg ??= FindGraph(B_Loop)`）；常规档走 `挑主名("music")` = 按当前档降级链在同档 B 组内随机
    /// （nomal 档 = nomal-1..5，含官方 `Nomal_1`/`Nomal_2` 那两条「防单调」变体）。
    /// </summary>
    public static string 挑歌()
    {
        var 档 = 当前档();
        if (嗨档 && CharAnim.有动画($"music-single-{档}")) return $"music-single-{档}";
        return StateMachine.挑主名("music") ?? "";
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
    public static int 探针_舞期数 => _舞期数;

    public static void 探针_重置()
    {
        探针_峰值覆写 = null;
        _采样计时 = 0f;
        _有声秒 = 0f;
        _静音秒 = 0f;
        _峰值和 = 0;
        _采样数 = 0;
        _舞期和 = 0;
        _舞期数 = 0;
        _已请求收场 = false;
        嗨档 = false;
    }
}
