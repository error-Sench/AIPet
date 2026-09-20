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
| **按帧序号排序** | 帧名是 `<前缀>_<序号>_<时长>.png`；**必须按序号排**，不能按文件名 |
| **丢遮罩层** | `mode` 为 `1`/`L`/`LA` 的是灰度/1bit 遮罩，丢弃 |
| **节奏 `rate`** | 由帧时长推：`rate = 1000 / 模式时长`（单帧长保持帧的 rate 会很低，如 `2`） |
| **先看图再动手** | 导入前先拼对照图做视觉复核，确认语义（「A/B/C 是体态档还是动作三段」这种事**凭名字猜必错**，实测踩过两次） |

## 3. 踩坑

### 坑：一个目录里「多序列」的判据不能用文件名前缀

VPet 里真实存在**帧名拼写不一致**：`SideHide_Right_Main/Nomal/A/` 里 `A_000..A_013` 少了 `A_011`，
第 11 帧被命名成 **`A01_011`**。若按「前缀不同 = 混了两条序列」判，整段 `right-in` 会被跳过（丢素材）。

→ **规则：按帧序号判重复**——序号唯一就合并并按序号排序；序号重复才是真的混装（此时才跳过并要求人工拆目录）。

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
| `idle` | happy-1..3 / nomal-1..3 / poor-1..2 | 13×3 / 8×3 / 17×2 | 待机（VPet `Default` 三档；2026-09-20 组①补 Nomal/Poor 并对齐三档：变体名 = `{档}-{n}`，三档开启时按 `idle-{档}-` 前缀随机取一条）|
| `walk` | left / right / left-fast / right-fast / left-slow / right-slow | 6+6 / 10+10 / 5+5 | 走动（**循环**）。P10：**快/慢 = 心情档**（VPet `walk.*.faster` 就是 Happy、`walk.*.slow` 就是 PoorCondition），位移速度同步变（×1.35 / ×0.72，否则会滑步）|
| `think` | nomal / happy / poor（+各档 a/c 过渡段） | 9×3 + 2 帧×6 | 三档状态（对接 P5 情绪变体）。2026-09-20 组①·过渡段：`think-{档}-a` 进入 / `think-{档}-c` 退出（包裹段，见 `script/State/README.md`）|
| `say` | smile / self / serious / shy（+各感情 a/c 过渡段） | 7/15/4/5 + 段共 41 帧 | 说话（P10 补 VPet `Say/Shy` 害羞档；组①·过渡段：`say-{感情}-a/c` 与站姿衔接）|
| `work` | pc / read / write / calligraphy / paint / study2 / sausage / clean / fixmenu / game / water / remove / rope | 5~31 | 干活（P10 补齐 VPet `WORK` 全部 13 种：书法/画画/研究/烤肠/清屏/修屏幕/玩游戏/玩水/删错误/跳绳）。**只导 Nomal 段**，金钱/体力收益等玩法数值不抄）|
| `sleep` | loop / happy（+ a/c 与 happy-a/c 过渡段） | 6+6 + 4/7/4/7 | 睡觉（循环）。2026-09-20 组①·过渡段：A=躺下入睡、C=醒来起身（首尾与站姿衔接）；主段 `sleep-loop` 的段用池级回退名 `sleep-a`/`sleep-c` |
| `greet` | amuse / meow | 11/20 | 打招呼（VPet 无专用动作，用开心姿势） |
| `interact` | a / b / c | 2/11/2 | 摸头反应**三段序列**（进入→保持→退出）|
| `interact` | happy-a / happy-b / happy-c | 3/12/2 | 摸头的高兴档（P10，VPet `Touch_Head/Happy`）—— 三档/心情=开心时序列**换档**（`interact-a` → `interact-happy-a`）|
| `interact_body` | a / b / c | 15/14/3 | **摸身体**反应三段（P10，VPet `Touch_Body/{A,B,C}_Happy/tb1`；官方只有 Happy/ill 两档，取 Happy）|
| `turn` | a / b / c | 3/15/4 | **被摸转身**（P10，VPet `Touch_Body/Happy_Turn`）—— 摸身体时 30% 概率改成她转身躲一下 |
| `switch` | up / down | 13/14 | **干活进出场过渡**（P10，VPet `Switch/Up·Down/Nomal`）—— `switch-up` 起身开工、`switch-down` 收工坐下 |
| `fidget` | 原项目 6（bubble/doze/meow/meowlook/spin/yawning）+ VPet 5（squat/tennis/bubbles/boring/aside）+ 组① 3（state-one/state-two/happy520）| 共 325+ | 待机小动作（P10 加 VPet 的 蹲/网球/泡泡/打呼噜/侧看；2026-09-20 组①加 State 坐下/躺下待机与比心彩蛋；`+` 追加模式导入，不清空原有变体）|
| `edge_hide` | {left,right}-{in,keep,hold,out,peek,rise,unpeek} | 共 76 | 贴边隐藏：`Main`=隐藏姿态序列（in/keep/hold/out），`Rise`=探出（A 弹出 → **B 探出后微动循环** → C 缩回）。左右**逐段一一对应**（每侧 in9/keep4/hold1/out7/peek4/rise10/unpeek3） |
| `pinch` | a / b / c | 1 / 6 / 21 | 捏脸（照 VPet 官方「**长按脸**」抄）：A 进入 → B **循环**（按住时连续播）→ C 松手退出。**只导 Nomal**——官方按它自己的 Mode 选 Happy/PoorCondition，我们的心情定义与官方不同 → 不做映射；官方的体力-2/心情+1 也不抄（捏脸只做动作） |
| `climb` | left-a/b/c / right-a/b/c | 3/4/3 ×2 | **组② 爬边**（VPet `MOVE/climb.*`）：A 扑向墙挂住 / B 手脚交替爬（循环，方向由窗口位移决定）/ C 脱手回站姿。官方吸附 = 窗口推出屏外（左 145 / 右 185 @Zoom1）→ 我们用「挂边可见比例」参数换算 |
| `climb_top` | left-a/b/c / right-a/b/c | 1/4/2 ×2 | **组② 顶边横爬**（`MOVE/climb.top.*`）：A 抓住顶边 / B 沿顶边爬（循环）/ C 离开。素材是横置构图（挂在顶边、身体垂在屏内） |
| `crawl` | left / right | 9 / 9 | **组② 趴行**（`MOVE/crawl.*` 的 B 段）：贴地慢爬，当**走动的慢速变体**用（0.72 倍速）——不进行为链 |
| `fall` | left-a/b/c / right-a/b/c | 4/8/21 ×2 | **组② 掉落**（`MOVE/fall.*`）：A 脱手 / B 横着下落（循环）/ C 落地起身（右版 21 帧长起身）。C_Nomal 混了两条命名序列（FLA 触地 + FLB 起身）→ 按前缀拆开拼接 |
| `bday` | a / b / c | 4 / 45 / 4 | **生日彩蛋**（2026-09-20 组①）：A 惊喜 → B 开心摇摆（~5.6s）→ C 比心。触发 = `config/config.json` 的「生日」MM-dd 命中当天 → 入场完成后播一遍 |
| `music` | a / c / {nomal,happy,poor}-1..n / single-{档} | 1 / 6 / 16~30 / 14 | **组③ 音乐反应**（VPet `Music/*`）：A 起跳 → 舞蹈循环（三档，Happy>Nomal>Poor 欢快度，带音符特效）→ C 收尾；`single-*` = 嗨档（音量超刺激阈值时 MusicSense 显式指定，不进普通随机）。运行时 = 包裹段（music 已入包裹池）|

**验证**：`tests/PoolProbe`（各池真的播出对应动画）+ `tests/EdgeHideProbe`（12 段全部载入可播）+ `tests/BirthdayProbe`（生日命中 → 三段 → 回 idle）+ `tests/WrapProbe` / `ClimbProbe` / `MusicProbe`（组①~③ 机制专测）。
