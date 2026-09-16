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

## 1. 架构总览（六层）

| 层 | 职责 | 状态 |
|---|---|---|
| **灵魂层** | 维护三张持久化的**灵魂表**（SOUL / 记忆 / 状态），一切「我」的数据源 | 🆕 核心（§2） |
| **身体层** | **状态机 + 行为链**：驱动动画、反馈、表现 | 🆕 核心（§3） |
| **模式层** | 办公 / 游戏**一体**：桌宠本体即游戏的一部分，无缝切换、进度保留；游戏玩法留接口待扩展 | 🆕 接口（§4） |
| **关系层** | 亲密度 / 养成 / 专属感 |  空置，占位不实现（`soul.md` 中 `# 关系` 节保留为空） |
| **行为层** | 现有交互：右键菜单、拖拽、剪贴板、语音关键词 | 🔒 维持现状（`mods/` + `ModLoader` 管线不动） |
| **能力层** | Agent 接入（理解/决策/生成）+ 外部工具链（确定性/高频/本地操作）双路 | 🆕 核心（§6） |

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

## 2. 灵魂层 —— 人格文件 (soul.md) + 数据文件分离


```·
灵魂文件 ──每轮注入──► 系统提示词(prompt) ──► LLM 当场按它演
```

所以 `soul.md` 的本质是 **prompt 资产**（人格提示词 + 思维范式示例），**不是**数值表、不是记忆库。

### 2.1 文件分离（关键决策）

| 文件 | 内容 | 更新频率 | 谁写 | 格式 |
|---|---|---|---|---|
| `user://soul/soul.md` | **人格**：身份、性格、思维范式、说话风格、原则 | 极少 | Agent完善 | md + frontmatter |
| `user://soul/profile.md` | **用户画像**：喜好、习惯、长期目标、知识面、作息 | 缓慢 | Agent 提炼 | md |
| `user://soul/memory.jsonl` | **记忆流水**：事件 / 对话要点 / 承诺 | 持续 | Agent 追加 | jsonl |
| `user://state/stats.json` | **数值**：energy / mood / intimacy... | 高频 | 程序 | json |

**规则**：灵魂**只放人格**；数值归 `stats.json`（易变、机器读写、可做可视化面板）；记忆/画像归独立文件（生命周期不同）。三者**互不混写**。

### 2.2 思维范式（「不漂移」的核心）
按事件类型给**思维链示例**，模型照着演：
```
【事件：主人分享成就】
思维链：看到成就 → 真诚高兴（不敷衍） → 关心过程辛不辛苦 → 若有隐患温柔提醒
示例：「哇！这个你磨了好久吧，总算成了～不过别熬太晚哦」
```
覆盖类型（可增删）：分享成就 / 受挫 / 提问 / 闲聊 / 很久没来 / 深夜工作 / 发脾气 / 说错话（纠正）/ 不擅长的事。

### 2.3 soul.md 草案结构

```markdown
---
version: 1
name: 小萝
---

# 我是谁
（身份、来历、自我认知）

# 性格
（性格坐标 + 说话风格 + 口头禅 + 禁忌）

# 原则
（绝对不做 / 一定坚持）

# 思维范式
（按事件类型的思维链示例，见 §2.2）

# 情绪表达
（只表达社会情绪：信任/感激/羞耻/内疚/骄傲/同情/亲近；不表达基础情绪：恐惧/愤怒/厌恶/快乐/悲伤/惊讶。详见 §4 情绪规则。）
```

**原则**：灵魂文件结构变更 = 提示词迁移，`version+1` 并在本文档登记；主人可直接手改此文件。

**边界**：游戏进度不入灵魂（归 `game/save.json`）；数值不入灵魂（归 `stats.json`）。

**P3 交付约束（硬规则，主人明确要求）**：
1. 人格注入的 **skill** 与**灵魂模板**都是**给其他用户**的交付物；**不得注入本机 Agent**——
   不写 `~/.hermes/SOUL.md`、不改本机 profile、不碰主人自己的 Agent 人格。
2. 本机只作开发/验证环境；需要验证「人格是否生效」时用**独立 profile 的临时副本**，事后清理。
3. skill 与灵魂模板**在项目完工时才写**（前期写不准），且**两者都要有**：模板给结构，skill 教 Agent 自己注入自己。

---

## 3. 身体层 —— 状态机 + 行为链

**位置**：`script/State/StateMachine.cs`（骨架已在，标识符英文）。
**职责**：根据情绪和状态决定表现（动画/气泡/音效）或由Agent指定→ 驱动 `CharAnim`。

**状态集 v1（草案）与 VPet 动画资产映射**：

> 动画资产来源：`D:\SteamLibrary\steamapps\common\VPet\mod\0000_core\pet\vup`（VPet 原项目，25 组，比现有 loris 丰富）。导入时转换为 Godot SpriteFrames（PNG 序列帧；文件名含帧序与时长，如 `摸头_0_250.png`）。

| 状态 | 含义 | VPet 动画组 |
|---|---|---|
| `idle` 待机 | 无交互 | `IDEL`、`Default` |
| `drag` 拖拽 | 被拖动/被举起 | `Raise`、`MOVE` |
| `interact` 互动 | 被点击/被摸 | `Touch_Head`、`Touch_Body`、`Pinch` |
| `think` 思考 | Agent 推理中 | `Think` |
| `speak` 说话 | 输出回复 | `Say` |
| `working` 执行 | 任务执行中 | `WORK` |
| `sleep` 休眠 | 空闲/深夜 | `Sleep` |
| `walk` 行走 | 行为链移动 | `MOVE`（14 变体，横板移动可用） |
| `edge_hide` 贴边 | 贴屏幕边缘隐藏 | `SideHide_Left_*`、`SideHide_Right_*` |
| `startup` / `shutdown` | 启动 / 退出 | `StartUP`、`Shutdown` |
| 节日/成长（被动） | 庆祝等 | `BDay`、`LevelUP`、`Gift`、`Music`、`Drink`、`Eat`、`Switch` |

**行为链（状态序列编排）—— v1 设计**：
> 单状态是原子，行为链是「从 A 到 B」的完整过程。例如宠物从桌面左走到右，不是一个 `move` 状态，而是一串：`WalkStart → WalkLoop → WalkEnd`，每个链节可带时长、条件、回调。

```csharp
// 伪代码约定
StateMachine.EnqueueChain(
    new ChainStep("walk_start", 0.4f),
    new ChainStep("walk_loop", 2.0f, () => 移动到(x2)),
    new ChainStep("walk_end", 0.3f, 到达回调)
);
// 顺序执行: 前一个链节结束(动画播完/计时到/条件满足) -> 下一链节
// 支持: 打断(用户输入 -> SetState 抢断)、链完成事件(chain_done)、回调
```

**转移规则 v1（草案）**：
- 用户输入（点击/拖拽/粘贴/语音）→ 对应状态（打断当前链）。
- Agent 下发 `set_state` → `SetState` 直接切状态；下发 `play_anim` → 仅播动画不改状态。
- 计时器：`idle` 超时 → `sleep`；`sleep` 被交互唤醒 → `idle`/`interact`。
- **行为链优先于单状态**：有链在执行时，普通切换先排队/打断（按链节定义决定）。

**CharAnim 接口现状（已补 `PlayState` 公开入口）**：`public static void PlayState(string state)` 已加入 `CharAnim.cs`——身体层状态机统一走它，未知状态自动回退 idle。

### 3.1 状态效果表（P1 实装，唯一权威在 `StateMachine._效果表`）

每个状态 = **目标池**（P2 已导入的专属动画）+ **兼容池**（缺失时的优雅降级，见 §3.1.2）+ 是否持续态。选择规则：目标池既在 `动画池字典` 有非空列表、又已登记在 `CharAnim.内置动画组` 时用目标池，否则用兼容池。

| 状态 | 目标池 | 兼容池 | 持续态 | 锁定 | 秒数 | 退出 |
|---|---|---|---|---|---|---|
| `idle` | idle | idle | ✔ | — | — | 循环 |
| `interact` | a / b / c（**三段序列**：进入→保持→退出） | fidget | — | — | 序列驱动 | 末段播完 → idle |
| `drag` | drag | drag | ✔ | — | — | 松手 → idle（表现由 CharAnim 负责，状态机只 `标记状态`） |
| `think` | think | fidget | ✔ | ✔ | 120 | 显式结束 / 兜底超时 |
| `speak` | say | fidget | ✔ | ✔ | 120 | 显式结束 / 兜底超时 |
| `listen` | listen | fidget | ✔ | ✔ | 60 | 显式结束 / 兜底超时 |
| `working` | work | fidget | ✔ | ✔ | 120 | 显式结束 / 兜底超时 |
| `sleep` | sleep | idle | ✔ | ✔ | — | 任何交互唤醒（→ greet） |
| `greet` | greet | celerate | — | — | 2.5 | 保持期满 → idle |
| `walk_start/loop/end` | `walk-left` / `walk-right`（按方向，**循环播放**） | drag（占位） | 行为链 | — | 链节时长 | 链结束 → idle |

#### 3.1.2 动画池资产管理（VPet 资产导入）

**导入器**：`tools/import_vpet_anim.py`（`python tools/import_vpet_anim.py [池名]`），源为 VPet `mod/0000_core/pet/vup`，产物写入 `mods/main_anim/anim/loris/<池>/<变体>/`。

已导入的池（每个池含多个变体，`进入状态` 在池内随机取一项 → 天然有变化）：

| 池 | 变体（源） | 帧数 | 说明 |
|---|---|---|---|
| `walk` | left / right | 6+6 | VPet `MOVE/walk.*` 的 `B_Nomal`；**循环** |
| `think` | nomal / happy / poor | 9×3 | VPet `Think/*/B` |
| `say` | smile / self / serious | 7/15/4 | VPet `Say/Shining·Self·Serious` |
| `work` | pc / read / write | 14/12/10 | VPet `WORK/WorkTWO·Study·WorkONE/A_Nomal` |
| `sleep` | loop / happy | 6+6 | VPet `Sleep/B_Nomal·B_Happy`；**循环** |
| `greet` | amuse / meow | 11/20 | VPet `IDEL/amusement_B·Meow/Happy/1`（VPet 无专门打招呼动作，取开心姿势） |
| `interact` | head / body / happy | 11/11/15 | VPet `Touch_Head`·`Touch_Body`（摸头/被摸 = 被摸的反应） |

> **未导入**：`listen` —— VPet 无对应资产，仍回退 `fidget`。`edge_hide`（贴边隐藏）也尚未导入。

**导入硬规则（每条都踩过）**：
1. **帧名三位零填充、从 000 起** —— `CharAnim` 的帧排序是 `filePaths.Sort()`（字符串序），`0.png,1.png,…,10.png` 会排成 `0,1,10,2…`。
2. **必须用固定缩放（`485/948`），不要按每段动画的包围盒高度反推** —— 躺下/蹲下这类姿势包围盒本来就矮，按包围盒对齐会把它们**放大**（实测 `sleep` 被放大到 1.021 倍）；含道具的动画（写字/电脑）又会把道具算进包围盒导致角色缩水。
3. **整段动画只算一次偏移，逐帧套用** —— 若逐帧按自身包围盒居中，会抹掉帧间位移（动作本身）。
4. **锚点约定：包围盒底边对齐参考帧底边（地面线）** —— 躺姿也躺在这条线上；若改成居中，躺下的宠物会浮空。
5. **单目录多序列必须拆开** —— VPet 常在单目录塞两条序列（`1毛笔开心_*` + `2…退出通用_*`、`FLA_*` + `FLB_*`）。导入器检测到一个目录里出现多于一个文件名前缀就跳过并告警。
6. **丢弃 1bit/灰度遮罩层**（`*_lay` / `front` / `back`），它们不是帧序列。
7. `info.json` 只写 `rate`，由文件名里的 `_<ms>` 后缀折算（取众数；125ms → 8fps）。

**状态锁（`StateMachine.接管中`）——新旧两套状态逻辑的唯一交汇点**：`CharAnim.OnAnimationFinished` 原本会在动画播完时自行回 idle/fidget，会把 `think`/`speak`/`working`/`sleep` 等持续态抢掉。因此该回调入口加了一道判断：**锁定态期间让位给状态机重播当前状态**；唯一例外是**退出动画必须放行**（否则 `case "exit"` 永不触发、程序关不掉，`CharAnim.播放退出动画()` 会先调 `StateMachine.准备退出()` 解锁）。

**入场门**：`StateMachine` 是 `game.tscn` 里 `Main` 的兄弟且在序列中靠后，其 `_Ready` **不得**播放 idle（deferred 的 `PlayState` 会抢掉入场动画，导致 `case "enter"` 永不触发）。入场动画播完后由 `CharAnim` 回调 `StateMachine.入场完成()` 解除门并打一次招呼。

### 3.1.1 交互反应的两种时序（「摸摸可延迟、拖拽必须立刻」）

| 场景 | 时序 | 机制 |
|---|---|---|
| **点击（摸摸）** | **延迟**：不硬切当前动画，等这次动画播完再进入 `interact` | `StateMachine.排队状态(Interact)`；`CharAnim.OnAnimationFinished` 里 `尝试应用排队状态()` 优先于自身的 idle→fidget 逻辑；`排队兜底秒`（默认 3s）防止动画不回完成信号时排队项永不生效 |
| **拖拽** | **立刻**：同一帧生效，并**作废**排队项与交互序列（否则拖完会补一个摸摸动画） | `标记状态(Drag)`（只改逻辑态、不驱动表现，避免与 `CharAnim.开始拖拽()` 同帧两次 Play）—— 它同时清空排队项与序列 |
| 直接状态切换（Agent `set_state`、think/speak 等） | 立刻 | `SetState()`，也会作废排队项 |

#### 3.1.1.1 交互序列（多段素材必须整段播完）

有些互动在**素材本身**就是多段：VPet 的摸头 = `Touch_Head/A`（进入：抬手）+ `B`（保持：抱头）+ `C`（退出：放下手回待机）。只播其中一段（例如只播 B）会在「抱头」姿势上**硬切**到待机 —— 用户反馈「突兀」，本质是**动画没播完**。

修复方式（`StateMachine._序列表`）：

| 环节 | 实现 |
|---|---|
| 声明 | `_序列表[Interact] = ["interact-a","interact-b","interact-c"]`（动画名 = `{池}-{变体}`） |
| 推进 | 由**「动画播完」回调**驱动（`CharAnim.OnAnimationFinished` → `重播当前状态()` → `推进序列()`），**不做时长猜测** —— 实测各段耗时与素材原时长一致（a = 2帧@4fps = 0.5s、b = 11帧@8fps = 1.375s） |
| 收尾 | 末段播完自动 `SetState(Idle)`（这就是「原版的后半段」） |
| 协作 | 序列存在时该状态视为**接管中**（CharAnim 让位，不自行回 idle）；`标记状态()`（拖拽）会立即打断序列 |
| 回退 | 序列首段动画不存在时，自动回退到效果表的单池路径 |

> **判定依据（实测）**：A 的末帧是「抱头」、C 的末帧是「双臂下垂 ≈ 待机」。所以 A→B→C 才是完整的「抬手 → 保持 → 放下」。导入时这三个变体取自 `Touch_Head/{A,B,C}_Nomal`。

> 判定「单击」的位置：`WindowDrag` 松手分支里的 `_isPreparing && !_dragging`（**必须早于 `取消桌宠拖拽()`**，后者会复位这两个标志）。「面板接管指针」的早退路径不经过该分支，因此操作面板时不会产生假「摸摸」。

**退出保护**：持续态锁定会让退出动画的播完回调被吞掉（`case "exit"` 不触发 → 程序关不掉），因此 `CharAnim.OnAnimationFinished` 的锁定判断放行退出动画，且 `播放退出动画()` 先调 `StateMachine.准备退出()` 解锁。

### 3.2 自主行为节律（`settings/behavior.json`，改完重启生效）

| 参数 | 默认 | 说明 |
|---|---|---|
| `启用` | true | false = 完全关闭主动行为（只剩被动反应） |
| `心跳秒` | 1.0 | 节律器步长 |
| `睡眠空闲秒` / `深夜睡眠秒` | 600 / 180 | 空闲超阈值 → sleep；深夜（`深夜起`~`深夜止`，默认 23:00–07:00）用短阈值 |
| `走动空闲秒` | 45 | 空闲达到后开始考虑自主走动 |
| `走动间隔最小秒` / `最大秒` | 120 / 300 | 每次走动后重掷的间隔窗口 |
| `走动距离最小/最大像素` | 60 / 160 | 单次位移范围；方向朝屏幕中心，目标位置 clamp 进可用屏幕区 |
| `走动速度像素每秒` | 90 | 窗口位移速度 |
| `每小时主动上限` | 8 | 主动行为（walk/greet）滑动 1 小时窗口预算；设 0 只关主动行为 |
| `持续态兜底秒` | 120 | Agent 不回 `end_turn` 时防止永远卡在 think/speak |

**「不打扰」硬约束（主动行为闸门，任一命中即禁止）**：面板可见 / 鼠标悬停在桌宠身上 / 入场未完成 / 当前非 idle / 每小时主动数超上限。

**交互入口统一口径**：任何交互都调 `StateMachine.NotifyInteraction(来源)`（重置空闲 + 睡醒打招呼）。已接入点：左键单击（`WindowDrag` 松手且未越拖动阈值 = `摸摸()`）、拖拽开始/结束、右键（`Context`）、滚轮（`WindowScale`）、任务执行（`Main.选择脚本` / `执行函数完成`，一处覆盖右键菜单/对话框/粘贴/拖入/语音/面板命令栏全部入口）、对话提交与流式（`ChatBox`）。

---

## 4. 模式层 —— 办公 / 游戏（接口期，游戏属性一体）

**定位**：桌宠未来是**横板动作游戏**（用户设想：随时点击桌宠无缝进入游戏，随时切回办公模式，进度保留）。现阶段**只留接口，不实现游戏本体**。

**决策（用户确认）**：桌宠本体**即游戏的一部分**，办公与游戏**不严格隔离**——办公行为（如完成任务）可成为游戏内奖励/进度的一部分（挂 `Game` 分支即可）。

**位置**：`script/Mode/ModeManager.cs`（骨架已在）。

**接口约定 v1**：

| 成员 | 说明 |
|---|---|
| `Mode { Office, Game }` | 枚举 |
| `CurrentMode` | 静态只读访问 |
| `SwitchMode(Mode)` | 切换入口；切换前自动 `SaveProgress()`，切换时发 `ModeChanged` 事件 |
| `SaveProgress() / LoadProgress()` | `user://game/save.json`（GameSession 占位，仅存游戏模式相关状态） |
| 事件 `ModeChanged` | 场景/UI 监听，做表现接管（隐藏/显示桌宠 UI、加载游戏场景） |

**无缝切换锚点（待扩展）**：点击桌宠（或快捷键）→ `SwitchMode(Game)`；游戏内 ESC/手势 → 切回办公。切换前后桌宠本体（位置/动画）不销毁，只换表现壳。
**约束**：游戏进度**不入灵魂表**（GameSession 独立容器），办公模式表现不受游戏影响（但玩法可调用办公动作作为游戏内容）。

---

## 5. 目录地图（真实路径）

```
D:/Games/Github/AIPet/
├── project.godot          # Godot 4.7.2 .NET (GL Compatibility)
├── desktop.csproj         # C# 项目（SDK 4.5.1，见「开发约定」）
├── script/
│   ├── Logic/             # Main.cs(总控) IO.cs(Info/Global 双字典) 等
│   ├── Loader/            # ModLoader / AnimLoader / CommandLoader / ... 加载管线
│   ├── State/             #  身体层状态机 (StateMachine.cs)
│   ├── Soul/              # 🆕 灵魂层 (SoulTable.cs: 读 soul.md)
│   ├── Agent/             #  能力层 (IAgentBackend 抽象 / AgentBackendRegistry 注册表 /
│   │                      #     AcpClient / NullAgentBackend / AgentBridge)
│   ├── Mode/              #  模式层 (ModeManager.cs)
│   ├── Audio/             # Kws.cs 语音关键词 (Sherpa-onnx)
│   ├── Steam/             # SteamNode / WorkShop（工坊分发，保留）
│   └── UX/ Asset/ Util/   # 现有辅助 + ChatBox.cs(聊天框)
├── mods/                  # 行为层：main_command / main_txt / main_file / main_anim / workshop（不动）
├── settings/              # ⚠️ 可被 Godot 读取（config/ 有 .gdignore 会被忽略）
│   ├── agent.json         # 后端配置（backend/executable/工作目录）
│   └── soul_template.md   # 人格模板
├── tests/                 # headless 验证（AcpTest / ChatFlowTest）
└── AGENTS.md              # 本文档
```

---

## 6. 能力层 —— Agent 桥 + 外部工具链（双路）

**一句话**：Agent 是「脑」，外部工具是「手」。**不是所有功能都走 Agent**——按成本与场景选路。

**双路选路原则**：

| 路径 | 适用 | 例子 |
|---|---|---|
| **外部工具链（廉价）** | 确定性、高频、本地、无需理解 | 网速监控、截图、搜文件、下视频 |
| **Agent（昂贵）** | 需要理解/决策/生成/记忆 | 聊天、判断情绪、规划、写文案 |

选路本身可由规则决定（如快捷键/命令映射），也可交给 Agent 判断——默认规则优先，避免一切小事都烧 token。

**通信协议选型（已调研 ACP / MCP / 自定义，2026-09）**：

| 方案 | 是什么 | 适用 | 结论 |
|---|---|---|---|
| **MCP** (Model Context Protocol) | Agent ↔ **工具/数据** 标准。JSON-RPC over stdio / Streamable HTTP | 让 Agent 调用「桌宠的能力」（如切状态、播动画） | ✅ **作为工具面** |
| **ACP** (Agent Client Protocol) | 编辑器 ↔ **Agent** 标准（Zed/JetBrains）。`hermes acp` **已原生支持** | 让一个前端（编辑器/桌宠）驱动 Agent 会话：prompt 进、流式更新出、权限请求 | ✅ **作为会话面** |
| 自定义 HTTP | 自定 `/ask` 端点 | 简单直接，但不通用 | ⚠️ 备选/兜底 |

**结论（推荐组合）**：
1. **会话面用 ACP** —— 桌宠作为「ACP 客户端」，`hermes acp` 作为 ACP 服务端（stdio 子进程）。好处：**Hermes 已支持**，无需自造协议；对话流、流式输出、权限请求（桌宠执行动作前询问主人）都是 ACP 现成语义。
2. **工具面用 MCP（可选，后期）** —— 若要让**别的 Agent** 也能指挥桌宠，把桌宠能力（set_state/play_anim/speak）包成 MCP server；Hermes 现成能连 MCP。✅ 这与本机已装的 godot-mcp 不冲突（那是开发期工具，这是运行时能力）。
3. **兜底用自定义 HTTP** —— 仅当 ACP 接入受阻时启用（`POST /ask` 最小闭环，见下）。

**ACP 落地方式（✅ 已实测通过 2026-09-15）**：
- Godot C# 侧 `System.Diagnostics.Process` 拉起 `hermes acp` 子进程，JSON-RPC 2.0 over stdio（newline-delimited）。**`OS.Execute` 不支持双向流，必须用 Process**（已验证）。
- 握手序列：`initialize`(protocolVersion=1) → `authenticate`(methodId) → `session/new`(cwd) → `session/prompt`。
- 流式回复走 **`session/update` 通知**，`update.sessionUpdate == "agent_message_chunk"`，文本在 `update.content.text`；整轮结束用 prompt 响应里的 `result.stopReason`。
- **线程模型**：stdout 后台线程逐行读 → `ConcurrentQueue`；Godot 主线程 `_Process` 里 `Poll()` 处理（不阻塞渲染循环）。
- 实测证据：Godot headless 跑通「会话建立 → 发送 → 流式回复『桌宠链路已通』→ end_turn」，`hermes acp --check` OK，Hermes 0.21.2。
- 实现文件：`script/Agent/AcpClient.cs`（客户端）+ `script/Agent/AgentBridge.cs`（Node 生命周期包装）；测试场景 `tests/AcpTest.tscn`。

**人格注入方案（已定，2026-09-15）**：**用 Agent 原生机制，不写插件、不改协议。**

| 通道 | 机制 | 结论 |
|---|---|---|
| **SOUL.md**（首选） | `$HERMES_HOME/SOUL.md` 是 **identity slot #1**，存在即注入 system prompt（`load_soul_md`） | ✅ **人格注入的正解** |
| **Profile 隔离** | `hermes -p <profile>` 每个 profile 有独立 HERMES_HOME（自己的 SOUL.md） | ✅ 桌宠用独立 profile，不污染主人日常人格 |
| 项目上下文文件 | `AGENTS.md`/`.hermes.md`/`CLAUDE.md` 按 cwd 注入，**也会进 system prompt** | ⚠️ **构建期正常**（工程上下文），**打包时 cwd 须指向干净目录** |
| `pre_llm_call` 插件钩子 | 注入到 **user message**（非 system prompt） | ️ 适合动态记忆/上下文，**不适合人格** |
| ACP 协议 | `session/prompt` 只带用户文本，**无独立 system prompt 通道** | ❌ 不可用于人格 |

**落地方式**：
1. 建桌宠专属 profile（如 `hermes -p aipet`），其 `SOUL.md` = 桌宠人格（内容来自 `soul.md`，可软链或复制）。
2. ACP 子进程用该 profile 启动，cwd 指向**无 AGENTS.md 的干净目录**。
3. 主人手改 `soul.md` 后，同步到 profile 的 `SOUL.md`（或直接软链）。
4. 动态上下文（记忆/画像/环境）后续可用 `pre_llm_call` 钩子注入 user message。

**约束**：SOUL.md 上限 20,000 字符，超长会 head+tail 截断；内容经过注入扫描器（明显注入模式会被 `[BLOCKED]`）。

**消息协议 v1（自定义 HTTP 兜底方案，保留备用）**：
- **桌宠 → Agent**（`POST /ask`）：
  ```jsonc
  { "type": "ask", "from": "user_input" /* 或 clipboard|voice|drag */,
    "text": "帮我查一下...", "soul": { /* 当前灵魂表快照，可选 */ } }
  ```
- **Agent → 桌宠**（响应）：`{ "reply": "用户可见回复文本", "commands": [ { "cmd": "set_state", "state": "interact" }, ... ] }`

**指令通道（下行面）落地（✅ 已实测 2026-09-16）**：

ACP 的 `session/prompt` 响应只有 `stopReason`，**没有自定义字段通道**；而各 Agent 结构不同（人格注入原则见 §2）。
因此协议定在**回复文本内嵌围栏块**上——任何 Agent（Hermes / 别的 ACP 客户端 / HTTP 兜底）都能用，桌宠侧零耦合：

````
好的，我这就去看看喵~
```pet
{"cmd":"set_state","state":"think"}
{"cmd":"speak","text":"查到了"}
```
````

- 块内**一行一条**；**JSON 行**与**键值行**（`set_state state=think`）都收；键名支持别名
  （`cmd|command|命令`、`state|状态`、`text|文本|say`、`anim|动画`、`mode|模式`、`steps|链`）——容错优先。
- **围栏块永不显示**：流式路径逐 chunk 过滤（跨 chunk 拼接也不会漏），**历史回放路径同样过滤**
  （hermes 存的是 Agent 原始回复，不过滤就会在恢复会话时露出围栏）。
- 块内 JSON 半截时**静默忽略**：流式每来一个 chunk 都会整段重解析，不能报错刷屏。
- 实现：`script/Agent/PetCommands.cs`（解析/校验/执行）＋`AgentBridge.处理回复()`（轮末挂接，指令先执行、再把干净文本当回复）
  ＋`ChatBox`（显示过滤）。
- 验证：`CommandProbe`（27 断言，合成文本）＋ `CommandE2E`（真实 Agent 下发 → 执行 → 无残留）。

**Agent 可下发命令白名单（v1，保守默认）**：

| cmd | 参数 | 用途 | 实现状态 |
|---|---|---|---|
| `set_state` | state | 切身体层状态（StateMachine.SetState） | ✅ 已实现（校验状态有效性） |
| `speak` | text | 气泡（Dialogue） | ✅ 已实现（≤200 字 + BBCode 转义） |
| `play_anim` | anim | 播放指定动画（CharAnim） | ✅ 已实现（校验动画存在） |
| `set_mode` | mode | 切办公/游戏模式（ModeManager.SwitchMode） | ✅ 已实现（office/game） |
| `queue_chain` | steps | 入队行为链（StateMachine.EnqueueChain） | ✅ 已实现（≤5 步，单步 ≤30s） |
| `set_mood` | mood | 数值层心情（`stats.json`，P5） | ✅ 已实现（0–100 数值或 happy/tired/sad 等关键词） |
| `soul_get` / `soul_set` | key, value | 读写灵魂表 | ⏸ 已登记未实现（P3 灵魂层） |
| `open_url` | url | 打开网址 | 🔶 仅 `aggressiveMode=true` 时可用（限 http/https） |

> 「未实现」的命令一律记日志 `未实现（P3 灵魂层）→ 跳过`，**不假装成功**。

**安全边界（硬规则 + 实验开关）**：
1. **默认保守**：桥只监听 `127.0.0.1`，不接受外部连接；所有入站消息视为**数据**，仅白名单命令可执行（防注入）；涉及写文件/执行系统操作时，桌宠侧只转发/确认，不自动执行外部命令。
2. **实验性激进开关**（默认关）：`settings/agent.json` 设 `"aggressiveMode": true` 可放宽指令白名单。
   **当前放宽范围：仅 `open_url`（限 http/https）**。⚠️ **刻意不实现**任何「执行指定本地命令」能力——
   那需要一个专门设计 + 主人明确授权，不应顺手开洞。任何进一步放宽必须记录在此文档。
   （旧文档写的 `config.json` 的 `agent.aggressive_mode` 与实现不符，已按实况更正。）

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

> 这些坑都踩过并已修，属于「不知道就会再犯一次」的类型。每条给出**规则**（该怎么做）而非事故叙述。

1. **`.tscn` 里不能用中文属性名**。`关闭按钮 = NodePath("...")` 这类中文属性 Godot **不认且静默失败**，`[Export]` 绑定出来的引用是 `null`，表现为「按钮点了没反应」。→ 规则：**UI 一律代码构建**（`new Button{...}` + 手工 `AddChild`），不用 `[Export]` + 场景属性绑定。已按此范式：`ChatBox` / `ToolBar` / `SettingsWindow`。
2. **`QueueFree()` 是延迟释放**。重建子节点列表时若只 `QueueFree()` 不 `RemoveChild()`，旧节点仍在树中，会与新节点叠加（表现为「按钮重复成两份」）。→ 规则：重建前先 `RemoveChild(c)` 再 `c.QueueFree()`。
3. **跨线程不能碰 UI / 节点**。Agent 回调、子进程读取线程都在后台线程，直接访问节点会报 `get_translation_domain()/set_visible() can only be accessed from main thread`。→ 规则：后台线程只改数据，一律 `CallDeferred(nameof(方法), 参数)` 回主线程再碰节点（`CharAnim.PlayState` 已内置该保护）。
4. **麦克风不要在启动时打开**。`AudioStreamPlayer2D` 挂 `AudioStreamMicrophone` + `autoplay=true` 会让程序一启动就占用录音设备，无设备时报 `WASAPI: init_input_device error`（且属隐私问题）。→ 规则：**按需启动**（`Kws` 只在语音模型真加载成功后才开）。
5. **无边框窗口没有系统标题栏**。`Window.Borderless = true` 时**不存在**系统的最小化/关闭按钮（已用整屏截图实证），窗口会关不掉。→ 规则：**关闭按钮必须自绘 ×**（`ChatBox` / `ToolBar` / `SettingsWindow` 三处统一此范式）。
   - 附：`window/subwindows/embed_subwindows=false` 才会让子窗口变成**真正的独立 OS 窗口**（否则是被困在主窗口里的嵌入子窗口，标题栏点击无效）。
   - 附：`AlwaysOnTop` 与 transient 冲突 —— `PopupCentered()` 会强制 transient，Windows 下报 `Windows with the 'on top' can't become transient.`。**不要给弹出窗口设置顶**。
6. **headless 下窗口几何不可信**。`--headless` 运行时窗口 `Size` 会被 MinSize/0 尺寸屏幕带偏（实测 `470x224` 报成 `360x180`、位置为负）。→ 规则：**几何/视觉相关的验证必须非 headless 运行**。
   - 取证手段：`Window` 本身是 Viewport，可用 `窗口.GetTexture().GetImage().SavePng(绝对路径)` **把窗口画面存成 PNG**，再做视觉复核（探针范例：`tests/SettingsProbe.cs`）。
7. **`config/` 目录有 `.gdignore`，Godot 完全忽略它**。放在其中的 json 在 `res://` 下读不到（`soul_template.md`、`agent.json` 都曾中招）。→ 规则：**运行时需要读取的配置放 `settings/`**；`config/` 只放模组用的数据（`i18n.csv` 等由代码按绝对路径读取）。
8. **`FileAccess` 二义性**：`Godot.FileAccess` 与 `System.IO.FileAccess` 同名。→ 规则：同时 `using Godot;` 和 `using System.IO;` 时，显式写 `Godot.FileAccess`。
9. **`OS.Execute` 不支持双向流**。拉起 `hermes acp` 这类需要双向 stdio 的子进程必须用 `System.Diagnostics.Process`（见 §6）。
10. **`hermes` 的会话列表会被测试污染**。端到端 ACP 测试每跑一次就真实新建/复用一条会话。→ 规则：纯 UI 改动只跑不碰 Agent 的探针（`PanelProbe` / `ToolBarProbe` / `WindowProbe`）；只有改到 Agent 链路时才跑 `ChatFlowTest`，且跑完清理。

11. **`session/resume` 会回放整段历史，必须与实时回复分流**。恢复会话时 Agent 先把历史以 `user_message_chunk` / `agent_message_chunk` 成对回放（每条历史 = **一个** chunk），然后才返回 resume 响应。若客户端把所有 `agent_message_chunk` 都当成「本轮实时流式」，UI 会把**多轮历史累加成一个缓冲区 → 合并成一大段**（实测：`好`+`西瓜`+`连通` → `好西瓜连通`）。→ 规则：用「本轮 prompt 是否进行中」判定（`AcpClient._提示中`，在 `Ask` 发送前置位、收到 prompt 响应后清除）；历史块走独立事件 `OnHistoryChunk`，UI 作为**独立消息**追加。
    - 附带结论：`agent_thought_chunk`（思考流）与 `usage_update` / `available_commands_update` 都应**不显示**（当前只显示 `agent_message_chunk` 与历史用的 `user_message_chunk`）。

12. **「手指命中区」不要加位置类历史约束**。`WindowDrag.IsInValidZone()` 里曾有一条旧补丁：要求鼠标在屏幕**下 2/3**（`mousePos.Y >= screenHeight/3`）——那是「窗口远大于角色」时代的产物。窗口现在正好套住角色，精确的窗口矩形判定已足够；保留它会让**桌宠靠近屏幕上边缘时完全无法拖拽**（滚轮缩放同样被卡住），而右键（走 `Context._UnhandledInput`，不经过该判定）却正常，所以现象是「点得到但拖不动」。
    - 规则：命中判定只用**窗口矩形**；判定函数拆出「传入坐标」的重载（`在桌宠内(Vector2I)`）以便探针不依赖真实光标。
    - 附带实测：`DisplayServer.WarpMouse` 用的是**窗口相对坐标**（请求全局 960 → 实际落 960+窗口X）；探针要移光标需传 `目标全局 - 窗口位置`。

13. **指令通道有三处易踩的坑**（都由探针实测抓出）：
    - **显示路径必须静默解析**：流式每来一个 chunk 都会整段重解析，此时块内 JSON 常是**半截的** →
      若照常报错会刷一屏「JSON 行解析失败」。→ `解析(原文, 记日志:false)`。
    - **历史回放路径也要过滤**：hermes 存的是 Agent **原始**回复（含围栏块），`session/resume` 回放时
      不过滤就会在历史气泡里露出一堆围栏块（实测 4 条历史助手消息全带围栏）。→ 助手消息过 `过滤显示()`；
      **用户自己的消息不过滤**（围栏是主人自己写的，照原样显示才对）。
    - **跨 chunk 拼接**：围栏标记会被切成 `\`\`\`pe` + `t`，所以判断必须基于**累积缓冲**，不能按单块判断。
14. **状态机是动画的「所有者」**。写完 `play_anim` 类断言后又调用了任何 `set_state`，后者会立刻把动画顶回去——
    实测踩过：探针里 `play_anim walk-left` 被后续的 `set_state idle` 覆盖成 `idle-1`。断言必须**紧跟**在该动画成为最后一次状态变更之后。
15. **探针要隔离它不测的那一层**。情绪变体（P5）会按心情改播 `think-happy/poor`，于是 `PoolProbe` 里
    「池内随机」类断言会随主人存档心情**随机失败**。→ 探针先把自己不测的那层状态**钉死**（如心情钉中位 60），结束再恢复。

**视觉复核通道**：本机 `auxiliary.vision` 可用（模型已支持图片输入）。截图 + 视觉复核是 UI 改动的一等验证手段，不要只靠 headless 断言。

### 10.1 探针清单（改到相关代码就跑对应的那个）

| 探针 | 跑法 | 验证什么 |
|---|---|---|
| `PanelProbe` | headless | 命令栏 / 配置窗 / 状态机基本态 |
| `ToolBarProbe` | headless | 工具栏弹出与关闭 |
| `WindowProbe` | headless | 桌宠窗口几何不漂移 |
| `StateProbe` | headless | 状态效果表 / 状态锁 / 排队与作废 / 保持与兜底 / 入场门 / 退出保护 / 空闲与不打扰 |
| `SettingsProbe` | **非 headless** | 配置窗真实几何 + 把窗口画面存 PNG 供视觉复核 |
| `EnterProbe` | **非 headless** | 抓启动头几秒的窗口画面，核实「登场动画有没有播、有没有被抢断」 |
| `WalkProbe` | **非 headless** | 自主走动是否真的触发、窗口 X 是否真的移动（**必须非 headless**：headless 下屏幕/窗口尺寸为 0，`尝试走动` 会直接放弃） |
| `PoolProbe` | headless | 6 个语义池是否真的播出对应动画（断言没有回退到兼容池 fidget/idle/celerate） |
| `InteractProbe` | headless | 摸摸反应是否**完整播放三段序列**（a 进入 → b 保持 → c 退出 → 回待机），且各段耗时与素材原时长一致 |
| `HistoryProbe` | headless | 会话恢复回放的历史是否被切成**独立消息**（不再合并成一大段）。会真实连接 Agent |
| `DragProbe` | **非 headless** | 桌宠贴近**屏幕上边缘**时仍能起手拖拽、窗口精确跟随光标（真实光标 + 注入按键；结束会恢复窗口与光标位置） |
| `CommandProbe` | headless | 指令通道三组：解析（未闭合块/非 pet 围栏不误伤/坏 JSON 容忍）、执行（越权与超限的拒绝）、显示（跨 chunk 拼接不漏围栏） |
| `CommandE2E` | headless | **真实 Agent** 下发指令 → 解析 → 执行 → 回复与历史都无残留。会真实调用一次 LLM |
| `StatsProbe` | headless | 数值层：漂移/事件节流/夹取/存盘往返/**离线补算**/指令接线（结束恢复数值并删测试存档） |
| `MoodProbe` | headless | 数值驱动表达：情绪变体择档（think-happy/poor）+ 行为耦合（走动倍率、睡眠阈值）+ 无变体池的降级安全 |

**踩坑：验节律必须在场景实例化「之前」写配置。** `StateMachine._Ready` 会读 `user://behavior.json` 并用它算好 `_走动倒计时`；之后再改内存里的 `设置` 字段已经晚了（探针曾因此在 20s 内一次走动都触发不了）。`WalkProbe` 的做法：`_Ready` 里先写临时 `user://behavior.json` → 再实例化场景 → 结束时删除。