using System;
using System.Runtime.InteropServices;
using Godot;

namespace desktop.script.Util;

/// <summary>
/// 系统默认播放设备的峰值音量（Windows Core Audio `IAudioMeterInformation`，纯 COM 互操作、无第三方库）。
/// 用途：组③「音乐反应」判断系统是否在放声音 —— **只读音量峰值，不读音频内容、不落盘、不联网**。
/// 官方同款：VPet 用 NAudio 的 `MasterPeakValue`（MMDeviceEnumerator → 默认 Render 端点）。
/// 失败（无音频设备等）返回 -1 并（只）告警一次，调用方按「静音」处理。
/// </summary>
public static class AudioMeter
{
    private static bool _不可用;
    private static bool _已告警;
    private static IMMDeviceEnumerator _枚举器;
    private static IAudioMeterInformation _表;

    /// <summary>读当前系统输出峰值（0~1）；失败/不可用返回 -1。</summary>
    public static float 读峰值()
    {
        if (_不可用) return -1f;
        try
        {
            if (_表 == null)
            {
                _枚举器 ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                var hr = _枚举器.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out var 设备);
                if (hr != 0 || 设备 == null) { 不可用("无默认播放设备"); return -1f; }
                var iid = typeof(IAudioMeterInformation).GUID;
                hr = 设备.Activate(ref iid, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out var 接口);
                if (hr != 0 || 接口 is not IAudioMeterInformation 表) { 不可用($"Activate 失败（hr={hr}）"); return -1f; }
                _表 = 表;
            }
            var h = _表.GetPeakValue(out var 峰值);
            if (h != 0) { 不可用($"GetPeakValue 失败（hr={h}）"); return -1f; }
            return 峰值;
        }
        catch (Exception e)
        {
            不可用(e.Message);
            return -1f;
        }
    }

    private static void 不可用(string 原因)
    {
        _不可用 = true;
        _表 = null;
        if (_已告警) return;
        _已告警 = true;
        GD.Print($"[AudioMeter] 系统音量检测不可用（{原因}）→ 音乐反应停用（其它功能不受影响）");
    }

    // —— COM 互操作（槽位顺序必须与 native vtable 一致；用不到的方法不声明或声明成占位） ——

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int 占位_EnumAudioEndpoints();   // 不用；只为保住下一方法的 vtable 槽位
        int GetDefaultAudioEndpoint(int 数据流, int 角色, out IMMDevice 设备);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr 激活参数, [MarshalAs(UnmanagedType.IUnknown)] out object 接口);
    }

    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        int GetPeakValue(out float 峰值);   // vtable 第 1 槽 —— 只用到它，后面的方法无需声明
    }
}
