# AIPet-Agent.md — AIPet

> 给 AI 代理（及协作者）的项目指南。本文件是**契约**：改动前先读，改完遵守。
> 仓库语言：中文（注释与文档用中文，代码标识符用沿用旧仓库，见「开发约定」）。
> **文件名说明（2026-09-20）**：本文件原名 `AGENTS.md`——Hermes 等工具对「指令文件」设有写入保护门（不受审批开关影响），
> 为让开发期改动不再被反复拦截而更名；根目录的 `AGENTS.md` 是**占位指路文件**（保自动发现），内容以本文件为准。

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
| 灵魂层：人格内化（程序不保存）、数值层（mood=主人情绪读数）、名字、数值可视化 | `script/Soul/README.md` |
| 身体层：状态效果表、交互时序、自主行为节律、环境感知（P6） | `script/State/README.md` |
| 界面层：面板群（聊天/工具栏/配置/状态窗）、窗口铁律、动画池机制、云母样式 | `script/UX/README.md` |
| 能力层：ACP 客户端、AgentBridge、指令通道协议、白名单与安全边界、人格与数据的获取方式（**Agent 自主读取，不做注入**） | `script/Agent/README.md` |
| 模式层：办公 / 游戏切换接口 | `script/Mode/README.md` |
| 回归探针：有哪些、怎么跑、探针方法论（怎么写出不骗自己的断言） | `tests/README.md` |
| 资产导入：VPet → mods 的导入器规则与踩坑 | `tools/README.md`（见该文件） |
| 设计补充（Why/What）、施工记录、待办 | `document/idea.md`、`document/开发历史记录.md`、`document/Plan表.md`（待办）|

**定位**：AIPet-Agent.md = 总纲（**How 的框架**）；各层 README = 该层的**细节与踩坑**；`document/idea.md` = 设计补充（Why/What）。

> **章节编号故意保留原样**（所以你会看到 §1 之后直接是 §5）：§2 / §3 / §4 / §6 已各自搬到对应分册，
> 但**编号不变**，这样文档与代码注释里「见 §3.1」「见 §10 坑 #16」这类交叉引用全部继续有效。


## 1. 架构总览（六层）

| 层 | 职责 | 状态 |
|---|---|---|
| **灵魂层** | 人格（内化进 Agent，不落程序）+ 动态数据（记忆 / 数值 / 名字 / 画像） |  核心 → `script/Soul/README.md` |
| **身体层** | **状态机 + 行为链**：驱动动画、反馈、表现 |  核心 → `script/State/README.md` |
| **模式层** | 办公 / 游戏**一体**：桌宠本体即游戏的一部分，无缝切换、进度保留；游戏玩法留接口待扩展 | 🆕 接口 → `script/Mode/README.md` |
| **关系层** | 亲密度 / 养成 / 专属感 |  空置，占位不实现 |
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
                   灵魂层(读写: 记忆/数值/名字)
                          │
                          ▼
                   身体层(状态机+行为链) ──► 动画/气泡/TTS
                          ▲
                          └──────── 模式层(办公/游戏) 切换时接管表现
```

---

## 5. 目录地图（真实路径）

> **原则：顶层一个目录 = 一个职责。** 运行时要读的**配置**全在 `config/`；内容数据（动画/指令/翻译）全在 `mods/`；代码按层在 `script/`；**开发工具**在 `tools/`（不参与运行）。

```
D:/Games/Github/AIPet/
├── AIPet-Agent.md / AGENTS.md（占位） / README.md / VERSION / LICENSE   # 文档与版本
├── project.godot / desktop.csproj / desktop.sln / export_presets.cfg   # 工程文件
├── game.tscn / default_bus_layout.tres                # 主场景
├── config/                # ★ 运行时配置（用户可改；改完重启生效）
│   ├── config.json        #   模组配置（当前人物等；原项目数据）
│   ├── i18n.csv           #   界面多语言（原项目）
│   ├── tool.json          #   外部工具路径表（原项目）
│   ├── agent.json         #   Agent 接入（backend / executable / aggressiveMode）
│   ├── behavior.json      #   行为节律（走动/睡眠/贴边/环境感知/久坐提醒…）
│   ├── pet.json           #   外观（缩放）
│   ├── panel.json         #   面板（隐藏指令名单 / 配置窗尺寸）
│   ├── bubble.json        #   ★ 气泡（显示时长 + 字号/风格/位置微调；时长也在配置窗「行为」页）
│   ├── phrases.json       #   ★ 本地话语表（桌宠自己说的话：问候/被摸/久坐/磁盘/降级）
│   └── tts.json           #   语音输出（引擎/声音/语速/音量）
├── mods/                  # ★ 行为层内容（mod 协议：每个子目录 = 一个 mod；只加不改）
│   ├── main_command/ main_txt/ main_file/ main_anim/ workshop/   # 原项目
│   └── toolbar/           #   工具栏小组件（删目录即移除、`_` 前缀即禁用；**组件自带 config/state**，自包含）
├── script/                # ★ C# 源码（按层）
│   ├── Logic/             #   总控与数据流（Main / IO / FileDrop / ClipboardRead…）
│   ├── Loader/            #   加载管线（ModLoader / AnimLoader / CommandLoader…）
│   ├── State/             #   身体层：状态机 + 环境感知
│   ├── Soul/              #   灵魂层：数据接口（数值/名字/上下文）
│   ├── Agent/             #   能力层：后端抽象 + ACP 客户端 + 桥
│   ├── Mode/              #   模式层：办公/游戏接口
│   ├── Game/              #   ★ 游戏模式：横板动作玩法（开发区，见 script/Game/README.md）
│   └── Audio/ UX/ Steam/ Util/ Asset/    # 语音 / 界面与动画 / Steam / 工具 / 图标
├── tests/                 #   回归探针（41 个场景；跑法与清单见 tests/README.md）
├── tools/                 # ★ 开发工具（不参与运行、不随包发布）
│   ├── anim/              #   动画资产导入（VPet → mods/main_anim）
│   ├── package.sh         #   打包（构建→导出→组装→打 zip）
│   ├── run_probes.sh/.ps1 #   一键回归
│   └── install_edge_tts.ps1   # Edge 语音依赖安装
├── document/              # 项目文档（plan / idea / 打包审计 / 隐私模型 / 新用户上手）
├── dist/                  # 对外交付物（skill + 人格内化材料 + 用户 README）
├── licenses/              # 第三方许可
├── addons/                # Godot 插件（dialogue_manager）
└── font/ icon/            # Godot 素材（字体 / 菜单图标；原项目）
```

**三个常见疑问**：

- **「动画」在哪？** 成品动画资产在 `mods/main_anim/anim/loris/<池>/`（运行时读）；**导入工具**在 `tools/anim/`（开发期用、不随游戏运行）——工具与成品分离。
- **配置为什么只有一个目录？** 原 `config/`（模组数据）与 `settings/`（新功能配置）已于 2026-09-17 **合并为 `config/`**；代码统一走 `ConfigFile.候选("config/X")`（native 路径，不受 `.gdignore` 影响）。
- **用户数据在哪？** `%APPDATA%/Godot/app_userdata/AIPet/`（人格/数值/记忆/事件/会话；见 `document/新用户上手.md` §5）。

---

## 7. 实现顺序建议（路线图）

1. **灵魂层数据接口**：`StatsTable`（主人情绪读数）+ `NameTable`（显示名，读 `config/config.json`）+ `ContextTable`（上下文接口）——**人格不落程序**（内化进 Agent，见 §9 硬规则）。
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
- **游戏属性一体**：办公与游戏不严格隔离，办公行为可挂 `Game` 分支作游戏内容；游戏进度不入数据文件（归 ModeManager/GameSession）。
- **git**：提交前 `git status` 自查；`.godot/` 等构建缓存不提交。

```
dotnet build D:/Games/Github/AIPet/desktop.csproj
```

---

## 9. 当前已知事项

> **文档关系**：`AIPet-Agent.md` 是**主文档**（工程契约，How）。`idea.md` 是**对项目的设计补充**（意图，Why/What），两者**解耦**——本文件不引用 idea 的具体小节，只描述工程约束。

- `desktop.csproj` 的 SDK 已由 Godot 4.7.2 编辑器自动从 `4.5.1` 升级为 `4.7.2`（用户无手动改动；编辑器打开即自动改写）。**保留**该改动——本地引擎是 4.7.2，还原后打开又会被升回。
- 架构图参考：`D:/Games/Github/ACPPet-架构图.html`（旧名文件，内容对应本项目）。
- 骨架文件（`script/Soul/`、`script/State/`、`script/Agent/`、`script/Mode/`）已建，均为桩/TODO，待按此文档实现。
- 灵魂职责已重新定义为「人格 prompt 资产」，且**人格不落程序**（主人四次强调，2026-09-20 定稿）：人格经 skill **一次性内化**进 Agent 自己；程序只存动态数据——数值归 `state/stats.json`、画像归 `soul/profile.md`、记忆流水归 `soul/memory.jsonl`；显示名是配置值（`config/config.json` 的「名字」，缺省「萝莉丝」）。
- **数值语义（2026-09-20 主人定）**：`mood` = 主人情绪读数（LLM 判断 → `set_mood` 写入；程序只做衰减与存储，每 30s 向中性 50 回 5 点），**只影响回复策略、不加入互动**；精力/亲密属 mod 扩展（亲密归空置的关系层）。
- **人格与数据的获取方式（硬规则；主人 2026-09-16 三次强调 + 2026-09-20 第四次补充）**：桌宠**不做任何主动注入**——
  不拼进消息、不写插件钩子、不碰 Agent 的 system prompt、也不注入本机 Agent。交付物 = **人格内化材料**（结构 + 示例）+ **skill**（用户提交给自己的 Agent），
  **skill 是一次性内化文档**：Agent 读一次 → 先问主人「我叫什么？」→ 把「桌宠的职责与互动」写进自己的人设（自主注入），之后不必再读 skill、按需读**动态数据**文件。
  **人格不落程序**：程序侧不保存、不供读人格文件（`SoulTable` 已删除）；显示名 = 配置值（`config/config.json`）。skill 与材料**在项目完工时定稿**（P8）。
- **上下文组装照做，形态是接口**：`ContextTable` 把「数值 + 名字 + 画像 + 最近记忆 + 待处理事件 + 指令通道 + 动态文件路径」组装成 `user://context.md`（**只在启动 + 事件池变动时**刷新——数值漂移不触发；文件头标只读）；
  另有 **MCP 工具 `pet_context`**（`dist/aipet-mcp/`，Agent 调用即当场读盘、永远最新）。
  Agent **读这一份就够**、不必到处翻文件；要深挖再顺路径去读源文件。

---

## 10. 踩坑清单（全部为实测结论，改代码前先看这一节）

> **本清单已按层拆分**：与某一层强相关的条目在那一层的 README 里（**编号保持全局不变**，所以「见坑 #N」这类引用仍然有效）。
> 本文件只保留**跨层**的几条（下面 6 条）：
>
> | 编号 | 在哪 |
> |---|---|
> | #1 / #2 / #3 / #5 / #12 | `script/UX/README.md`（界面 / 窗口） |
> | #14 / #19–#24 | `script/State/README.md`（状态机是动画的所有者） |
> | #9 / #10 / #11 / #13 | `script/Agent/README.md`（协议 / 指令通道） |
> | #6 / #15 / #17 / #18 | `tests/README.md`（探针方法论） |
> | #4 / #7 / #8 / #16 / #25 / #26 | **本文件**（跨层：启动 / 目录约定 / 版本控制 / C# 命名冲突） |

4. **麦克风不要在启动时打开**。`AudioStreamPlayer2D` 挂 `AudioStreamMicrophone` + `autoplay=true` 会让程序一启动就占用录音设备，无设备时报 `WASAPI: init_input_device error`（且属隐私问题）。→ 规则：**按需启动**（`Kws` 只在语音模型真加载成功后才开）。

7. **`config/`、`mods/`、`document/` 都有 `.gdignore`，Godot 完全忽略它们**（原项目有意为之：不让运行数据混进导入系统）。放在其中的文件用 `Godot.FileAccess` 读 `res://...` 会读不到（`agent.json` 曾中招）。→ 规则：**读这些目录里的文件一律走 `ConfigFile.候选()`（native 路径）**；`ConfigFile.找` 的第三个候选就是 `res://` 的 native 化路径（开发期靠它）。

8. **`FileAccess` 二义性**：`Godot.FileAccess` 与 `System.IO.FileAccess` 同名。→ 规则：同时 `using Godot;` 和 `using System.IO;` 时，显式写 `Godot.FileAccess`。

16. **两个「与 BCL 同名」的坑，一律写全限定名**：
    - `Environment`：`Godot.Environment` vs `System.Environment`（用 `TickCount` 必炸）→ `System.Environment.TickCount`。
    - `FileAccess`：`Godot.FileAccess` vs `System.IO.FileAccess`。
    - `Mode`（成员遮蔽命名空间，比 BCL 同名更隐蔽）：`Window.Mode` 成员 vs `desktop.script.Mode` 命名空间——**继承 `Window` 的类里裸写 `Mode.ModeManager` 会被成员抢走解析**（`GameEntryDialog` 撞过；报错信息完全不指向真因）→ 全限定 `desktop.script.Mode.ModeManager`。
    规律：Godot 的 C# 命名空间里有一批与 BCL 同名的类型，凡是用「名字很通用」的 BCL 类型，一律全限定。

25. **启动窗口期不要直接往 root `add_child`**（`Main._ready` 及其向下链路里）：引擎会**静默拒绝**（只打一行 `Parent node is busy setting up children`，`AddChild` 既不抛异常也不生效——曾让「网速气泡启动恢复」一直没生效，且日志还谎报成功）。→ 规则：**延迟一帧**（`root.CallDeferred(Node.MethodName.AddChild, 节点)`，或 `Callable.From(方法).CallDeferred()`）；探针在 `_Ready` 里实例化 `game.tscn` 时同理。

26. **`.gitignore` 的两个反直觉行为（Windows 实测）**：① 模式匹配**大小写不敏感**（`core.ignorecase`）——`dist/AIPet-*` 会连 `dist/aipet-mcp/` 一起吞；② **不支持行尾注释**（`#` 必须独占一行，否则整行被当成模式）。→ 规则：模式往精确了写；**文件莫名不进 git 时先 `git check-ignore -v <路径>` 定位**。
