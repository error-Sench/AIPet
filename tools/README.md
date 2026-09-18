# 开发工具 —— 说明文档

> 本文件是 **`AGENTS.md` 的分册**（工具链部分）。
> **`tools/` 不参与游戏运行**——它是开发 / 构建 / 资产导入的工具箱；运行时要读的东西在 `config/`（配置）与 `mods/`（内容）。

## 目录

| 工具 | 用途 |
|---|---|
| `anim/import_vpet_anim.py` | **动画资产导入（通用）**：VPet → `mods/main_anim/anim/loris/<池>/` |
| `anim/import_vpet_walk.py` | 走动专用导入器（历史原因保留：它要按角色包围盒对齐基线） |
| `package.sh` | 打包：构建 → 导出 → 组装 `dist/AIPet-<版本>/` → 打 zip |
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
| `walk` | left / right | 6 + 6 | 走动（循环） |
| `think` | nomal / happy / poor | 9×3 | 三档状态（对接 P5 情绪变体） |
| `say` | smile / self / serious | 7/15/4 | 说话 |
| `work` | pc / read / write | 14/12/10 | 干活 |
| `sleep` | loop / happy | 6+6 | 睡觉（循环） |
| `greet` | amuse / meow | 11/20 | 打招呼（VPet 无专用动作，用开心姿势） |
| `interact` | a / b / c | 9/11/2 | 摸头反应**三段序列**（进入→保持→退出） |
| `edge_hide` | {left,right}-{in,keep,hold,out,peek,rise,unpeek} | 共 76 | 贴边隐藏：`Main`=隐藏姿态序列（in/keep/hold/out），`Rise`=探出（A 弹出 → **B 探出后微动循环** → C 缩回）。左右**逐段一一对应**（每侧 in9/keep4/hold1/out7/peek4/rise10/unpeek3） |

**验证**：`tests/PoolProbe`（各池真的播出对应动画）+ `tests/EdgeHideProbe`（12 段全部载入可播）。
