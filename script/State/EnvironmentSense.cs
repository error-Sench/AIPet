using System;
using System.Runtime.InteropServices;
using Godot;

namespace desktop.script.State;

/// <summary>
/// 环境感知（P6）：桌宠「感知」主人是否在用电脑、是否在全屏应用里。
/// <para>
/// **隐私边界（严格执行）**：
/// 1. **只在本机用**，只取两个数：**空闲秒数**（距上次键鼠输入多久）与**是否全屏**（前台窗口是否盖满显示器）。
/// 2. **不读窗口标题、不读进程名、不记录任何使用轨迹**，更**不发给 Agent**、不落盘。
/// 3. **默认关闭**（`config/behavior.json` 的 `环境感知启用`）——主人不开，它就完全不动、一次也不查。
/// </para>
/// 用途：让「不打扰」更聪明——主人在全屏（游戏/视频/演示）时彻底安静；主人刚回来时打个招呼。
/// 约定：标识符英文，注释中文（见 AGENTS.md §8）。
/// </summary>
public static class EnvironmentSense
{
    /// <summary>是否启用（由 behavior.json 注入；默认 false = 完全不感知）。</summary>
    public static bool 启用 { get; set; }

    /// <summary>空闲多久算「主人离开了」（秒）。</summary>
    public static float 离开阈值秒 { get; set; } = 300f;

    /// <summary>全屏时是否完全静默（不主动走动/不打瞌睡搭话）。</summary>
    public static bool 全屏静默 { get; set; } = true;

    /// <summary>距上次键鼠输入的秒数（未启用时恒返回 0，避免任何查询）。</summary>
    public static float 空闲秒
    {
        get
        {
            if (!启用) return 0f;
            if (_注入空闲秒 >= 0f) return _注入空闲秒;
            return 真实空闲秒();
        }
    }

    /// <summary>前台窗口是否全屏（未启用时恒 false）。</summary>
    public static bool 全屏
    {
        get
        {
            if (!启用) return false;
            if (_注入全屏.HasValue) return _注入全屏.Value;
            return 真实全屏();
        }
    }

    /// <summary>主人现在「不在」（空闲超过阈值）。</summary>
    public static bool 主人不在 => 启用 && 空闲秒 >= 离开阈值秒;

    /// <summary>全屏且要求静默 → 主动行为一律拦下。</summary>
    public static bool 应当静默 => 启用 && 全屏静默 && 全屏;

    /// <summary>闲→忙的边沿（用于「欢迎回来」），由 <see cref="心跳"/> 维护。</summary>
    public static bool 刚回来 { get; private set; }

    private static bool _上次不在;
    private static float _回来冷却;

    /// <summary>节律心跳（由 StateMachine 的心跳调用）。只在启用时做事。</summary>
    public static void 心跳(float 秒)
    {
        if (!启用) { 刚回来 = false; _上次不在 = false; return; }

        if (_回来冷却 > 0f) _回来冷却 = Math.Max(0f, _回来冷却 - 秒);

        var 不在 = 主人不在;
        刚回来 = _上次不在 && !不在 && _回来冷却 <= 0f;
        if (刚回来) _回来冷却 = 60f; // 节流：一分钟内不重复「欢迎回来」
        _上次不在 = 不在;
    }

    // ================= 真实感知（Win32） =================

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private static float 真实空闲秒()
    {
        try
        {
            var 信息 = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref 信息)) return 0f;
            // dwTime 是系统启动以来的毫秒数（32 位会回绕，用 System.Environment.TickCount 对齐）
            // 注意必须写全限定名：Godot.Environment 与 System.Environment 二义（同 FileAccess 那个坑）
            var 已过 = unchecked((uint)System.Environment.TickCount - 信息.dwTime);
            return 已过 / 1000f;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[EnvSense] 读空闲失败: {e.Message}");
            return 0f;
        }
    }

    private static bool 真实全屏()
    {
        try
        {
            var 窗口 = GetForegroundWindow();
            if (窗口 == IntPtr.Zero) return false;
            if (!GetWindowRect(窗口, out var 窗口矩形)) return false;

            var 显示器 = MonitorFromWindow(窗口, MONITOR_DEFAULTTONEAREST);
            var 信息 = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (显示器 == IntPtr.Zero || !GetMonitorInfo(显示器, ref 信息)) return false;

            var 屏 = 信息.rcMonitor;
            // 盖满整块显示器才算全屏（容忍 1px 误差；无边框全屏游戏会差 1~2px）
            const int 容差 = 2;
            return Math.Abs(窗口矩形.Left - 屏.Left) <= 容差 &&
                   Math.Abs(窗口矩形.Top - 屏.Top) <= 容差 &&
                   Math.Abs(窗口矩形.Right - 屏.Right) <= 容差 &&
                   Math.Abs(窗口矩形.Bottom - 屏.Bottom) <= 容差;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[EnvSense] 读全屏失败: {e.Message}");
            return false;
        }
    }

    // ================= 探针专用 =================

    private static float _注入空闲秒 = -1f;
    private static bool? _注入全屏;

    /// <summary>探针：注入假的环境值（-1 / null = 恢复真实感知）。</summary>
    public static void 探针_注入(float 空闲秒值, bool? 全屏值)
    {
        _注入空闲秒 = 空闲秒值;
        _注入全屏 = 全屏值;
    }

    /// <summary>探针：真实感知的一次性读取（不看启用开关，用于验证 Win32 调用本身可用）。</summary>
    public static (float 空闲秒, bool 全屏) 探针_真实读取() => (真实空闲秒(), 真实全屏());

    /// <summary>探针：重置边沿状态。</summary>
    public static void 探针_重置() { _上次不在 = false; _回来冷却 = 0f; 刚回来 = false; _注入空闲秒 = -1f; _注入全屏 = null; }

    /// <summary>一句话状态（诊断/日志用）。</summary>
    public static string 概述 => !启用 ? "环境感知：关" :
        $"环境感知：开（空闲 {空闲秒:0}s / {(主人不在 ? "主人不在" : "主人在")}{(全屏 ? " / 全屏中" : "")}）";
}