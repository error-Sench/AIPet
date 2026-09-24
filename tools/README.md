# 开发工具 —— 说明文档

> 本文件是 **`AIPet-Agent.md` 的分册**（工具链部分）。
> **`tools/` 不参与游戏运行**——它是开发 / 构建 / 资产导入的工具箱；运行时要读的东西在 `config/`（配置）与 `mods/`（内容）。

## 目录

| 工具 | 用途 |
|---|---|
| `anim/import_vpet_anim.py` | **动画资产导入（通用）**：VPet → `mods/main_anim/anim/loris/<池>/` |
| `anim/import_vpet_walk.py` | 走动专用导入器（历史原因保留：它要按角色包围盒对齐基线） |
| `package.sh` | 打包：构建 → 导出 → 组装 `dist/AIPet-<版本>/` → 打 zip（**导出前先关掉在跑的 Godot 编辑器**——挂起态编辑器会锁构建产物，导致 .NET 构建失败、包变砖） |
| `run_probes.sh` / `run_probes.ps1` | 一键跑全部回归探针（`--list` / `--all` / `--only`） |
| `aipet_mcp_smoke.py` | MCP 服务冒烟（不依赖 Hermes）：`pet_context` 读盘 + `pet_command` 收件箱/回执（含「无桌宠超时」「假桌宠回执」两态）；改过 `dist/aipet-mcp/` 后跑一次 |
| `install_edge_tts.ps1` | 装 Edge 语音（TTS 可选依赖；装到 `%LOCALAPPDATA%\AIPet\tts-venv`，不碰系统 Python） |

> **为什么动画导入器在 `tools/`**：导入器是**开发期工具**（从 VPet 素材生成动画池）；**成品动画资产在 `mods/main_anim/`**（运行时读取）。
> 工具与成品分离——改工具不影响运行；`tools/` 也**不随分发包发布**。

## 1. 动画导入（`anim/`）

| 脚本 | 用途 |
|---|---|
| `anim/import_vpet_anim.py` | **通用导入器**：`SPEC` 里一张「池 → [(源叶子路径, 目标变体名)]」表；`python tools/anim/import_vpet_anim.py [池名]` |
| `anim/import_vpet_walk.py` | 走动专用（历史原因保留：它要按角色包围盒对齐基线） |

**素材来源**：`D:/SteamLibrary/steamapps/common/VPet/mod/0000_core/pet/vup/`（VPet 原项目，1000×1000 画布）。
**输出目录**：自动相对本仓库定位（`<仓库根>/mods/main_anim/anim/loris/`）——换机器不用改脚本。

## 2. 导入规则（都是实测结论）

| 规则 | 说明 |
|---|---|
| **固定缩放 `485/948`** | 不要「按每段动画各自的包围盒高度」反推缩放：躺姿包围盒本来就矮，会被放大（实测 `sleep` 被放大 1.021 倍）；含道具的 work 动画又会因道具进包围盒让角色缩水 |
| **底边对齐** | 包围盒**底边**对齐参考帧底边（地面线）——躺姿也躺在这条线上；若居中则躺下的宠物会浮空 |
| **固定画布映射（转场池）** | **特效画满整张画布**的转场动画（`enter`=StartUP 门板、`exit`=Shutdown 故障方块）**不做逐段 union 对齐**——union 被特效带偏，角色站立帧脚线会落到 483（idle 是 500，衔接跳位）。它们在 `固定映射池` 里，改用 idle 源（`Default/Happy/1`）推出的画布偏移，全池统一（= 与 idle 逐像素同站位；特效超出 512 的部分按窗口边裁，与 VPet 窗口观感一致）。**新增这类池照此入列** |
| **原画布口径（拖拽系 Raise）** | 拖拽/拎起姿态（`drag`/`dragup`/`dragdown`/`draghold`）沿用**原项目旧导入口径**：整张 1000 画布缩到 512（`512/1000`、BILINEAR）、**零偏移**——不做角色包围盒基线对齐。VPet 的 raise 姿态是作者按 `raisepoint`（抓握点）画的，「被拎起来」的站位就在画布原位；union 对齐会把角色整体下拉 ~81px → 与旧变体切换时跳位。2026-09-24 实测旧产物与该口径**像素级一致**（alpha 平均差 ≤0.07）。见 `原画布池` |
| **基线快照 `baseline.json`** | 站位锚点（底边 500 / 中心 x 270.5 / 高 489）冻结在 `tools/anim/baseline.json`——**不要**每轮从导入产物现场算：旧实现拿 `idle/happy-1`（导入器自己的输出）当基准，全量重导自反馈、每轮漂移 +8px（已修）。删快照文件才会从 REF 重建 |
| **按帧序号排序** | 帧名是 `<前缀>_<序号>_<时长>.png`；**必须按序号排**，不能按文件名 |
| **丢遮罩层** | `mode` 为 `1`/`L`/`LA` 的是灰度/1bit 遮罩，丢弃 |
| **节奏 `rate`** | 由帧时长推：`rate = 1000 / 模式时长`（单帧长保持帧的 rate 会很低，如 `2`） |
| **先看图再动手** | 导入前先拼对照图做视觉复核，确认语义（「A/B/C 是体态档还是动作三段」这种事**凭名字猜必错**，实测踩过两次） |

## 3. 踩坑

### 坑：一个目录里「多序列」的判据不能用文件名前缀

VPet 里真实存在**帧名拼写不一致**：`SideHide_Right_Main/Nomal/A/` 里 `A_000..A_013` 少了 `A_011`，
第 11 帧被命名成 **`A01_011`**。若按「前缀不同 = 混了两条序列」判，整段 `right-in` 会被跳过（丢素材）。

→ **规则：按帧序号判重复**——序号唯一就合并并按序号排序；序号重复再看前缀：**同前缀 = 源未重编号**（如 `跳绳开心_000_124` + `跳绳开心_000_125`，保留全部帧、按「序号, 文件名」排）；**跨前缀 = 真混装**（如 FLA_/FLB_，跳过，用 4 元组的前缀切片拆）。

### 坑：VPet 的目录里可能**多粘/少装**帧（要用帧切片，规则 7）

实测（2026-09-19，数值核对 + 左右镜像比对）：

* `SideHide_Right_Main/Nomal/A` 有 14 帧，其中**只有前 9 帧是「缩进」**（与左 A 逐帧镜像一致）；
  第 9/10 帧其实是「退出」的起跳两帧（与 `Left_Main/C` 的第 0/1 帧镜像一致），11-13 帧是收尾。
  → 右「缩进」切 `0..8`，那两帧起跳帧拼进右「退出」，两侧这才真正一一对应。
* `SideHide_*_Rise/Nomal/B`（10 帧）是**探出后的微动循环**，早先漏导 → 表现为「探出没动画」。

→ **规则：导入 spec 支持 `(源, 起帧序号, 止帧序号)` 切片，一个变体可以由多个片段按序拼接**
（`import_vpet_anim.py` 的 `收集片段()`）。判据仍先看图/量包围盒，别凭目录名想当然。

### 坑：新增池必须改 2 处代码（见 `script/UX/README.md` 的动画池机制）

只导入资产不够，还要登记 `CharAnim.内置动画组`，否则不预载、播放失败。`edge_hide` 就是照这条接的。

## 4. 已导入的池

| 池 | 变体 | 帧数 | 说明 |
|---|---|---|---|
| `idle` | happy-1..3 / nomal-1..3 / poor-1..2 | 13×3 / 8×3 / 17×2 | 待机（VPet `Default` 三档；2026-09-20 组①补 Nomal/Poor 并对齐三档：变体名 = `{档}-{n}`，三档开启时按 `idle-{档}-` 前缀取一条；**2026-09-24 动画组D**：组内改**加权随机**——`idle权重`〔nomal-1 为主 50%〕，档选择仍走降级链）|
| `walk` | left / right（+ `-a`/`-c` 起步/停步段） | 6+6 + 段 3+3/6+6 = 共 30 | 走动（**循环**）。**2026-09-20 打磨**：补 A/C 起步/停步段（`-a`/`-c` 结尾 → 自动非循环）、走链三段分播、位移只在循环段推进。**2026-09-22 重构#4**：走链删除——walk 成为移动表条目，由 MoveRunner 按 VPet Move 模型推进。**2026-09-24 Plan #22**：只做普通档——快/慢变体（`faster`=Happy / `slow`=PoorCondition）整删，不按心情分快慢 |
| `think` | nomal / happy / poor（+各档 a/c 过渡段） | 9×3 + 2 帧×6 | 三档状态（对接 P5 情绪变体）。2026-09-20 组①·过渡段：`think-{档}-a` 进入 / `think-{档}-c` 退出（包裹段，见 `script/State/README.md`）|
| `say` | self / self-smile / self-tease / serious / shining / shining-excited / shining-calm / shy / shy-wry（+各感情 a/c 过渡段） | 15/15/14/4/7/6/7/5/5 + 段共 41 帧 | 说话（P10 补 VPet `Say/Shy` 害羞档；组①·过渡段：`say-{感情}-a/c` 与站姿衔接。**2026-09-24 动画组E**：补 5 条 B 变体、主名 `smile`（Shining/B_2）改名 `shining`——同感情统一前缀；同感情共用一份感情级 a/c 段）|
| `work` | pc / read / write / calligraphy / paint / study2 / sausage / clean / fixmenu / game / water / remove / rope（Nomal）＋ happy-\* / poor-\*{同类型}（Happy/PoorCondition 档）| 共 1369 | 干活（VPet `WORK` 全部 13 种：书法/画画/研究/烤肠/清屏/修屏幕/玩游戏/玩水/删错误/跳绳）。**2026-09-20 打磨**：改为**包裹段结构**（主段 = B 干活循环、取环内最丰富变体；A/C 拆 `-a`/`-c` 段）——原先只导 A 段，实机上「反复做准备动作、永远不干活」。**2026-09-22 重构#7**：补 Happy/PoorCondition 档（`{档}-{类型}` 命名，12/13 种有源；WorkTWO 无 Happy、Study 无档位 → 降级链兜底）。**2026-09-24 动画组I**：WORK 语义映射——Agent/命令通道指定「工作类型」→ 干活会话固定播对应素材（`工作类型映射` 10 键；详见 `script/State/README.md`）。金钱/体力收益等玩法数值不抄）|
| `sleep` | loop / happy（+ a/c 与 happy-a/c 过渡段） | 6+6 + 4/7/4/7 | 睡觉（循环）。2026-09-20 组①·过渡段：A=躺下入睡、C=醒来起身（首尾与站姿衔接）；主段 `sleep-loop` 的段用池级回退名 `sleep-a`/`sleep-c` |
| `greet` | {happy,nomal,poor}-1..3 | 共 157 | 打招呼（2026-09-24 动画组A **整池重构**：VPet `IDEL/Meow` 手敲屏幕 9 变体三档——主人口径「非常适合做问候语动画（因此最好不做空闲动画）」，命名 `{档}-{n}` 对齐 idle 池口径；旧 `amuse`/`meow` 两条已删，amusement_B 回归 fidget） |
| `interact` | a / b / c | 2/11/2 | 摸头反应**三段序列**（进入→保持→退出）|
| `interact` | happy-a / happy-b / happy-c | 3/12/2 | 摸头的高兴档（P10，VPet `Touch_Head/Happy`）—— 三档/心情=开心时序列**换档**（`interact-a` → `interact-happy-a`）|
| `interact_body` | a / b / c | 15/14/3 | **摸身体**反应三段（P10，VPet `Touch_Body/{A,B,C}_Happy/tb1`；官方只有 Happy/ill 两档，取 Happy）|
| `turn` | a / b / c | 3/15/4 | **被摸转身**（P10，VPet `Touch_Body/Happy_Turn`）—— 摸身体时 30% 概率改成她转身躲一下 |
| `switch` | up / down | 13/14 | **干活进出场过渡**（P10，VPet `Switch/Up·Down/Nomal`）—— `switch-up` 起身开工、`switch-down` 收工坐下 |
| `fidget` | 原项目 5（bubble/doze/meowlook/spin/yawning）+ VPet 6（squat/tennis/bubbles/boring/aside/amuse）+ 组① 1（happy520）| 共 287 | 待机小动作（P10 加 VPet 的 蹲/网球/泡泡/打呼噜/侧看；组①加比心彩蛋；**2026-09-20 打磨**：五个 VPet 变体补全 A（进入）/C（退出）段——原先只导单个 B 段、尾巴卡顿；tennis 8→44 帧（含「收拍放下站直」收尾）；`+` 追加模式导入，不清空原有变体。**重构#9**：State 坐下/躺下两个拼接变体删除 → 独立 `sit`/`lie` 池 + 嵌套会话。**2026-09-24 动画组A**：`meow` 移出（Meow 归 `greet`，不做空闲动画）、`IDEL/amusement_B` 回归作 `amuse`——它是循环动画，**动画组B 同日修正**：单段循环 2~5 次（`fidget单段循环`——退出帧=首帧、播完重播同名即无缝，一次 2.8~6.9 秒）；squat 的 B 循环调长（`fidget循环L覆盖`=4 ≈ 25 秒/次））|
| `sit` | {happy,nomal,poor}-a / -b1..b2 / -c | 共 75 | **坐卧长待机·坐下**（VPet `StateONE`，重构#9）：A 进入 → B 循环（每圈随机换一个 B 变体）→ C 起身；运行时由 CharAnim 的**坐卧嵌套会话**驱动（逻辑态仍在 idle，不是状态机状态）|
| `lie` | {happy,nomal,poor}-a / -b1..b2 / -c | 共 41 | **坐卧长待机·躺下**（VPet `StateTWO`，重构#9）：以 `1/(2+已躺次数)` 概率从 sit 嵌套进入，起身 C 播完**回 sit 的 B 判定**（可再躺）；素材含长停顿帧（单帧 1.5s 级）|
| `edge_hide` | {left,right}-{in,keep,hold,out,peek,rise,unpeek} | 共 76 | 贴边隐藏：`Main`=隐藏姿态序列（in/keep/hold/out），`Rise`=探出（A 弹出 → **B 探出后微动循环** → C 缩回）。左右**逐段一一对应**（每侧 in9/keep4/hold1/out7/peek4/rise10/unpeek3） |
| `pinch` | a / b / c | 1 / 6 / 21 | 捏脸（照 VPet 官方「**长按脸**」抄）：A 进入 → B **循环**（按住时连续播）→ C 松手退出。**只导 Nomal**——官方按它自己的 Mode 选 Happy/PoorCondition，我们的心情定义与官方不同 → 不做映射；官方的体力-2/心情+1 也不抄（捏脸只做动作） |
| `climb` | left-a/b/c / right-a/b/c | 3/4/3 ×2 | **智能移动·爬墙**（VPet `MOVE/climb.*`，重构#4 起由 MoveRunner 按移动表驱动）：A 扑向墙挂住 / B 手脚交替爬（循环，方向由窗口位移决定）/ C 脱手回站姿。官方吸附 = 窗口推出屏外（左 145 / 右 185 @Zoom1）→ 我们用「挂边可见比例」参数换算 |
| `climb_top` | left-a/b/c / right-a/b/c | 1/4/2 ×2 | **智能移动·顶边横爬**（`MOVE/climb.top.*`）：A 抓住顶边 / B 沿顶边爬（循环）/ C 离开。素材是横置构图（挂在顶边、身体垂在屏内） |
| `crawl` | left / right | 9 / 9 | **智能移动·趴行**（`MOVE/crawl.*`）：贴地慢爬；重构#4 起是移动表里的独立条目（VPet 同款，不再做「走动慢速变体」的随机替换）|
| `fall` | left-a/b/c / right-a/b/c | 4/8/21 ×2 | **智能移动·掉落**（`MOVE/fall.*`）：A 脱手 / B 横着下落（循环）/ C 落地起身（右版 21 帧长起身）。C_Nomal 混了两条命名序列（FLA 触地 + FLB 起身）→ 按前缀拆开拼接。重构#4：重力移动——触地即收尾进冷却 |
| `bday` | a / b / c | 4 / 45 / 4 | **生日彩蛋**（2026-09-20 组①）：A 惊喜 → B 开心摇摆（~5.6s）→ C 比心。触发 = `config/config.json` 的「生日」MM-dd 命中当天 → 入场完成后播一遍 |
| `music` | a / c / {nomal,happy,poor}-1..n / single-{档} | 1 / 6 / 16~30 / 14 | **组③ 音乐反应**（VPet `Music/*`）：A 起跳 → 舞蹈循环（三档，Happy>Nomal>Poor 欢快度，带音符特效）→ C 收尾；`single-*` = 嗨档（音量超刺激阈值时 MusicSense 显式指定，不进普通随机）。运行时 = 包裹段（music 已入包裹池）|
| `enter` | happy-1 / happy-2 / nomal / poor | 14 / 10 / 14 / 18 | **登场**（2026-09-24 动画组G **整池重导**：VPet `StartUP` 除 Ill 全档——happy-1=Happy、happy-2=Happy_1、nomal=Nomal、poor=PoorCondition；newyear 节日皮肤暂缓。**固定画布映射**池。旧 `enter/1-2`（原项目遗留、未基线对齐）已删。选名 = `挑登场退场`（挑主名/降级链；三档关闭钉 nomal）。注：源 11 帧里 `_001` 是 LA 落地扬尘特效帧，按「丢遮罩层」规则丢弃（与 `fidget-tennis-c` 同口径））|
| `exit` | happy-1 / nomal-1 / poor-1 / happy-2 / nomal-2 / nomal-3 / poor-2 | 32 / 32 / 32 / 15 / 12 / 12 / 12 | **退场**（动画组G **整池重导**：VPet `Shutdown` 除 Ill 全档——happy-1=2/Happy、nomal-1=2/Nomal、poor-1=2/Poor、happy-2=Happy_1、nomal-2=Nomal_1、nomal-3=Nomal_2、poor-2=Poor；**固定画布映射**池。旧 `exit/1-4` 已删。故障方块擦除 + 收尾星闪；末帧全透明。源 4 个 1bit 空白帧（`_032`/`_012`）按「丢遮罩层」规则丢弃）|

| `drag` | 1 / nomal-1..2 / poor | 22 / 8 / 11 / 11 | **拖拽动态**（VPet `Raise/Raised_Dynamic`，动画组H 补三档：nomal-1 摇晃 / nomal-2 狗刨 / poor；旧 `1`=Happy 保留作开心档的无档基名落点）。**原画布口径**（见导入规则表）|
| `draghold` | {happy,nomal,poor} 各 a/b/c（+ happy-c2） | 6/6/23/21 + 6/5/21 + 3/9/21 = 121 | **拖拽静态挂起**（VPet `Raise/Raised_Static`，动画组H）：拎起满「拖拽静止秒」→ A 拎定过渡 → B 循环挂起；松手 → c 放下落地回 idle（`c2`=C_Happy_2 英雄落地，原 `dragdown/2` 迁移；C 段 FLA+FLB 拆片拼接）。挂起会话见 `script/UX/README.md`，验证 `tests/DragHoldProbe` |

**验证**：`tests/PoolProbe`（各池真的播出对应动画）+ `tests/EdgeHideProbe`（12 段全部载入可播）+ `tests/BirthdayProbe`（生日命中 → 三段 → 回 idle）+ `tests/WrapProbe` / `MoveProbe` / `MusicProbe`（组①~③ 机制专测，其中组② 智能移动 = MoveProbe）。
