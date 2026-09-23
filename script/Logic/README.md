# script/Logic —— 总控与数据流（分册）

> 2026-09-24 解构合并后的格局（此前 `Main.cs` 一个文件塞了「引导 + 全局表 + 脚本执行管线」三件事，
> `FileDrop` / `ClipboardRead` / `IndexMatch` 是同一条输入管线拆成三块互调）。
> **命名空间统一为 `desktop.script.Logic`**（与文件夹同名；历史上小写 `logic` 与大写 `Logic` 并存过——
> C# 命名空间大小写不敏感所以没炸，但 IDE 跳转和 grep 都会踩坑，已全仓统一）。

## 文件职责（5 个文件，各管一件事）

| 文件 | 类型 | 职责 |
|---|---|---|
| `Main.cs` | Node（game.tscn 挂载） | **程序引导**：`_Ready` 启动顺序（LoadUtil → PhraseTable → Dialogue → Tts → ContextTable → ModLoader → ActionInbox → Kws → CharAnim）+ 首启引导 / 生日彩蛋；**全局只读表**（人物字典 / 配置信息字典 / 工具路径字典 / 配置脚本列表）与 mod 协议常量（info.json / group.json / i18n.csv 等文件名）；`IgnorePath`（`_` 前缀 = 禁用） |
| `IO.cs` | Node（**autoload**，project.godot 注册名 `IO`） | **数据总线**：`Info`（一次脚本执行的输入，执行完清空）/ `Global`（跨执行路径表 config/mod/save）；`ChangeLang` 语言切换；`PublishItem` 工坊发布。⚠️ **GDScript mod 以 `IO.xxx` 字符串调用（execute.gd / change-language 等）——类名与 autoload 名不可改**。原有一整块从未被调用的音频播放器（playAudio/setAudioText/stopAudio），已删（TTS 走 `script/Audio/Tts.cs`） |
| `ScriptRunner.cs` | 静态类 | **mod 脚本执行管线**：五个入口（右键 / 聊天面板 / 工具栏 / 语音关键词 / 拖入·粘贴选项）→ `选择脚本` → 干活过渡 → [option 先 `prepare.gd` + `option.dialogue` 对话] → `Task.Run` 跑 `execute.gd`（喂 `IO.单例.Info`）→ `Callable.CallDeferred` 回主线程收尾（庆祝 / 事件池 / tip 气泡 / 结束干活） |
| `FileInput.cs` | Node（game.tscn `File/FileInput`） | **文件/网址输入管线**（原 FileDrop + ClipboardRead + IndexMatch 三合一）：拖放（`Root.FilesDropped`）/ 窗口内 Ctrl+V → 解析（.lnk / 目录递归 / 网址拆分）→ 写 `IO.Info` → 按 mod 索引表匹配脚本（单个 / 同扩展名批处理 / 混合扩展名 batch+multi）→ `Dialogue.显示脚本选项`。剪贴板只在**桌宠聚焦时按 Ctrl+V** 才读一次（隐私边界见 `document/隐私与权限模型.md`） |
| `ModModels.cs` | DTO | **mod 协议数据模型**（原 `Date.cs`——名实不符，里面没有日期逻辑，已改名）：脚本信息 / 可见脚本信息 / 关键词脚本信息 / 索引脚本信息 / 目录脚本信息 / 脚本组信息 / 关键词信息 / 抬头信息 / 执行信息 / 人物数据 / 动画信息 / 对话信息 / 招呼信息。消费方：Loader/*（反序列化）、UX（选项菜单）、Kws（词表）、StateMachine/CharAnim（人物/动画） |

## 依赖方向（只准向下）

```
Main（引导） ──→ Loader/* ──→ ModModels（DTO）
ScriptRunner ──→ IO / StateMachine / CharAnim / Dialogue / Kws
FileInput ──→ IO / IndexLoader·CommandLoader / Dialogue
```

- `StateMachine.cs` / `CharAnim.cs` 只依赖 `Main.显示人物`（人物数据）与 DTO——**不依赖执行管线**。
- 回主线程纪律：后台 `Task` 里不碰 Godot API，收尾一律 `Callable.From(...).CallDeferred()`。

## 踩坑

1. **Godot C# 场景脚本类名 = 文件名**：合并节点脚本时类名要跟着新文件名走（FileDrop+ClipboardRead → `FileInput`），game.tscn 的 ext_resource/节点同步改，`load_steps` 记得减。
2. **删「疑似死代码」先查 GDScript**：C# 零引用 ≠ 没人用——mod 的 `execute.gd` 用 autoload 名字符串调用（`IO.ChangeLang` / `IO.PublishItem`）。审计时要 `grep -rn 'IO\.' mods/ --include='*.gd'`。
3. **命名空间大小写**：C# 大小写不敏感（`logic` == `Logic`），所以历史并存没炸；但 `grep 'desktop.script.logic'` 会漏掉大写引用，统一后不再有这个问题。
