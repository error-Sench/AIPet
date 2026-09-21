# 语音层（Audio）—— 分册

> `AIPet-Agent.md` 的分册。相关代码：`script/Audio/`；改动后跑 `tests/TtsProbe`（以及 Kws 相关的手工验证）。

## 1. 语音输出 TTS（`Tts.cs`）

**设计决策（2026-09-19 定稿）**：语音输出**默认关闭**（`config/tts.json` 的「启用」= false）；开启后**默认用 Edge 在线语音（晓晓）**，可切 `sapi`（Windows 自带）。**不内置任何模型/语音库**；合成失败（没装/断网）→ **静默不出声、不回退**（主人定：不回退系统语音）。

| 事项 | 做法 |
|---|---|
| 引擎 | `edge`（**默认**；Edge 在线语音——需 `tools/install_edge_tts.ps1` 装小依赖、联网合成）｜`sapi`（手动选项：Windows 自带 `System.Speech`，完全本机、机械）|
| 选声 | edge 默认 `zh-CN-XiaoxiaoNeural`（晓晓；`config/tts.json` 的 `edge语音` 可换）；sapi 自动挑中文女声（本机实测 `Microsoft Huihui Desktop`）|
| 说什么 | **桌宠的气泡**（`Dialogue.显示临时标题` → `Tts.说`）：它"冒出来"的话就念。**聊天面板里的长篇回复不念** |
| 清洗 | 去 BBCode、去 ``` 围栏块（含 ```pet 指令块）、去 markdown 记号、空白压成单空格（`Tts.清洗`，纯函数可断言）|
| 打断 | 新泡泡来了 `SpeakAsyncCancelAll()` → 不排队念小作文 |
| 降级 | edge 没装/断网 → **不出声**（不回退系统语音）；sapi 不可用/初始化异常 → **静默降级**（只出气泡，不出声，永不报错卡住）|

配置 `config/tts.json`：`启用`(**false，默认关**) / `引擎`(edge) / `edge语音`(zh-CN-XiaoxiaoNeural) / `edge命令`(""=自动) / `声音`(""=自动；sapi 用) / `语速`(0, -10~10) / `音量`(100) / `最大字数`(80，超过不念；0=不限)。

**验证**：`tests/TtsProbe`（headless，27 断言）—— 配置 / 清洗 / 门控 / **默认引擎 edge** / 系统语音枚举与自动挑选 /
**真合成到文件**（edge 出 MP3 / sapi 出 WAV；不需要声卡，headless 也能端到端验证"真的能出声"）/ 与气泡联动。
探针**不真的发声**（会打断主人）：验证出声用写文件的方式。

**给用户的提示**：edge 想换声音 → 改 `config/tts.json` 的 `edge语音`；sapi 想换声音/装中文女声 → Windows 设置 → 时间和语言 → 语音 → 管理语音（装完重启桌宠即可被自动挑中）。

## 2. 语音输入 KWS（`Kws.cs`）

关键词唤醒（Sherpa-onnx），模型路径走 `Main.工具路径字典` 的 `KWS-*` 键（mod 的 `config/tool.json` 提供）。
隐私/启动约束见 `AIPet-Agent.md` 坑 #4（**麦克风按需启动**，不 autoplay）。
