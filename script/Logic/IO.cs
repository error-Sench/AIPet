using System.Diagnostics.CodeAnalysis;
using desktop.script.Steam;
using desktop.script.UX;
using Godot;
using Godot.Collections;

namespace desktop.script.Logic;

// ReSharper disable once InconsistentNaming
[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
/// <summary>
/// 全局数据总线（project.godot autoload 单例，GDScript mod 以 `IO.xxx` 直接调用——**类名与 autoload 名不可改**）。
/// Info = 一次脚本执行的输入数据（拖入文件/剪贴板/关键词参数；执行完清空）；Global = 跨执行的路径表（config/mod/save）。
/// 2026-09-24 解构：删掉整块从未被调用的音频播放器（playAudio/setAudioText/stopAudio…，TTS 走 Audio/Tts.cs）；
/// 语言切换（ChangeLang）与工坊发布（PublishItem）保留——GDScript mod 在用。
/// </summary>
public partial class IO : Node
{
    public Dictionary Info = new ();//单例方便dialogue获取
    public Dictionary Global  = new();
    public static IO 单例;
    public static readonly ConfigFile 配置 = new();
    public const string 配置路径 = "user://settings.cfg";
    public override void _Ready()
    {
        单例 = this;
        var err = 配置.Load(配置路径);
        if (err == Error.Ok)
        {
            var lang = 配置.GetValue("Player", "lang", "").AsString();
            if (!string.IsNullOrEmpty(lang))
            {
                TranslationServer.SetLocale(lang);
            }
        }
    }
    public void set(string key, Variant value)
    {
        if(Info==null)return;
        Info[key] = value;
    }
    public Variant get(string key)
    {
        if (Info != null && Info.TryGetValue(key, out var value))
        {
            return value;
        }
        return default;
    }
    public bool get(string key, out Variant value)
    {
        if (Info != null && Info.TryGetValue(key, out value))
        {
            return true;
        }
    
        value = default;
        return false;
    }
    public void setG(string key, Variant value)
    {
        if(Global==null)return;
        Global[key] = value;
    }
    public Variant getG(string key)
    {
        if (Global != null && Global.TryGetValue(key, out var value))
        {
            return value;
        }
        return default;
    }
    public bool getG(string key, out Variant value)
    {
        if (Global != null && Global.TryGetValue(key, out value))
        {
            return true;
        }
        value = default;
        return false;
    }
    public void ChangeLang(string code)
    {
        // 注意：这里传递的是变量 code，而不是字符串 "code"
        CallDeferred(nameof(ChangeLangDef), code);
    }
    private void ChangeLangDef(string code)
    {
        TranslationServer.SetLocale(code);
        Context.显示指令列表();
        配置.SetValue("Player", "lang",code);
        配置.Save(配置路径);
    }

    #region steam

    // ReSharper disable once MemberCanBeMadeStatic.Global
    public void PublishItem(string path)
    {
        var _ = WorkShop.PublishItem(path).GetAwaiter().GetResult();
    }

    #endregion
}