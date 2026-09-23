using System;
using System.Collections.Generic;
using System.IO;
using desktop.script.Audio;
using desktop.script.Loader;
using desktop.script.Soul;
using desktop.script.State;
using desktop.script.Util;
using desktop.script.UX;
using Godot;
namespace desktop.script.Logic;

/// <summary>
/// 程序引导（game.tscn 的 Main 节点）+ 全局只读表。
/// 2026-09-24 解构：mod 脚本执行管线已迁 <see cref="ScriptRunner"/>、文件/剪贴板输入管线已迁 <see cref="FileInput"/>——
/// Main 只剩「启动顺序」与「全局字典」两件事；数据模型见 <see cref="ModModels.cs"/>（原 Date.cs，名实不符已改名）。
/// </summary>
public partial class Main:Node
{
	public const string 执行函数名 = "execute";
	public const string 本地化文件名 = "i18n.csv";
	public const string 配置文件名 = "info.json";
	public const string 脚本组文件名 = "group.json";
	public const string 工具配置文件名 = "tool.json";
	public const string 初始化配置文件名 = "init.json";
	public const string 配置信息文件名 = "config.json";
	public static readonly Dictionary<string, 人物数据> 人物字典 = new();
	public static 人物数据 显示人物 => 人物字典[配置信息字典.GetValueOrDefault("当前人物","loris")];
	public static readonly List<可见脚本信息> 配置脚本列表 = [];
	public static readonly Dictionary<string, string> 工具路径字典=new();
	public static readonly Dictionary<string, string> 配置信息字典=new();

	/// <summary>本次是否首次运行（数据目录还没建过）——_Ready 开头判定，供首启问候用。</summary>
	private static bool 首启;

	/// <summary>探针用：禁掉首启问候（headless 探针要确定性，别让欢迎气泡插进来）。</summary>
	public static bool 探针_禁首启提示;

	/// <summary>探针用：覆写「生日」（MM-dd）——隔离测试，不碰 config。</summary>
	public static string 探针_生日覆写;
	public override void _Ready()
	{
		// 首次运行判定（数据目录里还没有我们的画像文件）——必须在任何 user:// 写入之前判断
		首启 = !Godot.FileAccess.FileExists("user://soul/profile.md");
		LoadUtil.初始化();
		Soul.PhraseTable.加载();       // 本地话语表（config/phrases.json；缺失走内置兜底）
		UX.Dialogue.载入配置();        // 气泡显示时长（config/behavior.json 的 气泡显示秒；默认 4s）
		Audio.Tts.载入配置();          // 语音输出（系统 TTS；不内置模型）
		UX.NetSpeedBubble.载入外观配置();  // 网速气泡外观（字号/边距/透明度；随组件目录 mods/toolbar/netspeed/config.json）
		UX.NetSpeedBubble.启动恢复();      // 按上次开关状态恢复（入口＝工具栏「网速监控」；删/禁用 mod 即不再恢复）
		ContextTable.确保数据文件();   // 画像/记忆两个数据文件（只创建、不覆盖）
		ContextTable.生成();           // 上下文接口文件 user://context.md（Agent 自主读取，见 Soul/README.md 硬规则）
		// 首启引导（打包审计 A7）：第一次运行 → 打一行日志，并在**入场完成后**说一声
		// （入场期间冒泡会顶状态、抢入场动画 → 挂到入场门的一次性回调上）
		if (首启 && !探针_禁首启提示)
		{
			GD.Print($"[AIPet] 首次运行：数据目录 = {ProjectSettings.GlobalizePath("user://")}");
			StateMachine.入场完成后 += () => Dialogue.显示临时标题($"初次见面，我是{NameTable.当前名字}～右键我 → 配置，能看到我的数据都存在哪");
		}

		// 生日彩蛋（一年一次）：config/config.json 的「生日」= MM-dd；命中当天 → 入场完成后播一遍 BDay 序列
		var 生日 = 探针_生日覆写 ?? 配置信息字典.GetValueOrDefault("生日", "");
		if (生日.Length == 5 && 生日 == DateTime.Now.ToString("MM-dd"))
		{
			GD.Print($"[AIPet] 生日命中（{生日}）→ 入场后播 BDay 彩蛋");
			StateMachine.入场完成后 += () =>
			{
				StateMachine.SetState(StateMachine.Bday);
				if (Soul.PhraseTable.有("生日")) Dialogue.显示临时标题(Soul.PhraseTable.取("生日"));
			};
		}

		ModLoader.加载模组();
		Agent.ActionInbox.启动();     // 指令收件箱（工具通道）：MCP pet_command → user://actions.jsonl
		
		Kws.TurnOn();
		Context.显示指令列表();
		CharAnim.载入人物动画();
		Dialogue.显示临时标题(DialogueLoader.默认对话.入场招呼);
	}
	public static bool IgnorePath(string path) => Path.GetFileName(path).StartsWith($"_");
	public static void 游戏结束()
	{
		ConfigLoader.CleanupProcesses();
	}
}
