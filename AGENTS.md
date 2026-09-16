# AGENTS.md — AIPet

> 给 AI 代理（及协作者）的项目指南。本文件是**契约**：改动前先读，改完遵守。
> 仓库语言：中文（注释与文档用中文，代码标识符用沿用旧仓库，见「开发约定」）。

---

## 0. 项目定位（一句话）

**AIPet 是「Agent 驱动的桌宠前端」，不是工具箱。**

- 旧定位：套皮工具箱——右键菜单/拖拽/粘贴触发外部工具（8 个 bin 工具）。
- **新定位**：**桌宠是 Agent 的脸与手，Agent 是桌宠的脑**。用户通过桌宠与 Agent（Hermes）互动；桌宠提供形象、动画、语音、交互，Agent 提供对话、记忆、决策。
- **外部工具链保留**：原 8 个 bin 工具（CopyQ/Everything/Flameshot/ImageMagick/Lively/Optimizer/Sherpa-onnx/yt-dlp）继续可用。**不必让所有功能都走 Agent**——确定性、高频、本地的操作走外部工具链更快更省（快速路径）；需要理解/决策/生成的能力才走 Agent（决策路径）。两条路并存，按需选路。

---

## 分册索引（各层细节都在这里，别在本文件里找）

| 想知道什么 | 看哪 |
|---|---|
| 灵魂层：`soul.md` 结构、思维范式、数值层（心情/精力/亲密）、数值可视化 | `script/Soul/README.md` |
| 身体层：状态效果表、交互时序、自主行为节律、环境感知（P6） | `script/State/README.md` |
| 界面层：面板群（聊天/工具栏/配置/状态窗）、窗口铁律、动画池机制、云母样式 | `script/UX/README.md` |
| 能力层：ACP 客户端、AgentBridge、指令通道协议、白名单与安全边界、人格注入 | `script/Agent/README.md` |
| 模式层：办公 / 游戏切换接口 | `script/Mode/README.md` |
| 回归探针：有哪些、怎么跑、探针方法论（怎么写出不骗自己的断言） | `tests/README.md` |
| 资产导入：VPet → mods 的导入器规则与踩坑 | `tools/README.md`（见该文件） |
| 设计补充（Why/What）、阶段计划与施工记录 | `document/idea.md`、`document/plan.md` |

**定位**：AGENTS.md = 总纲（**How 的框架**）；各层 README = 该层的**细节与踩坑**；`document/idea.md` = 设计补充（Why/What）。

> **章节编号故意保留原样**（所以你会看到 §1 之后直接是 §5）：§2 / §3 / §4 / §6 已各自搬到对应分册，
> 但**编号不变**，这样文档与代码注释里「见 §3.1」「见 §10 坑 #16」这类交叉引用全部继续有效。


## 1. 架构总览（六层）

| 层 | 职责 | 状态 |
|---|---|---|
| **灵魂层** | 维护三张持久化的**灵魂表**（SOUL / 记忆 / 状态），一切「我」的数据源 |  核心 → `script/Soul/README.md` |
| **身体层** | **状态机 + 行为链**：驱动动画、反馈、表现 |  核心 → `script/State/README.md` |
| **模式层** | 办公 / 游戏**一体**：桌宠本体即游戏的一部分，无缝切换、进度保留；游戏玩法留接口待扩展 | 🆕 接口 → `script/Mode/README.md` |
| **关系层** | 亲密度 / 养成 / 专属感 |  空置，占位不实现（`soul.md` 中 `# 关系` 节保留为空） |
| **行为层** | 现有交互：右键菜单、拖拽、剪贴板、语音关键词 | 🔒 维持现状（`mods/` + `ModLoader` 管线不动） |
| **能力层** | Agent 接入（理解/决策/生成）+ 外部工具链（确定性/高频/本地操作）双路 | 核心 → `script/Agent/README.md` |

**层级间数据流（单向为主）**：

```
用户输入(粘贴/语音/拖拽/点击)
   │
   ▼
行为层(现有交互) ──► 能力层(Agent/Hermes 桥或本地工具链)
                          │ 响应: 文本 + 命令
                          ▼
                   灵魂层(读写表: 记忆/状态)
                          │
                          ▼
                   身体层(状态机+行为链) ──► 动画/气泡/TTS
                          ▲
                          └──────── 模式层(办公/游戏) 切换时接管表现
```

---

## 5. 目录地图（真实路径）

```
D:/Games/Github/AIPet/
├── project.godot          # Godot 4.7.2 .NET (GL Compatibility)
├── desktop.csproj         # C# 项目（SDK 4.7.2 —— Godot 4.7.2 编辑器打开时自动升级，保留）
├── script/
│   ├── Logic/             # Main.cs(总控) IO.cs(Info/Global 双字典) 等
│   ├── Loader/            # ModLoader / AnimLoader / CommandLoader / ... 加载管线
│   ├── State/             #  身体层状态机 (StateMachine.cs) + 环境感知 (EnvironmentSense.cs, P6 默认关)
│   ├── Soul/              # 🆕 灵魂/数值层 (SoulTable.cs 读 soul.md；StatsTable.cs 心情/精力/亲密)
│   ├── Agent/             #  能力层 (IAgentBackend 抽象 / AgentBackendRegistry 注册表 /
│   │                      #     AcpClient / NullAgentBackend / AgentBridge)
│   ├── Mode/              #  模式层 (ModeManager.cs)
│   ├── Audio/             # Kws.cs 语音关键词 (Sherpa-onnx)
│   ├── Steam/             # SteamNode / WorkShop（工坊分发，保留）
│   ── UX/ Asset/ Util/   # 现有辅助 + ChatBox.cs(聊天框) / ToolBar.cs(工具栏) /
│                          #   SettingsWindow.cs(配置窗) / StatsWindow.cs(状态窗) / MicaTheme.cs(云母样式)
├── mods/                  # 行为层：main_command / main_txt / main_file / main_anim / workshop（不动）
├── settings/              # ⚠️ 可被 Godot 读取（config/ 有 .gdignore 会被忽略）
│   ├── agent.json         # 后端配置（backend/executable/工作目录）
│   └── soul_template.md   # 人格模板
├── tests/                 # headless 验证（AcpTest / ChatFlowTest）
└── AGENTS.md              # 本文档
```

---

## 7. 实现顺序建议（路线图）

1. **SoulTable.cs**：读取人格文件 `soul.md` + 注入用文本生成（基础，无依赖）。
2. **ModeManager.cs**：双模式接口 + 进度存取桩（轻量，先钉住模式层）。
3. **CharAnim.cs** ✅ 已加 `PlayState(string)` 公开入口（状态机的地基）。
4. **StateMachine.cs（含行为链）**：接现有动画，接 `set_state/speak/play_anim/queue_chain`。
5. **AgentBridge.cs**：起 HTTP 服务，实现 `ask/reply/commands` 最小闭环。
6. **外部工具链保留**：原有 bin 工具链继续可用；新功能按「双路原则」选路，不强制走 Agent。
7. 接现有入口（粘贴/语音/拖拽 → 桥 → Agent → 回复/TTS + 状态机）。

---

## 8. 开发约定

- **引擎**：Godot 4.7.2 .NET；渲染 GL Compatibility。
- **标识符**：代码英文标识符（类名/成员/变量/常量），**注释与文档用中文**。旧代码沿用中文。
- **.uid 文件**：新增 `.cs` 后，必须在 Godot 编辑器打开项目一次生成 `.cs.uid`，否则导入失败。
- **验证闭环**：改代码 → `dotnet build`（路径见下）→ Godot 编辑器运行 → `get_debug_output` 查错。
- **不破坏行为层**：`mods/` 结构与 `ModLoader` 管线是现有用户资产，默认只加不改。
- **游戏属性一体**：办公与游戏不严格隔离，办公行为可挂 `Game` 分支作游戏内容；游戏进度不入灵魂表（归 ModeManager/GameSession）。
- **git**：提交前 `git status` 自查；`.godot/` 等构建缓存不提交。

```
dotnet build D:/Games/Github/AIPet/desktop.csproj
```

---

## 9. 当前已知事项

> **文档关系**：`AGENTS.md` 是**主文档**（工程契约，How）。`idea.md` 是**对项目的设计补充**（意图，Why/What），两者**解耦**——本文件不引用 idea 的具体小节，只描述工程约束。

- `desktop.csproj` 的 SDK 已由 Godot 4.7.2 编辑器自动从 `4.5.1` 升级为 `4.7.2`（用户无手动改动；编辑器打开即自动改写）。**保留**该改动——本地引擎是 4.7.2，还原后打开又会被升回。
- 架构图参考：`D:/Games/Github/ACPPet-架构图.html`（旧名文件，内容对应本项目）。
- 骨架文件（`script/Soul/`、`script/State/`、`script/Agent/`、`script/Mode/`）已建，均为桩/TODO，待按此文档实现。
- 灵魂职责已重新定义为「人格 prompt 资产」：数值归 `state/stats.json`，用户画像归 `soul/profile.md`，记忆流水归 `soul/memory.jsonl`，三者独立于 soul.md。

---

## 10. 踩坑清单（全部为实测结论，改代码前先看这一节）

> **本清单已按层拆分**：与某一层强相关的条目在那一层的 README 里（**编号保持全局不变**，所以「见坑 #N」这类引用仍然有效）。
> 本文件只保留**跨层**的几条（下面 4 条）：
>
> | 编号 | 在哪 |
> |---|---|
> | #1 / #2 / #3 / #5 / #12 | `script/UX/README.md`（界面 / 窗口） |
> | #14 | `script/State/README.md`（状态机是动画的所有者） |
> | #9 / #10 / #11 / #13 | `script/Agent/README.md`（协议 / 指令通道） |
> | #6 / #15 / #17 / #18 | `tests/README.md`（探针方法论） |
> | #4 / #7 / #8 / #16 | **本文件**（跨层：启动 / 目录约定 / C# 命名冲突） |

4. **麦克风不要在启动时打开**。`AudioStreamPlayer2D` 挂 `AudioStreamMicrophone` + `autoplay=true` 会让程序一启动就占用录音设备，无设备时报 `WASAPI: init_input_device error`（且属隐私问题）。→ 规则：**按需启动**（`Kws` 只在语音模型真加载成功后才开）。

7. **`config/` 目录有 `.gdignore`，Godot 完全忽略它**。放在其中的 json 在 `res://` 下读不到（`soul_template.md`、`agent.json` 都曾中招）。→ 规则：**运行时需要读取的配置放 `settings/`**；`config/` 只放模组用的数据（`i18n.csv` 等由代码按绝对路径读取）。

8. **`FileAccess` 二义性**：`Godot.FileAccess` 与 `System.IO.FileAccess` 同名。→ 规则：同时 `using Godot;` 和 `using System.IO;` 时，显式写 `Godot.FileAccess`。

16. **两个「与 BCL 同名」的坑，一律写全限定名**：
    - `Environment`：`Godot.Environment` vs `System.Environment`（用 `TickCount` 必炸）→ `System.Environment.TickCount`。
    - `FileAccess`：`Godot.FileAccess` vs `System.IO.FileAccess`。
    规律：Godot 的 C# 命名空间里有一批与 BCL 同名的类型，凡是用「名字很通用」的 BCL 类型，一律全限定。
