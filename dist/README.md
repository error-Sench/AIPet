# AIPet 桌宠 · 交付包（skill + 灵魂模板）

这个目录是 AIPet 桌宠**对外交付**的成果：给用户 Agent 的 **skill**、给用户的**灵魂模板**，外加这份说明。

## 语音（可选）
**默认关闭**（`config/tts.json` 的「启用」= false）。想开：① 装小依赖 `powershell -ExecutionPolicy Bypass -File tools\install_edge_tts.ps1`；② 把「启用」改成 `true` —— 之后用 Edge 在线语音（晓晓，好听，需要网络）念气泡。不装也没关系——**它就不出声**（不报错）；想用本机声音就把 `引擎` 改成 `sapi`（机械）。

## MCP 工具（可选，但推荐）

`aipet-mcp/aipet_mcp.py` 是一个 **stdio MCP 服务**（纯 Python 标准库，无依赖）。注册给你的 Agent 后，它会多出工具 **`pet_context`**：每次调用**当场读盘**，一次拿到最新的数值 / 画像 / 记忆 / 事件（**不含人格**——人格经 Agent 内化）。

注册示例（Hermes）：`hermes mcp add aipet -- python "<交付目录>\aipet-mcp\aipet_mcp.py"`；其它 Agent 按其 MCP 文档添加 stdio server。不注册也没关系 —— Agent 直接读 `context.md` 同样能工作。

## 这是什么（为什么是这两个文件）

AIPet 的产品硬规则：**桌宠不做任何主动注入。**

> 它不会把人格、数值、记忆拼进发给 Agent 的消息，不写插件钩子，也不改 Agent 的 system prompt。
> 它只把文件**放在磁盘上**；用户把 **skill 提交给自己的 Agent**——Agent 读一次，把这份职责**内化进自己的人设**（自主注入），之后按需读数据文件。

所以交付物不是插件、不是配置，而是这些：

| 交付物 | 文件 | 给谁 |
|---|---|---|
| skill | `skill/SKILL.md` | **给用户 Agent 的一次性内化文档**（读一遍：把「桌宠的职责与互动」写进自己的人设；之后不必再读） |
| MCP 工具 | `aipet-mcp/aipet_mcp.py` | 给用户的 Agent（注册后：`pet_context` 拿实时上下文；`pet_command` 下指令——正文保持干净、有真实回执） |
| 人格内化材料 | `AIPet-SOUL.md` | 给用户的 Agent（人格的结构、写法与成品示例；程序内部不保存人格） |

> 本目录只**新增文件**，不改动仓库里的任何代码与文档。`AIPet-SOUL.md` 是人格的**内化材料**（结构 + 示例）：给 Agent 读一次、写进它自己的人设；程序侧**不保存、不读取**人格。
>
> 打包脚本（`tools/package.sh`）会把本目录的 `AIPet-SOUL.md` / `skill/` / `aipet-mcp/` 一并复制进分发包根目录，用户开箱就能看到。

## 三步用起来

1. **接上你的 Agent**：桌宠通过 ACP 驱动 Agent（`config/agent.json` 里 `backend: hermes-acp` + `executable` 指向你的 agent CLI）。接好之后，聊天窗里你说的话会原样发给它，它的回复显示回来。
2. **把 skill 交给你的 Agent**（下一节）——它读一次就会把职责内化成自己的人设，不需要每次重读。
3. **想改人格** → 跟你的 Agent 说（人格在它自己的内化里）；想让它重新内化一遍，就让它再读一次 `AIPet-SOUL.md`。

## 怎么把 skill 交给自己的 Agent

通用做法：**建一个以 skill 名命名的目录，把 `SKILL.md` 放进去**（目录名建议用 `aipet-desktop-pet`，与文件里 frontmatter 的 `name` 一致）。

| 你的 Agent | 放哪 | 备注 |
|---|---|---|
| **Hermes**（本项目默认后端） | `~/.hermes/skills/aipet-desktop-pet/SKILL.md`（Windows 上可能是 `%LOCALAPPDATA%\hermes\skills\`） | 放好后用 `hermes skills list` 或会话里的 `/skills` 确认它被读到；也可以直接建目录后丢文件 |
| **Claude Code** | `~/.claude/skills/aipet-desktop-pet/SKILL.md`（个人）或项目里 `.claude/skills/...` | 目录名 = skill 名 |
| **支持 skills 的其他工具**（部分编辑器 Agent 等） | 按其文档的 skills 目录；跨工具约定目录是 `.agents/skills/` | 多数工具要求目录名与 `name` 字段一致 |
| **没有 skill 机制的 Agent**（纯 CLI / 自研） | 把 `SKILL.md` 的**正文**并入它的规则或上下文档（`AGENTS.md`、`.cursor/rules`、项目说明），或直接作为第一轮对话的附件 | 最不济：把它粘进对话并说一句「以后按这份 skill 办事」——能用，但每轮都要付上下文成本 |

> 有些 Agent 自己还有「人格文件」机制（例如 Hermes 的 `SOUL.md`）——那是**用户自己的选择**，与本项目无关，本项目不依赖它。

## 数据文件在哪（skill 里也写了）

桌宠的 `user://` 在 Windows 上：

```
C:\Users\<你的用户名>\AppData\Roaming\Godot\app_userdata\AIPet\
```

| 文件 | 谁写 | 说明 |
|---|---|---|
| `context.md` | **程序**（启动 + 事件池变动时） | 只读入口：数值摘要 + 画像 + 最近 12 条记忆 + 待处理事件 + 指令说明 + 全部源文件路径（不含人格——人格经 Agent 内化） |
| `soul/profile.md` | **Agent** | 用户画像，≤ 5000 字符 |
| `soul/memory.jsonl` | **Agent** | 记忆流水（短期 6 → 中期 6 → 永久 20 的提炼链） |
| `state/stats.json` | **程序** | 主人情绪读数：`mood` / `savedAtUnix`（值由 Agent 经 set_mood 写入；程序只做衰减与存储） |
| `events.jsonl` | 程序写；Agent 追加 ack | 事件池，上限 500 行 |

## 怎么改人格

1. 让 Agent 读一遍 `AIPet-SOUL.md`（内化材料），把桌宠的人格与职责写进它自己的人设 —— 之后人格就住在 Agent 那里。
2. 名字：在 `config/config.json` 的「名字」里写（缺省「萝莉丝」；程序里没有改名件）。
3. **数据各归各位**：数值归 `state/stats.json`、记忆归 `soul/memory.jsonl`、画像归 `soul/profile.md`、游戏进度归 `game/save.json`；名字是配置（`config/config.json`）。
4. 看实时状态：有 MCP 调 `pet_context`，否则读数据目录的 `context.md`。

## 已知事项（写给接手的人）

- ✅ **`play_anim` 示例键名已对齐**：`context.md` 生成的示例是 `{"cmd":"play_anim","anim":"…"}`（与 `PetCommands.cs` 一致；旧包里「写成 name 会被丢弃」的提示已过时）。
- `soul_get` / `soul_set` 在白名单里但**未实现**，会被跳过并记日志（人格在 Agent 自己的人设里）。
- `open_url` 仅在 `config/agent.json` 的 `aggressiveMode=true` 时可用；默认关闭。项目**刻意不提供**任何「执行本地命令」能力。
- 事件池 500 行为上限（自动裁最旧）；ack 是否算数取决于 `ref` 与事件的 `t` **完全一致**。
- 隐私边界：事件池只记桌宠自己的观察（时间 / 时长 / 状态机事件），**不记**窗口标题、进程名、键鼠内容、屏幕内容；**记录开关在配置窗「行为」页**（默认开；关掉 = 一条也不写）。
- 本包内容与代码同步于：`script/Agent/PetCommands.cs`（白名单 / 上限）、`script/Soul/ContextTable.cs`（路径 / 上下文结构 / 画像与记忆模板）、`script/State/EventPool.cs`（事件格式 / ack）、`script/State/StateMachine.cs`（合法状态）、`script/Soul/StatsTable.cs`（主人情绪读数 / 心情词）。
