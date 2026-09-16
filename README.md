<p align="center">
  <img src="./icon/icon.png" width="200" alt="AIPet Logo">
</p>

# AIPet —— Agent 驱动的桌宠

[![License](https://img.shields.io/badge/License-GPL%203.0-blue.svg)](https://opensource.org/licenses/GPL-3.0)
[![Engine](https://img.shields.io/badge/Engine-Godot%204.7.2%20.NET-orange.svg)](https://godotengine.org/)

> 一只**有自己的状态、会自己动、能跟你的 Agent 说话**的桌宠。
> 定位不是「套皮工具箱」，而是 **Agent 驱动的桌宠前端**：人格、数值、记忆、事件都写在磁盘上，
> 由 Agent 按 skill **自主读取** —— **我们不做主动注入**（硬规则，见 `script/Soul/README.md`）。

---

## 它现在能做什么

- **会说话**（默认开）：桌宠冒出的气泡会被念出来，默认用 **Edge 在线语音（晓晓）**，声音自然；不想联网就改 `settings/tts.json` 的 `引擎` 为 `sapi`（Windows 自带，本机、机械）。装语音：`powershell -ExecutionPolicy Bypass -File tools\install_edge_tts.ps1`；没装/断网会自动回退，不会报错。

- **网速监测气泡**（桌面气泡样式）：命令栏点「网速」→ 桌面右上角出现一行小胶囊 `↓1.2MB/s ↑340KB/s`，左键拖动、右键关闭。嫌大/嫌不透改 `settings/widget.json`（字号 8-22、边距 0-3、透明度 0.3-1.0）。

| 层 | 能力 |
|---|---|
| **身体** | 状态机自主行为：待机小动作、自主走动、打瞌睡、打招呼、被摸摸（三段动画）、**贴边隐藏**（拖到屏幕边缩进去、鼠标靠近探出；判据与对齐方式对照 VPet 官方源码实现）|
| **灵魂** | `soul.md` 人格文件（改文件即改性格，显示名取自 frontmatter 的 `name`）+ 数值层（心情/精力/亲密，驱动表情变体与行为频率）|
| **记忆** | `profile.md` 用户画像 + `memory.jsonl` 记忆流水（**由 Agent 自己写**）+ 事件池 `events.jsonl`（行为日志 + 待 Agent 处理的事件）|
| **能力** | ACP 协议接外部 Agent（Hermes 实测打通）：流式对话、会话恢复、**指令通道**（Agent 用 ` ```pet ` 围栏块指挥桌宠：set_state / speak / play_anim / set_mode / queue_chain / set_mood …）|
| **感知** | 环境感知（**默认关**）：只读「键鼠空闲秒数」与「前台是否全屏」→ 全屏静默、离开/回来打招呼、久坐提醒 |
| **表达** | 语音输出用 **Windows 自带语音**（不内置模型，`settings/tts.json` 可关）+ 气泡 + 动画 |
| **接口** | **上下文接口** `context.md`：把人格/数值/画像/最近记忆/待办事件组装成一份文件，Agent 读一份就够，不必到处翻文件 |

## 快速上手

1. **运行**：解压到**可写目录**（不要放 `Program Files`）→ 双击 `MagicPet.exe`。
   详见 [`document/新用户上手.md`](document/新用户上手.md)。
2. **接上你自己的 Agent**（推荐）：编辑 `settings/agent.json` 的 `backend` / `executable`。
3. **改人格**：编辑数据目录里的 `soul/soul.md`（右键桌宠 → 配置 → 「数据」可直接打开数据目录）。
4. **交出 skill**：把 `dist/skill/SKILL.md` 给你自己的 Agent（Claude Code / Hermes / Codex…），
   它就会按约定自主读数据、写记忆、用指令通道指挥桌宠 —— 见 [`dist/README.md`](dist/README.md)。

数据都在 `%APPDATA%\Godot\app_userdata\desktop\`；隐私边界与全部开关见
[`document/隐私与权限模型.md`](document/隐私与权限模型.md)。

### 交互

* **摸摸**：左键单击。
* **移动**：左键拖拽（拖到屏幕左右边缘会被吸附并缩进去，鼠标靠近会探出）。
* **菜单**：右键呼出（聊天 / 工具栏 / 配置）。
* **文件与文本**：拖放文件到桌宠、或 `Ctrl + V` 粘贴。
* ~~缩放：滚轮~~ —— **已删除**（运行时缩放会破坏动画链，缩放改成启动时读 `settings/pet.json`）。

## 打包（分发者）

```bash
bash tools/package.sh            # 构建 → Godot 导出 → 组装 dist/AIPet-<版本>/ → 打 zip
```
前置：Godot 4.7.2 的**导出模板**（编辑器 → 编辑器菜单 → 管理导出模板）；Godot 可执行文件路径可用环境变量 `GODOT=` 指定。
包结构与缺口清单见 [`document/打包审计.md`](document/打包审计.md)，第三方许可见 [`licenses/`](licenses/THIRD-PARTY-NOTICES.md)。

## 开发

```bash
dotnet build D:/Games/Github/AIPet/desktop.csproj   # 必须 0 个 error CS
bash tools/run_probes.sh                            # 一键跑全部 headless 探针（--all 含需要真实窗口的）
```

**文档地图**（改代码前先看这个）：

| 文件 | 内容 |
|---|---|
| [`AGENTS.md`](AGENTS.md) | **工程契约（主文档）**：六层架构、开发约定、踩坑清单索引 |
| [`document/plan.md`](document/plan.md) | 路线图 + **待办唯一权威清单**（含主人已拍板的决策与「不做」清单）|
| [`document/idea.md`](document/idea.md) | 设计意图（Why / What）|
| `script/*/README.md` | 各层分册：灵魂 / 状态 / UX / Agent / 音频（细节与踩坑都在这里）|
| [`document/新用户上手.md`](document/新用户上手.md) · [`document/隐私与权限模型.md`](document/隐私与权限模型.md) · [`document/打包审计.md`](document/打包审计.md) | 面向用户与分发 |

## 可挂载的外部工具链（原有能力，按需保留）

桌宠可以调起外部工具完成确定性任务（网速监控、截图、搜文件、下视频等），工具清单与路径在 `mods/` 的模组里配置：

| 工具 | 说明 |
|---|---|
| CopyQ / Everything / Flameshot / ImageMagick / Lively / Optimizer / yt-dlp | 见各 mod 的 `config/tool.json` 键与 `info.json` |
| Sherpa-onnx | 语音唤醒（KWS）已内置为 NuGet 依赖；**语音输出改用系统 TTS，不内置** |

> 模组里目录名加下划线前缀 `_` 即可禁用该模组。

---

## 说明

* **项目现状**：学习 Godot 时顺手做的项目，历史代码里有较多面条与 AI 生成代码，正在按 `document/plan.md` 逐层重构。
* **素材致谢**：角色美术来自开源项目 [VPet](https://github.com/LorisYounger/VPet)（**Apache-2.0**）。
* **文档**：`AGENTS.md` 是工程契约，`document/` 是设计与交付文档，`script/*/README.md` 是各层分册。

## 开源协议

本项目遵循 **GPL-3.0**；分发二进制时请随附源码或获取方式，并保留 [`licenses/THIRD-PARTY-NOTICES.md`](licenses/THIRD-PARTY-NOTICES.md)。
