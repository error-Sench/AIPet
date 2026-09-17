# 能力层（Agent 桥 / 协议 / 指令通道）—— 说明文档

> 本文件是 **`AGENTS.md` 的分册**。AGENTS.md 是总纲（项目定位 / 架构总览 / 目录地图 / 路线图 / 开发约定 / 已知事项），
> **本层的细节与踩坑在这里**。相关代码：`script/Agent/`；改动后请跑 `tests/README.md` 里对应的探针。
> 约定：标识符英文，注释中文。

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

**人格与数据的获取方式（已定稿，2026-09-16）**：**不做主动注入 —— Agent 自主读取。**

> 主人三次强调的硬规则：桌宠**不推送**人格/数值/记忆（不拼消息、不写插件钩子、不改 system prompt），
> 只把文件**放在磁盘上**；交付物是一份 **skill**，用户把它提交给自己的 Agent，Agent 按指引**自己来读**
> `soul.md` / `stats.json` / `profile.md` / `memory.jsonl`（路径表见 `script/Soul/README.md`）。

保留的技术事实（供选型参考，**不作为我们的方案**）：

| 通道 | 机制 | 备注 |
|---|---|---|
| SOUL.md | `$HERMES_HOME/SOUL.md` 存在即进 system prompt（`load_soul_md`） | Agent **自己**的机制，由用户自行决定是否使用 |
| Profile 隔离 | `hermes -p <profile>` 各有独立 HERMES_HOME | 别的用户可用；本机不碰主人 profile |
| 项目上下文文件 | `AGENTS.md`/`.hermes.md`/`CLAUDE.md` 按 cwd 进 system prompt | **打包时 cwd 须指向干净目录**（否则工程契约会进人格）|
| `pre_llm_call` 钩子 | 注入 user message | 我们**不用**（属主动注入）|
| ACP 协议 | `session/prompt` 只带用户文本，无 system prompt 通道 | 正因如此才走「文件 + 自主读取」|

**消息协议 v1（自定义 HTTP 兜底方案，保留备用）**：
- **桌宠 → Agent**（`POST /ask`）：
  ```jsonc
  { "type": "ask", "from": "user_input" /* 或 clipboard|voice|drag */,
    "text": "帮我查一下...", "soul": { /* 当前灵魂表快照，可选 */ } }
  ```
- **Agent → 桌宠**（响应）：`{ "reply": "用户可见回复文本", "commands": [ { "cmd": "set_state", "state": "interact" }, ... ] }`

**指令通道（下行面）：两条通道、一个执行口（✅ 工具通道 2026-09-17 落地）**：

ACP 的 `session/prompt` 响应只有 `stopReason`、没有自定义字段通道，所以下行这样分工：

1. **工具通道（首选）**：Agent 调 MCP 工具 **`pet_command`** → `aipet-mcp`（随包 stdio 服务）把请求写进
   `user://actions.jsonl` → `ActionInbox` 轮询读取、交 `PetCommands` 校验执行、把回执写回同一文件
   → 工具进程读回执返回给 Agent。**正文保持干净，Agent 拿到的是真实执行结果**（✓ / ✗ + 原因）。
2. **文本通道（兼容）**：Agent 在回复里内嵌围栏块 —— 没有 MCP 的 Agent（或注册前的过渡期）用这条：

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
- 工具通道**不吃旧账**：`ActionInbox` 启动时把读取偏移定位到文件末尾（桌宠不在时写下的指令不会事后诈尸）。
- 实现：`script/Agent/PetCommands.cs`（解析/校验/执行，两条通道共用）＋ `script/Agent/ActionInbox.cs`（工具通道收件箱）
  ＋ `AgentBridge.处理回复()`（文本通道末钩）＋ `ChatBox`（显示过滤）＋ `dist/aipet-mcp/aipet_mcp.py`（工具定义）。
- 验证：`CommandProbe`（合成文本）＋ `ToolChannelProbe`（收件箱协议：执行/回执/拒绝/不吃旧账）
  ＋ `CommandE2E`（真实 Agent 下发 → 执行 → 无残留）。

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
2. **实验性激进开关**（默认关）：`config/agent.json` 设 `"aggressiveMode": true` 可放宽指令白名单。
   **当前放宽范围：仅 `open_url`（限 http/https）**。⚠️ **刻意不实现**任何「执行指定本地命令」能力——
   那需要一个专门设计 + 主人明确授权，不应顺手开洞。任何进一步放宽必须记录在此文档。
   （旧文档写的 `config.json` 的 `agent.aggressive_mode` 与实现不符，已按实况更正。）

---
---

### 踩坑 #9

9. **`OS.Execute` 不支持双向流**。拉起 `hermes acp` 这类需要双向 stdio 的子进程必须用 `System.Diagnostics.Process`（见 §6）。
---

### 踩坑 #10

10. **`hermes` 的会话列表会被测试污染**。端到端 ACP 测试每跑一次就真实新建/复用一条会话。→ 规则：纯 UI 改动只跑不碰 Agent 的探针（`PanelProbe` / `ToolBarProbe` / `WindowProbe`）；只有改到 Agent 链路时才跑 `ChatFlowTest`，且跑完清理。
---

### 踩坑 #11

11. **`session/resume` 会回放整段历史，必须与实时回复分流**。恢复会话时 Agent 先把历史以 `user_message_chunk` / `agent_message_chunk` 成对回放（每条历史 = **一个** chunk），然后才返回 resume 响应。若客户端把所有 `agent_message_chunk` 都当成「本轮实时流式」，UI 会把**多轮历史累加成一个缓冲区 → 合并成一大段**（实测：`好`+`西瓜`+`连通` → `好西瓜连通`）。→ 规则：用「本轮 prompt 是否进行中」判定（`AcpClient._提示中`，在 `Ask` 发送前置位、收到 prompt 响应后清除）；历史块走独立事件 `OnHistoryChunk`，UI 作为**独立消息**追加。
    - 附带结论：`agent_thought_chunk`（思考流）与 `usage_update` / `available_commands_update` 都应**不显示**（当前只显示 `agent_message_chunk` 与历史用的 `user_message_chunk`）。
---

### 踩坑 #13

13. **指令通道有三处易踩的坑**（都由探针实测抓出）：
    - **显示路径必须静默解析**：流式每来一个 chunk 都会整段重解析，此时块内 JSON 常是**半截的** →
      若照常报错会刷一屏「JSON 行解析失败」。→ `解析(原文, 记日志:false)`。
    - **历史回放路径也要过滤**：hermes 存的是 Agent **原始**回复（含围栏块），`session/resume` 回放时
      不过滤就会在历史气泡里露出一堆围栏块（实测 4 条历史助手消息全带围栏）。→ 助手消息过 `过滤显示()`；
      **用户自己的消息不过滤**（围栏是主人自己写的，照原样显示才对）。
    - **跨 chunk 拼接**：围栏标记会被切成 `\`\`\`pe` + `t`，所以判断必须基于**累积缓冲**，不能按单块判断。
