# 回归探针 —— 说明文档

> 本文件是 **`AGENTS.md` 的分册**。AGENTS.md 是总纲（项目定位 / 架构总览 / 目录地图 / 路线图 / 开发约定 / 已知事项），
> **本层的细节与踩坑在这里**。相关代码：`tests/`；改动后请跑 `tests/README.md` 里对应的探针。
> 约定：标识符英文，注释中文。

### 踩坑 #6

6. **headless 下窗口几何不可信**。`--headless` 运行时窗口 `Size` 会被 MinSize/0 尺寸屏幕带偏（实测 `470x224` 报成 `360x180`、位置为负）。→ 规则：**几何/视觉相关的验证必须非 headless 运行**。
   - 取证手段：`Window` 本身是 Viewport，可用 `窗口.GetTexture().GetImage().SavePng(绝对路径)` **把窗口画面存成 PNG**，再做视觉复核（探针范例：`tests/SettingsProbe.cs`）。
---

### 踩坑 #15

15. **探针要隔离它不测的那一层**。情绪变体（P5）会按心情改播 `think-happy/poor`，于是 `PoolProbe` 里
    「池内随机」类断言会随主人存档心情**随机失败**。→ 探针先把自己不测的那层状态**钉死**（如心情钉中位 60），结束再恢复。
---

### 踩坑 #17

17. **断言现实世界的读数时，别假设自己（后台进程）会影响那个世界**。`EnvProbe` 首版断言「探针刚跑过，
    系统空闲秒应该很小」，实测读到 **413.6 秒**——探针跑在后台**不算**用户输入，**读数是对的、断言是错的**。
    → 改成验证「两次读数构成活的时钟」（前进=无人操作，归零=刚有输入）。
---

### 踩坑 #18

18. **边界值先看清是「<」还是「≤」再写断言**。`心情低落 = mood < 30` 是严格小于，探针用 `mood=30` 正好踩在界上，
    概览文案没切到「低落」分支 → 断言失败。凡是拿阈值数字当测试输入，先确认边界方向，或干脆避开设成 ±1。

**视觉复核通道**：本机 `auxiliary.vision` 可用（模型已支持图片输入）。截图 + 视觉复核是 UI 改动的一等验证手段，不要只靠 headless 断言。
---

## ### 10.1 探针清单（改到相关代码就跑对应的那个）

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
| `ContextProbe` | headless | 上下文接口（`user://context.md`）：组装内容 / 脚手架只建不覆盖 / 只读不推送 |
| `DegradeProbe` | headless | 降级路径：配了 Agent 起不来 → 提醒主人（气泡人话+事件池）；没配 Agent = 本地模式不打扰 |
| `TtsProbe` | headless | 语音输出：配置/清洗/门控/系统语音枚举与挑选/**真合成到 WAV**/与气泡联动（探针不发声）|
| `EventProbe` | headless | 行为事件：事件池读写/ack/保留策略/隐私字段 + 久坐提醒触发与冷却 + 升级为 Agent 事件 + 进上下文接口 |
| `MoodProbe` | headless | 数值驱动表达：情绪变体择档（think-happy/poor）+ 行为耦合（走动倍率、睡眠阈值）+ 无变体池的降级安全 |
| `EnvProbe` | headless | 环境感知：默认关得住、真实 Win32 读数（活的时钟）、全屏闸门、离开/回来边沿与节流 |
| `StatsWindowProbe` | **非 headless** | 状态窗：命令栏入口存在 → 点击弹出 → **一个数字都不出现** → 文字状态随 mood 变化（并截图供视觉复核） |

**踩坑：验节律必须在场景实例化「之前」写配置。** `StateMachine._Ready` 会读 `user://behavior.json` 并用它算好 `_走动倒计时`；之后再改内存里的 `设置` 字段已经晚了（探针曾因此在 20s 内一次走动都触发不了）。`WalkProbe` 的做法：`_Ready` 里先写临时 `user://behavior.json` → 再实例化场景 → 结束时删除。

---

## ### 10.2 一键回归：`tools/run_probes.sh` / `tools/run_probes.ps1`

一次把探针全跑一遍（默认只跑 headless 的），逐个打印「名字 / 结果 / 耗时」，最后给汇总表；**任一失败 → 脚本退出码非 0**（可以直接串进别的脚本）。

| 用途 | bash（git-bash / MSYS） | PowerShell（没装 bash 的机器） |
|---|---|---|
| 默认：所有 headless 探针 | `bash tools/run_probes.sh` | `powershell -NoProfile -ExecutionPolicy Bypass -File tools\run_probes.ps1` |
| 先看清单与分类（不跑，安全） | `bash tools/run_probes.sh --list` | `... -File tools\run_probes.ps1 -List` |
| 含非 headless（会弹窗、动光标） | `bash tools/run_probes.sh --all` | `... -File tools\run_probes.ps1 -All` |
| 只跑单个 / 几个 | `--only PanelProbe,StateProbe` | `-Only PanelProbe,StateProbe` |
| 跳过需要真 Agent 的探针 | `--no-agent` | `-NoAgent` |
| 单个探针超时秒数（默认 180） | `--timeout 300` | `-Timeout 300` |
| 透传给探针的参数 | `--only SessionProbe -- -- read` | `-Only SessionProbe -ProbeArgs read` |
| 指定 Godot 可执行文件 / 日志目录 | `--godot <exe> --logs <dir>` | `-Godot <exe> -Logs <dir>` |

**判定规则**：退出码 0 = 通过、非 0 = 失败；日志里 `FAIL  `（FAIL + 两个空格）行数 = 失败断言数；**通过 = 退出码 0 且 0 条 FAIL**。脚本自己的退出码：`0` 全过 / `1` 有失败 / `2` 用法错 / `3` 环境错（找不到 Godot、找不到 `tests/`）。

**headless 与非 headless 的区别**（清单由脚本扫 `tests/*.tscn` 得到，新增探针不用改脚本；`--list` 里的清单是权威版，10.1 表里还没登记的探针也会列出来）：

- **默认只跑 headless 的**：不弹窗、可以后台/连着跑，适合每次改完代码的快检。
- **非 headless 白名单默认跳过**：`DragProbe` / `SettingsProbe` / `StatsWindowProbe` / `EdgeHideBehaviorProbe` / `EnterProbe`，外加 `WalkProbe`——它们要**真实窗口 / 真实光标**，会真弹窗、真动光标，只有 `--all` 或 `--only <名字>` 才跑。`WalkProbe` 是被踩坑赶进来的：headless 下屏幕与窗口尺寸为 0，`尝试走动` 会直接放弃（跑了也是**假绿**）。
- 名单在脚本顶部可改：`.sh` 的 `NON_HEADLESS`、`.ps1` 的 `$NonHeadless`（加进去的名字 = 默认不跑）。
- **必须串行**：探针共用 `user://` 存档（行为节律、数值…），脚本不并行跑。

**超时怎么看**：单个探针跑过 `--timeout`（默认 180s）→ 记 `TIMEOUT`、按失败计（退出码 124），并把**整棵进程树**杀掉，再按「命令行里含 `res://tests/<本探针>.tscn`」精确补一刀（只撞本探针的 Godot 进程，不会碰编辑器 / 别的项目）。两步都要的原因：`*_console.exe` 只是个启动器，只杀它会留一个孤儿窗口；MSYS 下 `taskkill /T` 还可能漏杀（子进程被重新挂父）。

- **慢探针**：真连 Agent（LLM）的 `HistoryProbe` / `CommandE2E` / `AcpTest` / `ChatFlowTest` / `SessionProbe`（`--list` 里标了 `[需 Agent]`）——网络/机器慢就给 `--timeout 300`；没配 Agent 就用 `--no-agent` 跳过。
- **失败时看什么**：脚本会把该探针的 `FAIL` 行直接打在结果下面（一条 FAIL 行都没有，就贴日志末尾 8 行）；每个探针的完整日志在汇总下面的「失败日志」里给路径，默认落在系统临时目录（`aipet_probes/<时间戳>/`）。
- **找不到 Godot**：脚本**一开始就报错退出**（`rc=3`，并提示改脚本顶部的 `GODOT_EXE` / `$DefaultGodot`，或用 `--godot` / `-Godot` / 环境变量 `GODOT_EXE` 指定），不会每个探针都炸一遍。

**`SessionProbe` 是两趟**（第一趟设暗号 → 第二趟问暗号）：单跑一次只完成第一趟，要验「记忆延续」得按上表的透传写法跑第二趟。

**改 `.ps1` 的注意**：内容是**纯 ASCII + 英文注释/输出**——本机 PowerShell 5.1 读无 BOM 的 `.ps1` 按 ANSI 解析，写中文注释会解析失败。
