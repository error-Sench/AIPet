# AIPet 开发计划（Plan）

> **依据**：`AGENTS.md`（工程契约，How）+ `document/idea.md`（设计补充，Why/What）。
> **执行方式**：循序渐进，一个阶段做完并**验证通过**才开下一个；每阶段可独立回退。
> **验证约定**：`dotnet build` 0 错误 + headless 探针 PASS；UI 改动额外做截图 + 视觉复核（见 AGENTS.md §10）。

---

## 0. 阶段总览

| 阶段 | 主题 | 依赖 | 状态 |
|---|---|---|---|
| **P0** | Agent 会话链路（ACP）+ 三个面板（聊天 / 工具栏 / 配置） | — | ✅ 已完成 |
| **P1** | **身体层：状态机自主行为**（状态效果 + 触发 + 交互接入） | — | ✅ 已完成（2026-09-16） |
| **P2** | 动画资产：新增语义池（think/sleep/walk/greet/interact）+ VPet 资产导入 | P1 | ⏳ |
| **P3** | 灵魂层：`soul.md` 落地 + Agent 侧人格注入 | P0 | ⏳ |
| **P4** | 记忆与画像：`memory.jsonl` / `profile.md` + 注入通道 | P3 | ⏳ |
| **P5** | 数值层：`stats.json`（mood/energy）+ 可视化 + 情绪表达规则 | P1 | ⏳ |
| **P6** | 环境感知：前台窗口 / 键鼠空闲（隐私可控）→ 驱动主动行为 | P1 | ⏳ |
| **P7** | 模式层：办公 / 游戏一体（留接口，玩法后置） | P1 | ⏳ |
| **P8** | 打包分发：cwd 干净化 + 给其他用户的「人格注入 skill」 | 全部 | ⏳ |

> P2–P8 只列目标与验收口径，**不预先细化**——按 AGENTS.md 的做法，上一阶段完成后才细化下一阶段，避免纸上设计跑在实现之前。

---

## P1：身体层 —— 状态机自主行为

**目标**：桌宠从「被动播放动画的壳」变成「有自己节律的小生命」——会思考、会自己走动、会打招呼、会打瞌睡，且**不打扰主人**。

**架构**：`StateMachine`（`script/State/StateMachine.cs`）是身体层的**唯一状态权威**；它向上接交互/Agent 事件，向下驱动 `CharAnim`。自主行为由**心跳节律器**统一调度（不用多个 Timer 各自为政）。

### 1.1 状态效果表（本计划决定，v1）

| 状态 | 含义 | 现有动画池 → 目标池(P2) | 保持 | 退出条件 | 气泡/其他表现 |
|---|---|---|---|---|---|
| `idle` | 待机 | `idle` | 循环 | — | 无；保留原有 idle→fidget 小动作 |
| `interact` | 被摸摸 | `fidget` → `interact` | 2.0s | 超时 → idle | 30% 概率一句短话（节流：≥90s 一次） |
| `drag` | 被拖 | `drag` | 持续 | 松手 → idle | 无 |
| `think` | Agent 推理中 | `fidget` → `think` | 持续 | 首块回复 → speak / 结束 → idle | 面板内已有「思考中…」 |
| `speak` | 正在说话 | `fidget` → `say` | 持续 | `end_turn` → idle | 面板内流式文本 |
| `working` | 执行外部脚本 | `fidget` → `work` | 持续 | 脚本完成 → idle | 面板内 tip |
| `sleep` | 休眠 | `idle` → `sleep` | 持续 | 任何交互 / Agent 活动唤醒 | 无（不打字、不出声） |
| `walk` | 自主走动 | `drag` → `move` | 行为链 | 链结束 → idle | 无 |
| `greet` | 打招呼 | `celerate` → `greet` | 2.5s | 超时 → idle | 启动/唤醒时的招呼语 |
| `edge_hide` | 贴边隐藏 | — → `sidehide` | — | P2 后置，本阶段不做 | 无 |

> **降级原则**：目标池不存在时（P2 之前）自动回退到「现有动画池」列；`CharAnim.进入状态` 已经内置「未知池 → idle」的兜底。

### 1.2 自主行为节律（v1，参数全部走 `settings/behavior.json`）

| 行为 | 触发条件 | 约束 |
|---|---|---|
| `greet` | ① 启动完成加载后 1 次；② `sleep` 被唤醒时 1 次 | 计入主动预算 |
| `sleep` | 无交互 ≥ `睡眠空闲秒`（默认 600）；**深夜**（默认 23:00–07:00）阈值降为 `深夜睡眠秒`（默认 180） | 不计预算 |
| `walk` | 无交互 ≥ `走动空闲秒`（默认 45）后，每次心跳按概率触发（期望 2–5 分钟一次） | 计入主动预算 |
| `interact` | 左键单击（按下未超拖动阈值即松手） | 被动，不计预算 |

**不打扰硬约束（主动行为一律不得触发，若条件成立）**：
1. 聊天 / 配置 / 工具栏任一面板可见；
2. 鼠标悬停在桌宠身上（避免"自己凑上来"）；
3. 当前处于 `drag` / `think` / `speak` / `working` / `greet`；
4. 主人正在拖拽或正在与面板交互（最近 5s 内有面板/输入活动）；
5. 本小时主动行为已达 `每小时上限`（默认 8）。

### 1.3 任务分解（每步 2–5 分钟）

**任务 1｜状态效果表 + 配置读取**
- 改：`script/State/StateMachine.cs`（状态→池映射表 + `settings/behavior.json` 读取，缺省用内置值）
- 验证：`dotnet build` 0 错误

**任务 2｜状态锁：让 `CharAnim` 让位给状态机**
- 问题：`CharAnim.OnAnimationFinished` 会在动画播完时**自作主张**回 `idle`/`fidget`，会覆盖 `think`/`sleep` 等持续态。
- 改：`script/UX/CharAnim.cs` —— 在 `OnAnimationFinished` 开头加一个总开关：状态机处于「持续态」时直接重播当前状态动画并返回，不走原有 idle/fidget 逻辑。
- 新增：`StateMachine.接管中`（只读属性）+ `StateMachine.重播当前状态()`
- 验证：headless 探针断言「`think` 状态下跑 120 帧，CurrentState 仍为 think」

**任务 3｜心跳节律器**
- 改：`script/State/StateMachine.cs` —— `_Process` 内 1s 心跳：维护 `_空闲秒`（任何交互重置）、统计主动行为次数（按小时窗口）、调度 sleep/walk。
- 新增：`StateMachine.NotifyInteraction(来源)`（唯一重置空闲的入口）
- 验证：探针伪造 `_空闲秒` 推进，断言状态按预期迁移

**任务 4｜接入被动触发（交互入口）**
- 改：`script/UX/WindowDrag.cs` —— 松手时若 `_isPreparing && !_dragging`（= 单击摸摸）→ `StateMachine.摸摸()`；`OnDragStart()` → `SetState(Drag)` + `NotifyInteraction`；拖拽结束 → `SetState(Idle)`
- 改：`script/UX/Context.cs`（右键）、`script/Logic/ClipboardRead.cs`、`script/Logic/FileDrop.cs`、`script/Audio/Kws.cs` → 各触发点补 `NotifyInteraction` 并唤醒 `sleep`
- 改：`script/UX/ChatBox.cs` / `AgentEvents.Bind` → 现有 `SetState(Think/Speak)` 改为新的持续态调用（带锁定）
- 验证：探针模拟各入口，断言状态与空闲计时正确

**任务 5｜不打扰约束 + 频率预算**
- 改：`script/State/StateMachine.cs` —— 实现 1.2 的 5 条约束（面板可见性用 `ChatBox.可见` / `ToolBar.可见` / `SettingsWindow.可见`，鼠标悬停用 `DisplayServer.MouseGetPosition` 与桌宠窗口矩形）
- 验证：探针分别置面板可见/不可见，断言 walk 只在不可见时触发

**任务 6｜探针与文档收口**
- 新建：`tests/StateProbe.cs` + `tests/StateProbe.tscn`（headless：状态迁移、预算、不打扰、唤醒）
- 改：`AGENTS.md` §3（把状态效果表与节律参数写进契约）
- 验证：`PanelProbe` / `ToolBarProbe` / `WindowProbe` / `StateProbe` 全 PASS + `dotnet build` 0 错误

### 1.4 可能改动的文件

```
script/State/StateMachine.cs      # 主体：状态效果表 / 心跳 / 触发 / 约束
script/UX/CharAnim.cs             # 让位开关（状态锁）
script/UX/WindowDrag.cs           # 单击摸摸 + 拖拽状态接入
script/UX/Context.cs              # 右键 → 交互记录
script/UX/ChatBox.cs              # think/speak 持续态
script/Logic/ClipboardRead.cs     # 粘贴 → 交互记录
script/Logic/FileDrop.cs          # 拖入 → 交互记录
script/Audio/Kws.cs               # 语音 → 交互记录/唤醒
settings/behavior.json            # 新增：节律参数（可调，不用重编译）
tests/StateProbe.cs (+ .tscn)     # 新增：状态机回归探针
AGENTS.md                         # 契约同步
```

### 1.5 风险与对策

| 风险 | 对策 |
|---|---|
| **新旧两套状态逻辑互相覆盖**（`CharAnim` 自己会回 idle） | 任务 2 的总开关是唯一单点，先做、先验证 |
| 持续态卡死（Agent 不回 `end_turn` 就永远 think） | 每个持续态带**兜底超时**（think/speak/working 默认 120s），超时回 idle |
| 自主走动把桌宠挪出屏幕/挪到不可见区 | 走动前 clamp 到屏幕可用区内；位移 ±60~160px 且不跨屏 |
| 主动行为打扰（设计红线） | 任务 5 的 5 条约束 + 每小时上限；参数在 json 里，可调到 0 即完全关闭 |
| 频繁重播动画导致 CPU 占用 | 重播有最小间隔（≥0.2s）；空闲心跳不做事就早退 |

### 1.6 验收口径（P1 完成定义）

1. `dotnet build` 0 错误；4 个探针全 PASS。
2. 启动后不碰它：≥45s 后能看到自主走动；≥10 分钟（或深夜 3 分钟）进入 sleep。
3. 单击桌宠有反应（interact），拖动时立即打断自主行为。
4. 面板打开期间**没有任何**主动行为。
5. 参数全在 `settings/behavior.json`，改完重启生效，无需重编译。

---

## P2 起的验收口径（只列口径，不细化）

- **P2**：新增语义池后，`think/sleep/walk/greet` 各自播放**专属**动画（不再回退）；VPet 资产导入后与原画风一致、帧率正确。
- **P3**：`soul.md` 改动后重启桌宠，Agent 回复的语气随之改变（人格生效）。
- **P4**：跨天对话能引用昨天说过的事（记忆可检索、可被引用）。
- **P5**：`stats.json` 的 mood/energy 随交互变化，并有可视化面板。
- **P6**：能按「主人在不在电脑前」调整主动行为频率（隐私可关）。
- **P7**：点击桌宠可无缝进/出游戏模式，进度保留。
- **P8**：打包后 cwd 干净（不把 `AGENTS.md` 灌进人格），并附一份给其他用户的「人格注入 skill」。

---

## 附：为什么这样排序

1. **P1 先做**：它是「桌宠感」的地基，且不依赖任何新资源（缺动画时优雅降级），做完立刻能看见效果。
2. **P2 紧随**：P1 定完状态语义，才知道需要哪些动画——反过来做会白导资源。
3. **P3/P4 依赖 P0 已完成**（Agent 链路已通），可并行开工，但要在 P1 之后以免同时改 `StateMachine` 与 Agent 回调造成冲突。
4. **P8 最后**：人格注入 skill 必须等项目完工才能写准（现在写不准——这是已知结论）。
---

## P1 完成记录（2026-09-16）

**已实装**：状态效果表 / 状态锁（`CharAnim.OnAnimationFinished` 让位）/ 心跳节律器 / 不打扰闸门 / 每小时预算 / 行为链走动 / 保持与兜底超时 / 入场门 / 交互入口接入。

**验证**：`dotnet build` 0 错误；`StateProbe` 22 项断言全 PASS；`PanelProbe` / `ToolBarProbe` / `WindowProbe` 无回归；启动期无 ERROR。

**实装时发现并修掉的 4 个真问题**（均已写进 AGENTS.md）：

| 编号 | 问题 | 修法 |
|---|---|---|
| R6 | StateMachine 在 Main 之后 `_Ready`，其 `PlayState("idle")`（deferred）会抢掉入场动画 → `case "enter"` 永不触发 | `_Ready` 不播放表现 + 入场门 `_入场未完成`，由 `CharAnim` 回调 `入场完成()` 解除并打启动招呼 |
| R7 | 持续态锁定会让**退出动画**的播完回调被吞 → `case "exit"` 不触发、程序关不掉 | 锁定判断放行退出动画 + `播放退出动画()` 先调 `准备退出()` 解锁 |
| R5 | 拖拽表现有两套驱动（`CharAnim.开始拖拽` 与 `SetState(Drag)`）会互相覆盖 | 新增 `标记状态()`：只改逻辑态不驱动表现 |
| R3 | `取消桌宠拖拽()` 是双入口（松手 + 面板抢指针每帧），塞进去会产生假「摸摸」 | 单击判定只写在松手分支，且早于状态复位 |

## P2 开工准备（侦察结论已备，可直接施工）

### 动画池机制（关键约束，全部为实测）

- **路径 = `mods/main_anim/anim/<人物>/<池名>/<变体名>/`**，`info.json` 与帧 PNG 同层（`AnimLoader.cs:25-49`）。
- **池名 = `Type` = `动画池字典` 的 key**，由**目录名强制决定**（`info.json` 里写 `Type`/`Path` 无效）。
- `info.json` 唯一必需字段是 **`rate`**（FPS）。缺 `info.json` 的目录会被**静默跳过**。
- **新增池必须同步改 2 处代码**：① `CharAnim.内置动画组` 加池名（否则不被预载，`Play` 报动画不存在）② `CharAnim.OnAnimationFinished` 的 `switch(Type)` 加分支（否则动画播完**永久冻结在末帧**）。
  > ① 已在 P1 提前登记（`think/say/work/listen/sleep/walk/greet/interact/move`），池目录不存在时加载管线自动跳过，无副作用；② 留到 P2，因为每个池的"播完去哪"要对着素材定。
- **帧排序是字符串序**（`filePaths.Sort()`，`CharAnim.cs:216`）→ 帧名必须**等宽零填充**（`000.png`…）；同目录内前缀必须一致，否则多条序列会被拼成一条。
- 单帧也合法（= 静态图）；窗口尺寸按**首帧**尺寸算（`CharAnim.cs:69`）。

### VPet 资产导入规则（`D:\SteamLibrary\steamapps\common\VPet\mod\0000_core\pet\vup`）

25 组 / 566 叶子 / 6288 PNG，1000×1000。首选映射：`Think→think`、`Sleep→sleep`、`MOVE/walk.*→walk`（含 faster/slow 速度档）、`Touch_Head→greet`、`Touch_Body·Pinch→interact`、`Say→say`、`WORK→work`、`Default→idle`、`Raise→drag`、`SideHide_*→edge_hide`、`StartUP/Shutdown→enter/exit`。

导入七步：① 叶子目录 → `<语义池>/<变体>/`，目录名改**小写 ASCII、无点无空格** ② **按文件名前缀拆目录**（VPet 常在单目录塞两条序列，如 `1毛笔开心_*` + `2…退出通用_*`、`FLA_*`+`FLB_*`）③ 帧重编号为**三位零填充从 000 起** ④ **丢弃 1bit 灰度 `*_lay`/`front`/`back` 遮罩层**（不是帧序列）⑤ `info.json` 只写 `{"rate": 8}`（125ms 折算）⑥ 图片缩放到 **512×512**（与现有素材一致）⑦ 每批用 `find <池> -name info.json | wc -l` 对齐叶子数，防静默漏加载。

**单帧叶子约 40 个**（Pinch 3、Music 3、IDEL 5、SideHide_Main 各 5、MOVE 6、WORK 3…），导入后就是静态图，需按「是否可接受静态表现」逐个决定去留。

## P2 进度（进行中）

### ✅ 已完成：walk 池（2026-09-16）

| 项 | 内容 |
|---|---|
| 资产来源 | VPet `MOVE/walk.left` / `walk.right` 的 `B_Nomal` 体态档（A/B/C 是 VPet 的体态档；取中间档、各 6 帧最平滑） |
| 产物 | `mods/main_anim/anim/loris/walk/{left,right}/`，各 `000.png…005.png`（三位零填充）+ `info.json {"rate": 8}` |
| 转换 | 导入工具 `tools/import_vpet_walk.py`（可复用、参数化）。**关键：不能直接 1000→512 缩放**——必须按角色包围盒对齐到现有 loris 素材基线（参考帧底边 497 / 中心 x 263 / 角色高 485），且整段动画只算**一次**偏移逐帧套用，否则会抹掉帧间位移（走动的动作本身）。实测导入后角色高 482、底边 499，与参考一致 |
| 循环 | `CharAnim.循环动画组 = ["walk"]` → 该池以循环模式载入（6 帧 @8fps = 0.75s，一次位移 ≈1s，不循环会断档） |
| 方向 | `CharAnim.PlayNamed("walk-left"/"walk-right")`（新增：按动画名精确播放，区别于 `PlayState` 的「按池随机取一项」）；状态机按 `_走动目标X` 与当前 X 决定方向 |
| 不重播 | 三个走链节共用同一段动画，已是目标动画时不重播（否则链节边界重置相位、看起来一顿一顿） |
| 降级 | 池缺失时仍回退 `drag`（P1 占位行为保留） |
| 验证 | `WalkProbe` 断言「走动期间动画 ∈ {walk-left, walk-right}」+「窗口 X 真的移动」；12 帧全为 RGBA 透明底（无白方块）；4 个 headless 探针无回归 |

### ✅ 已完成：think / say / work / sleep / greet / interact（2026-09-16）

通用导入器 `tools/import_vpet_anim.py`（参数化 SPEC，`python tools/import_vpet_anim.py [池名]`），共导入 **169 帧 / 16 段动画**：

| 池 | 变体 | 帧数 |
|---|---|---|
| think | nomal / happy / poor | 9×3 |
| say | smile / self / serious | 7/15/4 |
| work | pc / read / write | 14/12/10 |
| sleep | loop / happy（**循环**） | 6+6 |
| greet | amuse / meow | 11/20 |
| interact | head / body / happy | 11/11/15 |

**语义修正**：原计划把 `Touch_Head` 给 `greet`，实际它是「被摸头的反应」→ 归 `interact`；`greet`（打招呼）改用 VPet 的开心姿势（`IDEL/amusement_B`、`IDEL/Meow/Happy/1`），因为 VPet 没有专门的打招呼动作。

**尺度坑（实测）**：第一版按「每段动画各自的包围盒高度」反推缩放，结果 `sleep` 被放大到 **1.021 倍**（躺姿包围盒本来就矮）；含道具的 work 动画又因道具进入包围盒而让角色缩水。→ 改为**固定缩放 `485/948`**（VPet 站立高 ↔ loris 参考高），所有姿势保持同一角色比例。验证：站立类 484–489（参考 485）、躺姿 241 高 × 476 宽（天然横躺比例）。

**锚点约定**：包围盒**底边**对齐参考帧底边（地面线）——躺姿也躺在这条线上；若居中则躺下的宠物会浮空。

**验证**：`PoolProbe` 断言 6 个池实际播出的动画名前缀（`think-nomal` / `say-self` / `work-read` / `sleep-loop` / `greet-amuse` / `interact-body`）全 PASS；5 个探针无回归；启动无 ERROR。

### ⏳ 待导入的池

`listen`（无对应资产，回退 fidget）、`edge_hide`（贴边隐藏）仍未导入。其余状态均已用专属动画。

> 导入每个池都要过同一套规则（按前缀拆目录 / 三位零填充 / 丢 1bit 遮罩层 / 缩放对齐 512 / 写 rate），`tools/import_vpet_walk.py` 可直接改造成通用导入器。

## 两个交互/会话缺陷修复（2026-09-16）

### ① 会话恢复时历史被合并成一大段

**现象（用户反馈）**：会话追回后，多轮助手回复被合并成一整块。

**根因（实测流量取证）**：`session/resume` 时 hermes **先把整段历史回放**出来（`user_message_chunk` / `agent_message_chunk` 成对，每条历史 = 一个 chunk），**之后**才返回 resume 响应；而客户端把所有 `agent_message_chunk` 都当成「本轮实时流式」→ ChatBox 把它们累加到同一个流式缓冲 → 合并成一条消息（实测产物：`好`+`西瓜`+`连通` → `好西瓜连通`）。

**修复**：
- `AcpClient` 加「本轮 prompt 进行中」标志 `_提示中`（`Ask` 发送前置位、收到 prompt 响应后清除）；进行中的 `agent_message_chunk` 才是实时回复，其余走新事件 `OnHistoryChunk`。
- `ChatBox.历史消息()` 把回放历史作为**独立消息**追加，首次还插入灰色分隔提示「──── 以下为上次会话（它还记得的旧事）────」。
- 顺带：`agent_thought_chunk` / `usage_update` / `available_commands_update` 一律不显示。

**同批修掉的小问题**：面板隐藏期间容器布局未计算 → 滚动条 `MaxValue` 为 0 → 单次 `CallDeferred(滚到底)` 落空，**打开面板会停在最旧一条**。改为「连续 3 帧施加滚到底」。

**验证**：`HistoryProbe` 断言「有分隔提示 / 「小萝」出现 ≥2 次（多条独立消息）/ 无合并产物『好西瓜』」全 PASS；视觉复核确认分隔线与逐条消息渲染正常、已滚到底部。

### ② 摸摸结束后硬切待机（动画没播完）

**现象（用户反馈）**：摸摸动画结束后直接跳待机，很突兀。**用户判定准确**：原版动画有后半段，是我没播完整。

**根因**：VPet 的摸头反应在素材里是**三段**：`Touch_Head/A`（进入：抬手）+ `B`（保持：抱头）+ `C`（退出：放下手回待机）。原先只导入了 `B`，于是动画结束时角色保持「抱头」，随后被状态机的保持计时切到待机 → 姿态硬切。

**判定依据（实测）**：A 的末帧是抱头、C 的末帧是「双臂下垂 ≈ 待机」（逐格视觉比对 8 格对照图确认）。

**修复**：`StateMachine._序列表` 声明交互序列 `["interact-a","interact-b","interact-c"]`；由**「动画播完」回调**逐段推进（不做时长猜测），末段播完自动回 idle。序列存在时该状态视为接管中；拖拽（`标记状态`）会立即打断序列。

**验证**：`InteractProbe` 断言轨迹 `[interact-a → interact-b → interact-c]`、终态 idle，且各段耗时与素材原时长一致（a = 2帧@4fps = 0.5s → 实测 30 帧；b = 11帧@8fps = 1.375s → 实测 83 帧）。视觉复核：退出末帧与待机姿态接近、可自然衔接、各格大小一致无裁切。

## ③ 桌宠贴近屏幕上边缘无法拖拽（2026-09-16）

**现象（用户反馈）**：桌宠一旦靠近屏幕上边缘就无法拖拽，但可以点击到。

**根因**：`WindowDrag.IsInValidZone()` 里有一条**历史遗留约束** —— 要求鼠标在屏幕**下 2/3**（`mousePos.Y >= screenHeight/3`）。那是「窗口远大于角色」时代的补丁；桌宠贴上边缘时鼠标必然落在上 1/3 → 判定永远为 false → 拖拽起手不了。右键（走 `Context._UnhandledInput`，不经过该判定）正常，所以现象正是「点得到但拖不动」。同一条约束也卡住了**滚轮缩放**（`WindowScale` 复用该判定）。

**修复**：删除该位置约束，命中判定只用**窗口矩形**；拆出可传坐标的重载 `WindowDrag.在桌宠内(Vector2I)` 供探针使用（不依赖真实光标）。

**验证**：新增 `DragProbe`（非 headless，真实光标 + 注入按键）——把桌宠挪到屏幕上边缘（用例落点 y=143，旧门槛 y≥344）后：

```
PASS  上边缘处：指针落在桌宠身上被判定为有效区
PASS  上边缘处：能起手（旧逻辑在这里会失败）
PASS  越过阈值后真的进入拖拽状态
PASS  拖拽时窗口真的跟随光标移动（位移 (30,20) 与光标位移精确相等）
PASS  松手后拖拽状态复位
（结束已恢复窗口与光标位置）
```

> 附带实测坑：`DisplayServer.WarpMouse` 是**窗口相对坐标**（请求全局 960 → 实际落 960+窗口X）。

---

## P0 扩展：Agent 指令通道（下行面）落地（2026-09-16）

**目标**：AGENTS.md §6 里那张「Agent 可下发命令白名单」表，此前**只声明没有消费者**。本节把它接通。

### 协议选择：为什么是「文本内嵌围栏块」

ACP 的 `session/prompt` 响应只有 `stopReason`，**没有自定义字段通道**；而各 Agent 结构不同（见 idea.md 人格注入原则）。
所以协议定在**回复文本**上：

````
好的，我这就去看看喵~
```pet
{"cmd":"set_state","state":"think"}
{"cmd":"speak","text":"查到了"}
```
````

任何 Agent（Hermes / 别的 ACP 客户端 / HTTP 兜底）都能用，桌宠侧零耦合。块内一行一条，JSON 行或键值行都收；
键名支持别名（`cmd|command|命令`、`state|状态`、`text|文本|say` …），容错优先。

### 实现分层

| 层 | 文件 | 职责 |
|---|---|---|
| 解析 + 校验 + 执行 | `script/Agent/PetCommands.cs` | 围栏块解析、显示过滤、白名单、参数校验、分发 |
| 挂接 | `script/Agent/AgentBridge.cs` | 一轮结束时 `处理回复()`：先执行指令，再把**干净文本**当回复 |
| 显示 | `script/UX/ChatBox.cs` | 流式与历史回放**两条**路径都过滤围栏块 |

### 安全（保守默认）

- Agent 文本是**数据**。非白名单 → 拒绝；参数不过校验 → 拒绝；每轮上限 6 条。全部落日志。
- `set_state` 查 `StateMachine.状态有效()`；`play_anim` 查 `CharAnim.有动画()` —— 拒绝任意字符串（实测越界构造如 `../../evil`、以及伪造的 shell 命令串，都被挡下）。
- `speak` 限 200 字 + 转义 `[`（防 BBCode 注入）；`queue_chain` 限 5 步 / 单步 ≤30s。
- `set_mood` / `soul_get` / `soul_set` **已登记但未实现**（P3）→ 记「未实现」而**不假装成功**。
- `aggressiveMode`（`settings/agent.json`，默认 false）当前只放宽 `open_url`（限 http/https）。
  **刻意不实现任何「执行本地命令」能力** —— 那需要专门设计 + 主人明确授权，不应顺手开洞。

### 验证

| 探针 | 覆盖 |
|---|---|
| `CommandProbe`（headless，27 断言） | A 解析（含未闭合块、非 pet 围栏不误伤、坏 JSON 容忍）· B 执行（越权/超长/未知状态/未知动画/链内非法/每轮上限/未实现/激进开关）· C 显示（真走 ChatBox 流式，跨 chunk 拼接） |
| `CommandE2E`（headless，真实 Agent） | 真实 LLM 下发指令 → 解析 → 执行（interact + speak 都验到）→ 回复与历史都无围栏残留 |

### 本轮探针抓到的两个真 bug（已修）

1. **流式显示路径刷错误日志**：流式每来一个 chunk 就整段重解析，块内 JSON 常是半截的 → 刷一屏「JSON 行解析失败」。
   修：`解析(原文, 记日志)`，显示路径传 false（静默）。实测修后该类日志 0 条。
2. **会话恢复回放不做过滤**：hermes 存的是 Agent **原始**回复（含围栏块），`session/resume` 回放时若不过滤，
   历史气泡里会露出一堆围栏块（实测 4 条历史助手消息全带围栏）。
   修：`ChatBox.单例历史消息` 对助手消息也过 `过滤显示()`；整条都是指令块时不产生空气泡。
   **用户自己的消息不过滤** —— 围栏是主人自己写的，照原样显示才对。

---

## P5 数值层 —— 第一切片（2026-09-16）

**为什么先做 P5 而不是 P3**：P3（灵魂层 + 人格注入）需要主人参与设计（soul 结构、注入方式、给其他用户的 skill），
而 P5 是完全自足的一层，且它正是指令通道里 `set_mood` 那个「已登记未实现」缺口的上游。先把缺口补上。

### 设计原则（承主人决策）

1. **数值不是灵魂**：`soul.md` 只放人格；数值独立成 `user://stats.json`，可可视化、可随手改。
2. **有惯性**：数值随时间自然漂移，不是只有互动才变（否则它就是一坨死数据）。
3. **离线也要活着**：存盘带 `savedAtUnix`，下次启动按「离开了多久」补算离线漂移——你不在时它也会累、也会想你。
4. **驱动表达而非驱动人格**：数值只影响**表现与主动行为频率**，绝不改写人格与说话方式（那归 soul.md）。

### 数值规则表（v1，权威实现见 `script/Soul/StatsTable.cs`）

| 数值 | 区间 | 自然漂移 | 事件增量 |
|---|---|---|---|
| `mood` 心情 | 0–100 | 每小时向**基线 60** 线性靠拢 4 点（高则淡、低则缓） | 摸摸 +6（**节流 60s**）、对话 +4、打招呼 +2 |
| `energy` 精力 | 0–100 | 清醒 −3/小时；睡眠 **+14/小时** | 走动 −1/次 |
| `affection` 亲密 | 0–999 | 无（只增不减） | 摸摸 +1、对话 +1（**节流只挡心情，不挡亲密**） |

阈值判定：`心情低落 = mood < 30`、`精力不济 = energy < 20`（供后续「低落时降频主动行为」用）。

### 接线点

| 位置 | 接线 |
|---|---|
| `StateMachine._Process` | `StatsTable.心跳(delta, 睡眠中: CurrentState == Sleep)` —— 复用**已有心跳**，不新增 Timer |
| `StateMachine.摸摸/唤醒/走动` | `事件_摸摸` / `事件_打招呼` / `事件_走动` |
| `AgentBridge.Ask` | `事件_对话`（一次发言 = 一次互动） |
| `PetCommands` | `set_mood` **从「未实现」转正**：接受数值（0–100）或关键词（happy/开心、tired/累、sad/难过…） |
| 自动存盘 | 每 30s 一次（带时间戳） |

### 验证（`StatsProbe`，headless，15 断言全 PASS）

- **漂移**：心情高于基线回落（90 → 86，= 4 点/小时，线性可预测）、低于基线回升（20 → 22.7）；清醒耗精力 / 睡眠回充
- **事件与节流**：摸摸加心情+亲密；节流内二次摸摸**只加亲密**；对话加心情；走动耗精力
- **夹取**：上限（100/100/999）与下限（0/0/0）都夹住；阈值判定与解除
- **指令接线**：`set_mood=85` 生效、`set_mood=难过` 映射 20、无法识别的值被拒；`soul_set` 仍正确记「未实现」
- **存盘与离线补算**：文件含数值+时间戳；手改时间戳模拟「离线 2h 后启动」→ 心情 70 → 62、精力 50 → 44（离线真的在漂移）
- 探针结束**恢复原始数值并删除测试存档**，不污染主人的存档

### P5 待续（下一批）

1. **可视化**：设置窗/信息面板里的「心情/精力/亲密」展示（沿用 MicaTheme，含数值条）。
2. **表达耦合**：`心情低落` → 降低主动行为频率；`精力不济` → 更容易进入 sleep；表情池按数值择档（think-happy / think-poor 已有素材）。
3. **数值进 Agent 上下文**：每轮把数值摘要注入 user message（不做人格改写），让 Agent 知道它「现在什么状态」。

---

## P5 数值层 —— 第二切片：数值驱动表达与行为（2026-09-16）

**目标**：数值不能只躺在文件里，得**看得见地影响表现**。

### 情绪表达（动画变体择档）

`StateMachine.应用表现` 在按池播放前先试**情绪变体**：

| 心情 | 变体 | 效果 |
|---|---|---|
| ≥ 75 | `happy` | `think-happy`（思考得开心）、`interact-happy`（被摸摸时开心版） |
| < 35 | `poor` | `think-poor`（蔫的思考） |
| 其余 | 池内随机 | 不做硬造 |

**降级规则**：该池没有这个变体就退回池内随机 —— 例如 `say` 池只有 `smile/self/serious`，请求 `happy` 会自动回退（探针实测 `say-serious`），**不硬造动画**。

### 行为耦合（做成纯函数，可断言）

| 耦合 | 规则 | 实现 |
|---|---|---|
| 心情低落少乱跑 | 走动间隔 **×1.6** | `StateMachine.走动间隔倍率` |
| 精力不济提前打瞌睡 | 睡眠阈值 `min(设置值, 120s)` | `StateMachine.有效睡眠空闲秒` |

> **为什么做成纯函数**：探针能直接断言实际生效的值，不必等几分钟真实时间流逝。这是「可验证」优先于「写法优雅」。

### 验证（`MoodProbe`，headless，9 断言全 PASS）

```
PASS  B1 心情正常 → 走动间隔倍率 1.0；睡眠阈值不变（600s）
PASS  B2 心情低落 → 走动间隔倍率 1.6（少乱跑）
PASS  B3 精力不济 → 睡眠阈值提前到 120s
PASS  A1 心情 90 → 思考播 think-happy
PASS  A2 心情 20 → 思考播 think-poor
PASS  A3 心情中间值 → 用池内随机（think-nomal，不做硬造情绪）
PASS  C1 say 池无 happy 变体 → 安全回退池内随机（say-serious）
```

### 顺带修的探针脆弱点

`PoolProbe` 断言过 `think-nomal`，而情绪变体会按心情改播 `think-happy/poor` → 一旦主人存档心情偏低/偏高，这个断言就会**随机失败**。
修：PoolProbe 开始时把心情**钉在中位 60**（隔离情绪变体），结束时恢复。**教训：探针要隔离它不测的那一层状态。**

---

## AGENTS.md 审批落盘（2026-09-16 夜，主人已批）

主人回「继续推进，你留下的待审批，审批清除掉吧」→ 全部落盘，`document/待审批-AGENTS改动.md` 已删除。

**第一节（事后补审的 4 处）**：全部追认，无需回退。

**第二节（6 条提案）**：全部落盘 —

| 提案 | 落盘位置 | 内容 |
|---|---|---|
| 1 | §6 | 「指令通道（下行面）落地」小节：ACP 无自定义字段通道 → 协议定在回复文本内嵌围栏块；块内一行一条；永不显示；实现与验证指路 |
| 2 | §6 安全边界第 2 条 | 激进开关写实：`settings/agent.json` 的 `aggressiveMode`，当前仅放宽 `open_url`；**刻意不实现本地命令执行** |
| 3 | §6 白名单表 | 新增「实现状态」列：5 条已实现、`set_mood` 已转正、`soul_get/set` 待 P3、`open_url` 仅激进 |
| 4 | §10 坑 #13/#14/#15 | 指令通道三坑（静默解析 / 回放也要过滤 / 跨 chunk 拼接）＋「状态机是动画所有者」＋「探针要隔离不测的那层」 |
| 5 | §10.1 探针表 | 新增 `CommandProbe` / `CommandE2E` / `StatsProbe` / `MoodProbe` 四行 |
| 6 | §2 末尾 | **P3 交付约束（硬规则）**：人格注入 skill 与灵魂模板是给其他用户的、**不得注入本机 Agent**、完工时才写、两者都要有 |
