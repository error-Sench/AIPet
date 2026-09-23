using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using desktop.script.Audio;
using desktop.script.State;
using desktop.script.Util;
using desktop.script.UX;
using Godot;
using DialogueManagerRuntime;

namespace desktop.script.Logic;

/// <summary>
/// mod 脚本执行管线（2026-09-24 从 `Main.cs` 解构拆出——原来「引导 + 全局表 + 执行管线」三类职责挤在一个文件里）。
/// 职责链：五个入口（右键菜单 / 聊天面板 / 工具栏 / 语音关键词 / 拖入·粘贴的脚本选项）都汇聚到 <see cref="选择脚本"/>
/// → 干活过渡（`StateMachine.开始干活`）→ [option 脚本先走预处理 + `option.dialogue` 对话选项] →
/// `Task.Run` 里执行 mod 的 `execute.gd`（传入 `IO.单例.Info` 数据总线）→ 回主线程收尾
/// （庆祝 / 事件池 / tip 气泡 / `StateMachine.结束干活`）。
/// 静态类，无场景节点；回主线程用 `Callable.From(...).CallDeferred()`。
/// </summary>
public static class ScriptRunner
{
    /// <summary>当前正在执行的脚本（对话选项结束后续跑用）。</summary>
    public static 脚本信息 当前脚本;

    public static void 选择脚本(脚本信息 脚本信息)
    {
        StateMachine.NotifyInteraction("task"); // 右键/粘贴/拖入/语音/面板 5 个入口都汇聚到这里
        StateMachine.开始干活();   // P10：先播「起身」过渡（VPet Switch_Up）再进 working
        当前脚本 = 脚本信息;
        if (!string.IsNullOrEmpty(脚本信息.tool))
        {
            IO.单例.set("tool", LoadUtil.GetExternalToolPath(脚本信息.tool));
        }
        if (脚本信息.tools != null)
        {
            var tools = 脚本信息.tools.Select(LoadUtil.GetExternalToolPath).ToArray();
            IO.单例.set("tools", tools);
        }
        IO.单例.set("script", 脚本信息.Path);
        IO.单例.set("mod", 脚本信息.ModPath);
        IO.单例.set("out", LoadUtil.GetOutputDir());
        if (脚本信息.option)
        {
            if (脚本信息.prepare)
            {
                运行预处理函数(脚本信息);
            }
            else
            {
                预处理函数完成();
            }
        }
        else
        {
            运行执行函数(脚本信息);
        }
    }

    public static void 打开配置(int index)
    {
        if (index >= Main.配置脚本列表.Count) return;
        var 配置路径 = Main.配置脚本列表[index].config;
        var absolutePath = ProjectSettings.GlobalizePath(配置路径);
        if (OS.GetName() == "Windows")
        {
            // Windows: 使用 powershell 或 cmd 调用 start 指令
            OS.Execute("cmd.exe", ["/C", "start", "", absolutePath]);
        }
        else if (OS.GetName() == "macOS")
        {
            // macOS: 使用 open 命令
            OS.Execute("open", [absolutePath]);
        }
        else if (OS.GetName() == "X11") // Linux
        {
            // Linux: 使用 xdg-open 命令
            OS.Execute("xdg-open", [absolutePath]);
        }
        CharAnim.开始庆祝();
    }

    /// <summary>`option.dialogue` 对话选项走完 → 续跑执行函数（`Dialogue.cs` 对话结束时调用）。</summary>
    public static void 对话结束() => 运行执行函数(当前脚本);

    private static async void 加载对话(string absolutePath)
    {
        try
        {
            if (!File.Exists(absolutePath)) return;
            var rawText = await File.ReadAllTextAsync(absolutePath);
            var temporaryResource = DialogueManager.CreateResourceFromText(rawText);
            Dialogue.开始对话(temporaryResource);
        }
        catch (Exception e)
        {
            GD.Print($"对话读取错误:[{e.Message}]");
        }
    }

    private static void 运行执行函数(脚本信息 脚本信息)
    {
        if (脚本信息.excute)
        {
            if (脚本信息.wait)
            {
                Dialogue.显示标题("wait");
            }
            // 直接调用通用提取函数
            RunScriptTask(脚本信息, Main.执行函数名, 执行函数完成);
        }
        else
        {
            执行函数完成();
        }
        Dialogue.脚本结束();
    }

    private static void 执行函数完成()
    {
        StateMachine.结束干活();    // P10：先播「坐下」过渡（VPet Switch_Down）再回 idle
        StateMachine.NotifyInteraction("task_done");
        if (当前脚本.cheer) CharAnim.开始庆祝();
        // 行为事件（#3）：任务完成也进事件池（程序侧；日志用途，不打扰主人）
        State.EventPool.记("任务完成", State.EventPool.归属.程序, string.IsNullOrEmpty(当前脚本.id) ? "任务完成" : $"任务完成：{当前脚本.id}");
        var tip = string.IsNullOrEmpty(当前脚本.tip) ? "done" : 当前脚本.tip;
        if (IO.单例.get("tip", out var value))
        {
            tip = (string)value;
        }
        if (当前脚本.showOut)
        {
            IO.单例.Info.Remove("tip");
            Dialogue.文件处理完成(tip);
        }
        else
        {
            if (tip == "done")
            {
                Dialogue.关闭标题();
            }
            else
            {
                Dialogue.显示临时标题(tip);
            }
            IO.单例.Info.Clear();
        }
    }

    private static void 运行预处理函数(脚本信息 脚本信息)
    {
        // 直接调用通用提取函数
        RunScriptTask(脚本信息, 预处理函数名, 预处理函数完成);
    }

    private static void 预处理函数完成()
    {
        加载对话(Path.Combine(当前脚本.Path, 对话文件名));
    }

    /// <summary>注册 mod 声明的脚本（`CommandLoader` / `indexLoader` 加载 info.json 时调用）：
    /// 可见脚本收图标与配置入口；关键词脚本展开进 KWS 词表。</summary>
    public static void 注册脚本信息(脚本信息 脚本信息)
    {
        if (脚本信息 is 可见脚本信息 可见脚本信息)
        {
            可见脚本信息.LoadIcon();
            if (!string.IsNullOrEmpty(可见脚本信息.config))
            {
                可见脚本信息.config = Path.Combine(脚本信息.Path, 可见脚本信息.config);
                Main.配置脚本列表.Add(可见脚本信息);
            }
        }

        if (脚本信息 is 关键词脚本信息 { keywordList: not null } 关键词脚本)
        {
            var 关键词列表 = Kws.关键词列表;
            var 关键词映射 = Kws.关键词映射;
            foreach (var 关键词信息 in 关键词脚本.keywordList)
            {
                if (关键词信息.keywords == null) continue;
                关键词信息.对应脚本 = 关键词脚本;
                foreach (var 关键词字段 in 关键词信息.keywords)
                {
                    var 分段 = 关键词字段.Split("@");
                    if (分段.Length == 1) continue;
                    关键词列表.Add(关键词字段);
                    关键词映射[分段[1]] = 关键词信息;
                }
            }
        }
    }

    private const string 预处理函数名 = "prepare";
    private const string 对话文件名 = "option.dialogue";

    private static void RunScriptTask(脚本信息 脚本信息, string gdMethodName, Action callback)
    {
        // 1. 加载并实例化脚本
        var scriptPath = Path.Combine(脚本信息.Path, $"{gdMethodName}.gd");
        var 脚本 = GD.Load<GDScript>(scriptPath);
        var 脚本实例 = (GodotObject)脚本.New();
        // 2. 异步执行任务（完成回调经 Callable 推迟回主线程——Godot API 不能在后台线程碰）
        Task.Run(() =>
        {
            try
            {
                脚本实例.Call(gdMethodName, IO.单例.Info);
                Callable.From(callback).CallDeferred();
            }
            catch (Exception e)
            {
                GD.PrintErr($"执行脚本 {gdMethodName} 时出错: {e.Message}");
            }
        });
    }
}
