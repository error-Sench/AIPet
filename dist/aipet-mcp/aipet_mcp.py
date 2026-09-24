#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""AIPet 桌宠 · MCP 工具（stdio 服务）—— 让 Agent 读实时上下文、下结构化指令。

两个工具：
- `pet_context`（读）：调用即**当场读盘**组装（数值 / 画像 / 记忆 / 事件池 / 指令说明），
  永远是最新状态——桌宠「不做主动注入」，读取靠 Agent 自己发起。
- `pet_command`（写）：把一条指令写进桌宠的收件箱 `user://actions.jsonl`，等桌宠轮询执行并写回执，
  再把**真实执行结果**（✓ / ✗ + 原因）返回。正文里不再需要嵌指令块（那是没有 MCP 时的兼容通道）。

怎么用（以 Hermes 为例）：
    hermes mcp add aipet --command python --args "<本文件路径>"
    注册后 Agent 侧多出 `pet_context` 与 `pet_command`。其它 Agent 按其 MCP 文档添加 stdio server
    （命令 `python`，参数为本脚本路径）即可。

数据目录：
    默认 `%APPDATA%/Godot/app_userdata/AIPet`（找不到时回退旧名 `desktop`）；
    可用环境变量 `AIPET_DATA` 覆盖。

依赖：仅 Python 3 标准库（不需要安装任何东西）。
协议：MCP stdio —— 换行分隔的 JSON-RPC 2.0（每个消息一行）。
"""

import datetime
import io
import json
import os
import sys
import time
import uuid

# stdout / stdin 必须是 UTF-8（中文环境默认可能是 GBK，会破坏协议）
sys.stdin = io.TextIOWrapper(sys.stdin.buffer, encoding="utf-8")
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", write_through=True)
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8")

SERVER_NAME = "aipet-mcp"
SERVER_VERSION = "0.2.0"
DEFAULT_PROTOCOL = "2024-11-05"

ACTIONS_FILE = "actions.jsonl"
回执等待秒 = 2.0


def log(msg):
    print(f"[aipet-mcp] {msg}", file=sys.stderr, flush=True)


def find_data_dir():
    """定位桌宠数据目录（Godot 的 user:// 在 Windows 上的落点）。"""
    candidates = []
    env = os.environ.get("AIPET_DATA")
    if env:
        candidates.append(env)
    appdata = os.environ.get("APPDATA") or ""
    if appdata:
        candidates.append(os.path.join(appdata, "Godot", "app_userdata", "AIPet"))
        candidates.append(os.path.join(appdata, "Godot", "app_userdata", "desktop"))  # 旧项目名
    for c in candidates:
        if c and os.path.isdir(c):
            return c
    return None


# ============================ pet_context（读） ============================

def read_text(path, default=""):
    try:
        with open(path, "r", encoding="utf-8") as f:
            text = f.read().strip()
        return text if text else default
    except Exception:
        return default


def read_tail_lines(path, n):
    """读文件末尾 n 条非空行（记忆是流水账，只带最近几条）。"""
    try:
        with open(path, "r", encoding="utf-8") as f:
            lines = [ln.strip() for ln in f.readlines()]
        lines = [ln for ln in lines if ln]
        return lines[-n:]
    except Exception:
        return []


def read_json(path):
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def build_events(data_dir, pending_limit=5, recent_limit=8):
    """读事件池：返回（待处理事件文本, 最近事件文本）。与程序侧 EventPool 的语义一致。"""
    path = os.path.join(data_dir, "events.jsonl")
    lines = []
    try:
        with open(path, "r", encoding="utf-8") as f:
            lines = [ln.strip() for ln in f.readlines() if ln.strip()]
    except Exception:
        pass

    acked = set()
    rows = []
    for ln in lines:
        try:
            row = json.loads(ln)
        except Exception:
            continue  # 坏行跳过（与程序一致）
        if row.get("kind") == "ack" and row.get("ref"):
            acked.add(row["ref"])
        else:
            rows.append(row)

    pending = [r for r in rows
               if r.get("owner") == "agent" and r.get("kind") != "ack" and r.get("t") not in acked]
    recent = rows[-recent_limit:]

    def fmt(r):
        return f"- [{r.get('t', '?')}] {r.get('kind', '?')}（{r.get('owner', '?')}）：{r.get('text', '')}"

    pending_text = "\n".join(fmt(r) for r in pending[:pending_limit]) if pending \
        else "（没有待你处理的事件）"
    if len(pending) > pending_limit:
        pending_text += f"\n- …还有 {len(pending) - pending_limit} 条（自己读 events.jsonl）"
    recent_text = "\n".join(fmt(r) for r in recent) if recent else "（还没有事件）"
    return pending_text, recent_text


def mood_word(mood):
    """主人情绪读数的文字状态（与桌宠端 StatsTable.心情文字 同一套档位）。"""
    if not isinstance(mood, (int, float)):
        return "未知"
    if mood >= 85:
        return "超开心"
    if mood >= 70:
        return "心情不错"
    if mood >= 50:
        return "平平静静"
    if mood >= 35:
        return "有点蔫"
    if mood >= 20:
        return "不太开心"
    return "很低落"


def build_context():
    data_dir = find_data_dir()
    if not data_dir:
        return ("（找不到 AIPet 的数据目录：桌宠至少运行过一次吗？"
                "也可以用环境变量 AIPET_DATA 指向 `%APPDATA%/Godot/app_userdata/AIPet`。）")

    profile = read_text(os.path.join(data_dir, "soul", "profile.md"), "（还没有建立画像）")
    memory = read_tail_lines(os.path.join(data_dir, "soul", "memory.jsonl"), 12)
    stats = read_json(os.path.join(data_dir, "state", "stats.json"))
    pending_text, recent_text = build_events(data_dir)

    mood = stats.get("mood", "?")
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")

    out = []
    out.append("# AIPet 桌宠上下文（MCP 工具实时读取）")
    out.append("")
    out.append(f"> 本内容为 {now} 当场从磁盘读取 —— **永远是最新**（不是缓存快照、不受刷新节奏影响）。")
    out.append(f"> 数据目录：`{data_dir}`")
    out.append("> 源文件：`state/stats.json`（主人情绪读数，程序写、你经 set_mood 更新）· `soul/profile.md`（画像，你写）·")
    out.append("> `soul/memory.jsonl`（记忆，你写）· `events.jsonl`（事件池，程序写 + 你追加 ack）")
    out.append("")
    out.append("## 主人情绪读数（stats.json）")
    out.append(f"- mood {mood}/100 —— {mood_word(mood)}（0–100，中性 50；由**你**判断后经 set_mood 写入，程序只做衰减）")
    out.append("- 它**只影响你的回复策略**（怎么回应主人），不参与互动、不驱动动画。")
    out.append("")
    out.append("## 用户画像（profile.md）")
    out.append("```")
    out.append(profile)
    out.append("```")
    out.append("")
    out.append("## 最近记忆（memory.jsonl 最后 12 条）")
    if memory:
        out.extend(f"- {m}" for m in memory)
    else:
        out.append("（还没有记忆）")
    out.append("")
    out.append("## 待你处理的事件（处理完请往 events.jsonl 追加一条 ack 行，ref = 原事件的 t）")
    out.append(pending_text)
    out.append("")
    out.append("## 最近事件（发生了什么）")
    out.append(recent_text)
    out.append("")
    out.append("## 你能指挥桌宠做什么（指令通道）")
    out.append("**首选：调工具 `pet_command`** —— 一次一条、正文保持干净，还会拿到真实回执（成功 / 被拒 + 原因）。")
    out.append("没有 MCP 时（兼容通道）：在回复文本里内嵌一个 pet 围栏块（三个反引号 + 语言标记 pet），块内每行一条 JSON 指令，桌宠会执行并把围栏块从聊天里隐藏：")
    out.append("- `{\"cmd\":\"set_state\",\"state\":\"think|idle|sleep|working|speak…\"}` —— 切状态（10 个合法值，详见 skill）；`working` 可加 `\"type\":\"创作|计算机|美食|游戏|写作|其他|资料|绘图|清理|声音\"` 指定干活类型")
    out.append("- `{\"cmd\":\"speak\",\"text\":\"…\"}` —— 让它冒个气泡（≤200 字，别复述正文）")
    out.append("- `{\"cmd\":\"play_anim\",\"anim\":\"…\"}` —— 播指定动画（键名是 **anim**，不是 name）")
    out.append("- `{\"cmd\":\"set_mood\",\"mood\":65}` —— 写入主人情绪读数（0–100，50=中性；或 happy / sad / tired… 关键词）")
    out.append("- 文本通道每轮最多 6 条；写错了会被忽略并记日志。")
    out.append("")
    out.append("（完整规则见随桌宠交付的 skill：`aipet-desktop-pet`。）")
    return "\n".join(out)


# ============================ pet_command（写） ============================

def _find_reply(path, rid):
    """在收件箱里找这条 id 的回执行。返回 (ok, note) 或 None。"""
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            lines = f.readlines()
    except Exception:
        return None
    for ln in lines:
        ln = ln.strip()
        if not ln or rid not in ln:
            continue
        try:
            row = json.loads(ln)
        except Exception:
            continue
        if row.get("repl") == rid:
            return (bool(row.get("ok")), str(row.get("note", "")))
    return None


def run_command(arguments):
    """把一条指令写进桌宠收件箱并等回执。返回 (result_text, is_error)。"""
    cmd = arguments.get("cmd")
    if not cmd:
        return ("缺少 cmd —— 必须给一条指令名（set_state / speak / play_anim / set_mood / queue_chain / set_mode / open_url）", True)

    data_dir = find_data_dir()
    if not data_dir:
        return ("找不到桌宠数据目录（桌宠至少运行过一次吗？）", True)

    path = os.path.join(data_dir, ACTIONS_FILE)
    rid = uuid.uuid4().hex[:12]
    行 = {"id": rid, "t": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")}
    for k, v in arguments.items():
        if v is None:
            continue
        行[k] = v if isinstance(v, str) else str(v)

    try:
        with open(path, "a", encoding="utf-8") as f:
            f.write(json.dumps(行, ensure_ascii=False) + "\n")
            f.flush()
    except Exception as e:
        return (f"写收件箱失败：{e}", True)

    # 等桌宠回执（它每 0.25 秒轮询一轮）
    deadline = time.time() + 回执等待秒
    while time.time() < deadline:
        reply = _find_reply(path, rid)
        if reply is not None:
            ok, note = reply
            return ((f"✓ {note}" if ok else f"✗ {note}"), False)
        time.sleep(0.05)

    return (f"已发送，但 {回执等待秒:g} 秒内没收到回执 —— 桌宠好像没在运行，这条指令不会被执行。", False)


# ============================ MCP 协议 ============================

TOOLS = [
    {
        "name": "pet_context",
        "description": (
            "读取 AIPet 桌宠的最新上下文：主人情绪读数（mood）、显示名、用户画像、最近记忆、"
            "待处理事件、最近事件与指令通道说明。每次调用都当场读盘，永远是最新状态。"
            "需要了解桌宠、准备下发指令、或处理它的事件池时调用。"
        ),
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "pet_command",
        "description": (
            "给桌宠下一条指令并拿到真实回执（✓ 已执行 / ✗ 被拒 + 原因）。这是指令通道的**首选**："
            "正文保持干净，不要往回复文本里嵌指令块（那是没有 MCP 时的兼容通道）。一次一条；"
            "大多数回复**不要**调用它——只在真有表达价值时用。可用 cmd："
            "set_state（state：idle/interact/drag/think/speak/listen/working/sleep/greet/edge_hide；"
            "working 可加 type：创作/计算机/美食/游戏/写作/其他/资料/绘图/清理/声音 —— 指定干活类型）、"
            "speak（text：短气泡，≤200 字，别复述正文）、play_anim（anim：如 walk-left）、"
            "set_mood（mood：0-100 或 happy/tired/sad 等）、queue_chain（steps：如 greet:3,think:10,idle:0，≤5 步）、"
            "set_mode（mode：office/game）、open_url（url：仅主人开启了 aggressiveMode 时可用）。"
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "cmd": {"type": "string",
                        "enum": ["set_state", "speak", "play_anim", "set_mood", "queue_chain", "set_mode", "open_url"],
                        "description": "指令名（白名单内）"},
                "state": {"type": "string", "description": "set_state 用：10 个合法值之一"},
                "type": {"type": "string",
                         "description": "set_state 用（可选，仅 state=working）：工作类型 —— 创作/计算机/美食/游戏/写作/其他/资料/绘图/清理/声音；带上就走「开工」过渡（起身动作）并固定该类型的干活动画，不填 = 随机"},
                "text": {"type": "string", "description": "speak 用：要冒的短气泡（≤200 字）"},
                "anim": {"type": "string", "description": "play_anim 用：动画名（池-变体，如 walk-left；不确定就别用）"},
                "mood": {"type": ["string", "number"], "description": "set_mood 用：0-100 或关键词（happy/tired/sad 等）"},
                "steps": {"type": "string", "description": "queue_chain 用：如 greet:3,think:10,idle:0（≤5 步）"},
                "mode": {"type": "string", "enum": ["office", "game"], "description": "set_mode 用"},
                "url": {"type": "string", "description": "open_url 用（仅 aggressiveMode 时）"},
            },
            "required": ["cmd"],
            "additionalProperties": False,
        },
    },
]


def handle(request):
    method = request.get("method", "")
    params = request.get("params")
    if not isinstance(params, dict):
        params = {}
    rid = request.get("id")

    if method == "initialize":
        version = params.get("protocolVersion") or DEFAULT_PROTOCOL
        return {"jsonrpc": "2.0", "id": rid, "result": {
            "protocolVersion": version,
            "capabilities": {"tools": {}},
            "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
        }}
    if method.startswith("notifications/"):
        return None  # 通知不回复
    if method == "ping":
        return {"jsonrpc": "2.0", "id": rid, "result": {}}
    if method == "tools/list":
        return {"jsonrpc": "2.0", "id": rid, "result": {"tools": TOOLS}}
    if method == "tools/call":
        name = params.get("name")
        arguments = params.get("arguments")
        if not isinstance(arguments, dict):
            arguments = {}
        if name == "pet_context":
            try:
                text = build_context()
                return {"jsonrpc": "2.0", "id": rid, "result": {
                    "content": [{"type": "text", "text": text}], "isError": False}}
            except Exception as e:
                log(f"组装上下文失败: {e}")
                return {"jsonrpc": "2.0", "id": rid, "result": {
                    "content": [{"type": "text", "text": f"读取失败：{e}"}], "isError": True}}
        if name == "pet_command":
            try:
                text, is_err = run_command(arguments)
            except Exception as e:
                log(f"下发指令失败: {e}")
                text, is_err = f"下发失败：{e}", True
            return {"jsonrpc": "2.0", "id": rid, "result": {
                "content": [{"type": "text", "text": text}], "isError": is_err}}
        return {"jsonrpc": "2.0", "id": rid, "result": {
            "content": [{"type": "text", "text": f"未知工具：{name}（本服务提供 pet_context / pet_command）"}],
            "isError": True}}
    if method == "resources/list":
        return {"jsonrpc": "2.0", "id": rid, "result": {"resources": []}}
    if method == "prompts/list":
        return {"jsonrpc": "2.0", "id": rid, "result": {"prompts": []}}
    if rid is None:
        return None  # 未知通知：忽略
    return {"jsonrpc": "2.0", "id": rid, "error": {"code": -32601, "message": f"Method not found: {method}"}}


def main():
    log(f"启动（数据目录：{find_data_dir() or '未找到'}）")
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            request = json.loads(line)
        except Exception as e:
            log(f"无法解析输入：{e}")
            continue
        try:
            response = handle(request)
        except Exception as e:
            log(f"处理失败：{e}")
            response = {"jsonrpc": "2.0", "id": request.get("id"),
                        "error": {"code": -32603, "message": str(e)}}
        if response is not None:
            sys.stdout.write(json.dumps(response, ensure_ascii=False) + "\n")
            sys.stdout.flush()


if __name__ == "__main__":
    main()
