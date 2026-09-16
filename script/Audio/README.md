# 语音层（Audio）—— 分册

> `AGENTS.md` 的分册。相关代码：`script/Audio/`；改动后跑 `tests/TtsProbe`（以及 Kws 相关的手工验证）。

## 1. 语音输出 TTS（`Tts.cs`）

**设计决策（主人 2026-09-16）**：**默认调用 Windows 自带语音（SAPI5），不内置任何模型/语音库**。

| 事项 | 做法 |
|---|---|
| 引擎 | `System.Speech.Synthesis.SpeechSynthesizer`（NuGet `System.Speech` 8.0.0，Windows 专用）|
| 选声 | 配置指定 > **中文女声** > 中文 > 任意；本机实测自动选中 `Microsoft Huihui Desktop`（zh-CN/Female）|
| 说什么 | **桌宠的气泡**（`Dialogue.显示临时标题` → `Tts.说`）：它"冒出来"的话就念。**聊天面板里的长篇回复不念** |
| 清洗 | 去 BBCode、去 ``` 围栏块（含 ```pet 指令块）、去 markdown 记号、空白压成单空格（`Tts.清洗`，纯函数可断言）|
| 打断 | 新泡泡来了 `SpeakAsyncCancelAll()` → 不排队念小作文 |
| 降级 | 非 Windows / 系统没有可用语音 / 初始化异常 → **静默降级**（只出气泡，不出声，永不报错卡住）|

配置 `settings/tts.json`：`启用`(true) / `声音`(""=自动) / `语速`(0, -10~10) / `音量`(100) / `最大字数`(80，超过不念；0=不限)。

**验证**：`tests/TtsProbe`（headless，16 断言）—— 配置 / 清洗 / 门控 / 系统语音枚举与自动挑选 /
**真合成到 WAV**（`探针_合成到文件`：不需要声卡，headless 也能端到端验证"真的能出声"）/ 与气泡联动。
探针**不真的发声**（会打断主人）：验证出声用写 WAV 的方式。

**给用户的提示**：想换声音/装中文女声 → Windows 设置 → 时间和语言 → 语音 → 管理语音（装完重启桌宠即可被自动挑中）。

## 2. 语音输入 KWS（`Kws.cs`）

关键词唤醒（Sherpa-onnx），模型路径走 `Main.工具路径字典` 的 `KWS-*` 键（mod 的 `config/tool.json` 提供）。
隐私/启动约束见 `AGENTS.md` 坑 #4（**麦克风按需启动**，不 autoplay）。
