# 回归探针 —— 说明文档

> 本文件是 **`AIPet-Agent.md` 的分册**。AIPet-Agent.md 是总纲（项目定位 / 架构总览 / 目录地图 / 路线图 / 开发约定 / 已知事项），
> **本层的细节与踩坑在这里**。相关代码：`tests/`；改动后请跑 `tests/README.md` 里对应的探针。
> 约定：标识符英文，注释中文。

### 踩坑 #6

6. **headless 下窗口几何不可信**。`--headless` 运行时窗口 `Size` 会被 MinSize/0 尺寸屏幕带偏（实测 `470x224` 报成 `360x180`、位置为负）。→ 规则：**几何/视觉相关的验证必须非 headless 运行**。
   - 取证手段：`Window` 本身是 Viewport，可用 `窗口.GetTexture().GetImage().SavePng(绝对路径)` **把窗口画面存成 PNG**，再做视觉复核（探针范例：`tests/SettingsProbe.cs`）。
---

### 踩坑 #15

15. **探针要隔离它不测的那一层，清理也要隔离**。历史教训：情绪变体曾按心情改播（P5 时期的耦合，2026-09-20 已按主人决定移除），
    `PoolProbe` 的「池内随机」断言会随存档心情**随机失败** → 探针要把不测的那层**钉死**再恢复；
    **存盘/删除必须走临时路径**（`StatsTable.探针_覆盖存盘路径`）—— 曾因 `File.Delete(真实存档路径)`
    把主人的 `stats.json` **每次回归删一次**（2026-09-20 修）。
---

### 踩坑 #17

17. **断言现实世界的读数时，别假设自己（后台进程）会影响那个世界**。`EnvProbe` 首版断言「探针刚跑过，
    系统空闲秒应该很小」，实测读到 **413.6 秒**——探针跑在后台**不算**用户输入，**读数是对的、断言是错的**。
    → 改成验证「两次读数构成活的时钟」（前进=无人操作，归零=刚有输入）。
---

### 踩坑 #18

18. **边界值先看清是「<」还是「≤」再写断言**。历史例子：曾有「mood < 30 算低落」的严格小于判断，探针用 `mood=30` 正好踩在界上，
    概览文案没切到「低落」分支 → 断言失败。凡是拿阈值数字当测试输入，先确认边界方向，或干脆避开设成 ±1。

### 踩坑 #27

27. **跑回归前看一眼 `user://` 有没有「用户覆盖」文件**。`ConfigFile.候选` 的读取顺序里 `user://` **优先于**仓库 `config/`——
    在 `user://` 放一份同名 JSON（如主人实机联调用的临时 `user://behavior.json`）会**静默压过**仓库配置，
    「配置读到了 X」类断言按覆盖档取值 → **随机假失败**（实测：RoutineProbe 的「问候启用」被临时联调档的 `false` 坑过一整轮全量回归）。
    → 规则：断言"配置值"的探针先判 `user://` 覆盖是否存在，**存在就打印 SKIP 说明原因、不判 FAIL**（覆盖优先是设计行为，不是故障）；
    全量回归若只有这类配置断言挂掉，先查 `user://`，再查代码。

### 踩坑 #28

28. **探针喂键盘：「PushInput」和「ParseInputEvent」不是一回事**。实测（临时场景 A/B 对照，`InputEventKey` 空格按下）：
    `get_viewport().push_input(ev)` 与 `window.push_input(ev)` 之后，`Input.is_physical_key_pressed(SPACE)` **都是 false**——
    push_input 只把事件塞进"那一条视口"的事件管线（喂 `_input` / GUI 回调），**不更新全局输入状态**；
    `Input.parse_input_event(ev)` 之后才是 true（再注释放事件回 false）。
    → 规则：**被测方用轮询（`Input.IsPhysicalKeyPressed` / `IsActionPressed`）判定时，探针必须用 `Input.ParseInputEvent` 喂**；
    只用 PushInput 会出现"探针以为按了、程序纹丝不动"（`GameEntryDialog` 的空格确认靠 ParseInputEvent 跑通全链路）。

### 踩坑 #29

29. **探针跑在哪棵树上：`run_probes.sh` 的项目根 = 调用时的 CWD（相对路径调用时）**。`tools/run_probes.sh` 用脚本自身路径的上一级推项目根——**相对路径调用（`bash tools/run_probes.sh`）时 = 你 cd 到的那棵树**。多棵树（主树 / worktree）并行跑探针时，日志按启动时间戳并列在同一个 `aipet_probes/` 下——**用日志里 `[PhraseTable] 载入 <路径>` 认领归属**（曾把并行会话在主树上的失败误当本树回归，白查一轮）。
   - 规矩：**先 `cd <项目根>` 再 `bash tools/run_probes.sh`**；结论只从「载入路径 = 本树」的日志里读。

### 踩坑 #30

30. **机器在放声音时，MusicSense 会抢状态——触摸类探针可能假红**。实测（2026-09-22）：`TouchProbe` 在「摸身体 → interact_body」等待期间，系统放音乐被 `[MusicSense] 识别到音乐（峰值 0.33，嗨档 → Single）→ 起跳` 抢走状态 → 挂 2 条（「摸身体没在 8 秒内进入身体反应（当前 music）」「过渡到点回 idle（music）」）；**隔离复跑 0 失败**（音乐已停）。
   - 规矩：状态/触摸类探针的红，**先看日志里有没有 `[MusicSense]` 行**再定性；跑全量回归前尽量别放音乐，或对失败项隔离复跑一次。

**视觉复核通道**：本机 `auxiliary.vision` 可用（模型已支持图片输入）。截图 + 视觉复核是 UI 改动的一等验证手段，不要只靠 headless 断言。

### 踩坑 #29

29. **全量回归是「串行共享 `user://` 存档」的 —— 时间驱动日程会在单跑里看不见、在全量里抢状态**（实测：`MoveProbe` 的「收步回 idle」被**每日问候**顶掉 → 单跑绿、全量挂，顺序相关抖动）。
    迟到查因：`StateMachine.入场完成()` 会排上**启动问候**（`DailyRoutine.启动问候()`），兑现时机取决于跑第几个探针（跑在自己门口 / 跑在 12s 前后差别很大）。
    → 规则：**时序敏感探针在实例化场景「之前」置 `StateMachine.设置.探针_冻结时间驱动开关 = true`** —— `设置.加载()` 里会强制关掉 问候 / 磁盘提醒 / 音乐检测（只关时间驱动的三项，数值/节律照常），比逐项写四行开关可靠（旧写法会被随后 `behavior.json` 注入覆盖）。
    → 另一处同类坑：探针钩子要放在**概率骰之前**（`if (探针_接力目标 != null || 接力掷骰(...))`），否则 0.8 概率下 20% 的跑次会翻车 —— 探针必须能把概率路径**绕成确定性**。
---

## 10.1 探针清单（改到相关代码就跑对应的那个）

| 探针 | 跑法 | 验证什么 |
|---|---|---|
| `PanelProbe` | headless | 命令栏 / 配置窗 / 状态机基本态 |
| `ToolBarProbe` | headless | 工具栏：弹出/关闭 + mod 工具格（「网速监控」·开/关状态、旧工具按钮接线）+ 点击开/关气泡 + 启动恢复 + 指令查找（**不触发副作用**）|
| `WindowProbe` | headless | 桌宠窗口几何不漂移 |
| `StateProbe` | headless | 状态效果表 / 状态锁 / 排队与作废 / 保持与兜底 / 入场门 / 退出保护 / 空闲与不打扰 |
| `SettingsProbe` | **非 headless** | 配置窗真实几何 + 把窗口画面存 PNG 供视觉复核 |
| `EnterProbe` | **非 headless** | 抓启动头几秒的窗口画面，核实「登场动画有没有播、有没有被抢断」 |
| `WalkProbe` | **非 headless** | 自主走动是否真的触发、窗口 X 是否真的移动（**必须非 headless**：headless 下屏幕/窗口尺寸为 0，`尝试走动` 会直接放弃） |
| `PoolProbe` | headless | 6 个语义池是否真的播出对应动画（断言没有回退到兼容池 fidget/idle/celerate）+ 新素材存在性核对（P10 + 2026-09-20 组①，共 40 个动画） |
| `BirthdayProbe` | headless | 生日彩蛋全链路：把「生日」覆写成今天（不碰真实 config）→ 入场完成后自动进 `bday` 三段序列 → 逐段推进（模拟播完）→ 回 idle |
| `WrapProbe` | headless | 包裹段机制（组①）：think/sleep/说话 进入先播 A → 主段钉死不换 → 退出先播 C、状态延迟落地；硬接管（拖拽）跳过 C 立刻生效 |
| `MoveProbe` | headless | 智能移动（重构#4，VPet Move 模型）：纯函数几何（挂边/顶挂/落地 X/Y、距、距离骰/接力骰）+ 移动表加载/档位过滤 + 触发·检查近远门 + 冷却只挡爬边族 + 兼容接力方向评分 + 全流程（上墙→吸附 X=-123→爬→顶爬 Y=-116→角上接力→下落→落地 Y=790→回位 X=1664→收势→idle+冷却）+ 下爬 junction（近底 240）+ 「屏外让位拉回」 |
| `SitProbe` | headless | 坐卧嵌套会话（重构#9，VPet StateONE/StateTWO）：档名/B 变体列表/四键加载 + sit A→B 循环（退出骰不中续圈）→ 命中后进 lie（次数 +1、圈数清零）→ lie A→B→C 起身 → **回 sit 的 B 判定** → 收场 C → idle + 接管作废 + 分派集成（全槽坐卧必中 / 坐卧关闭不中）+ 槽位分布（移动 3 / 坐卧 2 / 其余无效）+ 掷骰≡槽位 |
| `MusicProbe` | headless | 音乐反应（组③）：阈值纯函数 + `探针_峰值覆写` 注入假音量：持续有声 → 起跳（A）→ 舞蹈档随机（排除 single-*）→ 静音 → 收场（C、延迟落地）→ idle；再以 0.6 音量验证嗨档换 `music-single-*` |
| `InteractProbe` | headless | 摸摸反应是否**完整播放三段序列**（a 进入 → b 保持 → c 退出 → 回待机），且各段耗时与素材原时长一致 |
| `HistoryProbe` | headless | 会话恢复回放的历史是否被切成**独立消息**（不再合并成一大段）。会真实连接 Agent |
| `DragProbe` | **非 headless** | 桌宠贴近**屏幕上边缘**时仍能起手拖拽、窗口精确跟随光标（真实光标 + 注入按键；结束会恢复窗口与光标位置） |
| `CommandProbe` | headless | 指令通道三组：解析（未闭合块/非 pet 围栏不误伤/坏 JSON 容忍）、执行（越权与超限的拒绝）、显示（跨 chunk 拼接不漏围栏） |
| `ToolChannelProbe` | headless | 工具通道（收件箱协议）：MCP `pet_command` → `actions.jsonl` → `ActionInbox` 执行 + 回执；含「不吃旧账」/越权拒绝/坏 JSON 容忍 |
| `CommandE2E` | headless | **真实 Agent** 下发指令 → 解析 → 执行 → 回复与历史都无残留。会真实调用一次 LLM |
| `StatsProbe` | headless | 主人情绪读数：衰减（每 30s 向中性回 5 点）/夹取/存盘 schema/**离线补算**/`set_mood` 接线（结束恢复数值并清理**临时**存档；真实档隔离 —— 修过「跑回归删主人真实数值档」的 bug）|
| `ContextProbe` | headless | 上下文接口（`user://context.md`）：组装内容（含「不含人格段」反向断言）/ 脚手架只建不覆盖 / 只读不推送 |
| `DegradeProbe` | headless | 降级路径：配了 Agent 起不来 → 提醒主人（气泡人话+事件池）；没配 Agent = 本地模式不打扰 |
| `NetSpeedProbe` | headless（`-- hold` 可保持显示供外部截屏）| 网速桌面气泡：格式化/采样差分/文本与历史/位置+开关状态持久化/懒创建（位置断言在 headless 下跳过；配置走临时文件）|
| `TtsProbe` | headless | 语音输出：配置/清洗/门控/系统语音枚举与挑选/**真合成到 WAV**/与气泡联动（探针不发声）|
| `EventProbe` | headless | 行为事件：事件池读写/ack/保留策略/隐私字段 + 久坐提醒触发与冷却 + 升级为 Agent 事件 + 进上下文接口 |
| `EnvProbe` | headless | 环境感知：默认关得住、真实 Win32 读数（活的时钟）、全屏闸门、离开/回来边沿与节流 |
| `StatsWindowProbe` | **非 headless** | 状态窗（现为隐藏界面、无入口）：命令栏入口已撤（反向断言）+「游戏模式」入口在位 → 程序唤出 → **一个数字都不出现** → 文字状态随 mood 变化（并截图供视觉复核）|
| `RoutineProbe` | headless | 时间驱动主动行为：启动问候（每次启动一次）/ 磁盘余量低（每天一次、闸门关着先记下补报）/ 预算与不打扰 |
| `PhraseProbe` | headless | 本地话语表 `config/phrases.json`：分类齐全 / 随机不连重 / `{盘}{余量}` 占位符 / 坏数据内置兜底 |
| `ConfigEditProbe` | headless | 配置写回三约定：只改目标键 / 保留 `_comment` 与未知键 / 中文不转义（写临时目录，不碰真配置）|
| `EdgeHideProbe` | headless | 贴边素材：12 段是否真的载入可播、帧数与左右对应关系 |
| `EdgeHideBehaviorProbe` | **非 headless** | 贴边行为：吸附触发 / 隐藏与探出比例 / **循环节拍（每 2 秒 2 次）** / 探出用 `-rise`（要真窗口几何与光标）|
| `PinchProbe` | headless | 捏脸：命中区纯函数 / 长按阈值 / 三段流转（A→B 循环→松手 C）/ 被拖拽抢走作废 / 贴边时不捏 / 不产生数值变化 |
| `BubbleProbe` | headless | 气泡：说话动作（可打断 + 忙态/流式不偷）/ 时长到点自动收 / **按内容自适应尺寸** / 字号 / 鼠标穿透 / BBCode 转义 |
| `BubbleShot` | **非 headless** | 气泡实机：把气泡窗渲成 PNG（供视觉复核）+ 位置数值断言（头顶居中 / 上方放不下自动翻下方）|
| `ChatBoxShot` | **非 headless** | 聊天面板命令栏：「游戏模式」入口在位 / 无「状态」「关闭」；「退出桌宠」红底 + 关机图标 + 贴最右（面板渲 PNG 供视觉复核）|
| `GameProbe` | **非 headless** | 游戏模式全链路（92 断言）：弹窗（Esc 取消 / 空格确认）→ 挂载（全屏换壳 / 面板收起 / 精灵 Reparent / 相机接管 / **鼠标穿透** B11 / 聚焦 B12）→ 最小可玩（着地 / 行走 / 跳跃（上升期不播 fall + 播起跳占位 C5e2、过顶播 `fall-B`、即时转身）/ 相机跟随 + **人物屏幕中下** C9b / 影子留地面）→ 手感三件套（土狼 / 缓冲 / 可变跳高，F1~F3）→ **失焦**（不响应 C5f / 变淡 C5g~C5h / 恢复 C5i）→ **M 键鼠标穿透切换 + 写回配置**（B13~B16）→ **X 攻击 + 键位改版**（真实键 X 进入攻击 G1 / 请求 attack-right G2 / 池登记 G3 / 占位 fidget-tennis G3b / 冷却不刷新 G4；真实方向键 ← G5 + 走动画 G5b；朝左请求 attack-left G6/G6b）→ 办公星钩子（干完一次活 +1）→ 二进宫玩法（收集回血 + 存档去重 / 掉落扣血 / 血空「玩累了」回满血**不退出** E13~E15 / 星摆位不变量 E0）→ 退出还原（窗口几何 / 精灵 / 状态机 / 鼠标交还 D9、Esc 退出 E16~E18）→ 两帧 PNG 供视觉复核 |
| `AnimShot` | **非 headless** | 新导入动画实机渲图：逐个播并各存一张 PNG（缩放/落地线只靠数值不够，得看得见）|
| `AudioProbe` | headless | 音频输入设备诊断（列出 Godot 能看到的麦克风）|
| `TouchProbe` | headless | P10：命中区纯函数（脸/身体不重叠）/ 摸摸部位分流（**排队**等反应，不能定帧断言）/ 三档状态四种情形 / 干活进出场四步（`switch-up` → working → `switch-down` → idle）|

**踩坑：验节律必须在场景实例化「之前」写配置。** `StateMachine._Ready` 会读 `user://behavior.json` 并用它算好 `_走动倒计时`；之后再改内存里的 `设置` 字段已经晚了（探针曾因此在 20s 内一次走动都触发不了）。`WalkProbe` 的做法：`_Ready` 里先写临时 `user://behavior.json` → 再实例化场景 → 结束时删除。

---

## 10.2 一键回归：`tools/run_probes.sh` / `tools/run_probes.ps1`

一次把探针全跑一遍（默认只跑 headless 的），逐个打印「名字 / 结果 / 耗时」，最后给汇总表；**任一失败 → 脚本退出码非 0**（可以直接串进别的脚本）。

| 用途 | bash（git-bash / MSYS） | PowerShell（没装 bash 的机器） |
|---|---|---|
| 默认：所有 headless 探针 | `bash tools/run_probes.sh` | `powershell -NoProfile -ExecutionPolicy Bypass -File tools\run_probes.ps1` |
| 先看清单与分类（不跑，安全） | `bash tools/run_probes.sh --list` | `... -File tools\run_probes.ps1 -List` |
| 含非 headless（会弹窗、动光标） | `bash tools/run_probes.sh --all` | `... -File tools\run_probes.ps1 -All` |
| 只跑单个 / 几个 | `--only PanelProbe,StateProbe` | `-Only PanelProbe,StateProbe` |
| **额外**跑需要真 Agent 的探针（默认不跑） | `--agent` | `-Agent` |
| 单个探针超时秒数（默认 180） | `--timeout 300` | `-Timeout 300` |
| 透传给探针的参数 | `--only SessionProbe -- -- read` | `-Only SessionProbe -ProbeArgs read` |
| 指定 Godot 可执行文件 / 日志目录 | `--godot <exe> --logs <dir>` | `-Godot <exe> -Logs <dir>` |

**判定规则**：退出码 0 = 通过、非 0 = 失败；日志里 `FAIL  `（FAIL + 两个空格）行数 = 失败断言数；**通过 = 退出码 0 且 0 条 FAIL**。脚本自己的退出码：`0` 全过 / `1` 有失败 / `2` 用法错 / `3` 环境错（找不到 Godot、找不到 `tests/`）。

**别在回归运行中改 `run_probes.sh`**：bash 按**字节偏移**增量读脚本文件，边跑边改会让它从错位处继续读 → 报假语法错（实测：跑完 24 个探针后收尾处 `syntax error near unexpected token`，`bash -n` 却是干净的）。要改先等跑完，或改完重跑。

**headless 与非 headless 的区别**（清单由脚本扫 `tests/*.tscn` 得到，新增探针不用改脚本；`--list` 里的清单是权威版，10.1 表里还没登记的探针也会列出来）：

- **默认只跑 headless 的**：不弹窗、可以后台/连着跑，适合每次改完代码的快检。
- **非 headless 白名单默认跳过**（当前 10 个）：`DragProbe` / `SettingsProbe` / `StatsWindowProbe` / `EdgeHideBehaviorProbe` / `EnterProbe` / `WalkProbe` / `BubbleShot` / `AnimShot` / `ChatBoxShot` / `GameProbe`——它们要**真实窗口 / 真实光标 / 真实渲染**，会真弹窗、真动光标，只有 `--all` 或 `--only <名字>` 才跑。`WalkProbe` 是被踩坑赶进来的：headless 下屏幕与窗口尺寸为 0，`尝试走动` 会直接放弃（跑了也是**假绿**）。
- 名单在脚本顶部可改：`.sh` 的 `NON_HEADLESS`、`.ps1` 的 `$NonHeadless`（加进去的名字 = 默认不跑）。
- **必须串行**：探针共用 `user://` 存档（行为节律、数值…），脚本不并行跑。

**超时怎么看**：单个探针跑过 `--timeout`（默认 180s）→ 记 `TIMEOUT`、按失败计（退出码 124），并把**整棵进程树**杀掉，再按「命令行里含 `res://tests/<本探针>.tscn`」精确补一刀（只撞本探针的 Godot 进程，不会碰编辑器 / 别的项目）。两步都要的原因：`*_console.exe` 只是个启动器，只杀它会留一个孤儿窗口；MSYS 下 `taskkill /T` 还可能漏杀（子进程被重新挂父）。

- **默认不跑**：真连 Agent（LLM）的 `HistoryProbe` / `CommandE2E` / `AcpTest` / `ChatFlowTest` / `SessionProbe`（`--list` 里标了 `[需 Agent]`）——真调模型受网络/环境延迟影响、不稳定，已从默认回归摘除；要手动验证用 `--agent`（慢时配 `--timeout 300`）。
- **失败时看什么**：脚本会把该探针的 `FAIL` 行直接打在结果下面（一条 FAIL 行都没有，就贴日志末尾 8 行）；每个探针的完整日志在汇总下面的「失败日志」里给路径，默认落在系统临时目录（`aipet_probes/<时间戳>/`）。
- **找不到 Godot**：脚本**一开始就报错退出**（`rc=3`，并提示改脚本顶部的 `GODOT_EXE` / `$DefaultGodot`，或用 `--godot` / `-Godot` / 环境变量 `GODOT_EXE` 指定），不会每个探针都炸一遍。

**`SessionProbe` 是两趟**（第一趟设暗号 → 第二趟问暗号）：单跑一次只完成第一趟，要验「记忆延续」得按上表的透传写法跑第二趟。

**改 `.ps1` 的注意**：内容是**纯 ASCII + 英文注释/输出**——本机 PowerShell 5.1 读无 BOM 的 `.ps1` 按 ANSI 解析，写中文注释会解析失败。
