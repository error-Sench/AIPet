#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""AIPet 桌宠 · MCP 工具（stdio 服务）—— 让 Agent 随时拿到桌宠「最新」的上下文。

为什么有它：
    桌宠的硬规则是「不做主动注入」——不推送、不写钩子、不碰 Agent 的 system prompt。
    所以除了让 Agent 自己读文件，我们还提供一个**被动式读取工具**：
    Agent 调用 `pet_context` 的那一刻，本服务**当场读盘**组装（人格 / 数值 / 画像 /
    记忆 / 事件池 / 指令说明），因此拿到的永远是最新状态，不受桌面程序刷新节奏影响。

怎么用（以 Hermes 为例）：
    hermes mcp add aipet -- python "<本文件路径>"
    注册后 Agent 侧会多出工具 `pet_context`。其它 Agent 按其 MCP 文档添加 stdio server
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

# stdout / stdin 必须是 UTF-8（中文环境默认可能是 GBK，会破坏协议）
sys.stdin = io.TextIOWrapper(sys.stdin.buffer, encoding="utf-8")
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", write_through=True)
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8")

SERVER_NAME = "aipet-mcp"
SERVER_VERSION = "0.1.0"
DEFAULT_PROTOCOL = "2024-11-05"


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
    if not isinstance(mood, (int, float)):
        return "未知"
    if mood >= 75:
        return "心情不错"
    if mood >= 45:
        return "平平静静"
    if mood >= 30:
        return "有点蔫"
    return "心情低落"


def build_context():
    data_dir = find_data_dir()
    if not data_dir:
        return ("（找不到 AIPet 的数据目录：桌宠至少运行过一次吗？"
                "也可以用环境变量 AIPET_DATA 指向 `%APPDATA%/Godot/app_userdata/AIPet`。）")

    soul = read_text(os.path.join(data_dir, "soul", "soul.md"), "（soul.md 还没有内容）")
    profile = read_text(os.path.join(data_dir, "soul", "profile.md"), "（还没有建立画像）")
    memory = read_tail_lines(os.path.join(data_dir, "soul", "memory.jsonl"), 12)
    stats = read_json(os.path.join(data_dir, "stats.json"))
    pending_text, recent_text = build_events(data_dir)

    mood = stats.get("mood", "?")
    energy = stats.get("energy", "?")
    affection = stats.get("affection", "?")
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")

    out = []
    out.append("# AIPet 桌宠上下文（MCP 工具实时读取）")
    out.append("")
    out.append(f"> 本内容为 {now} 当场从磁盘读取 —— **永远是最新**（不是缓存快照、不受刷新节奏影响）。")
    out.append(f"> 数据目录：`{data_dir}`")
    out.append("> 源文件：`soul/soul.md`（人格，主人/你写）· `stats.json`（数值，程序写）· `soul/profile.md`（画像，你写）·")
    out.append("> `soul/memory.jsonl`（记忆，你写）· `events.jsonl`（事件池，程序写 + 你追加 ack）")
    out.append("")
    out.append("## 此刻的状态（数值层）")
    out.append(f"- 心情 mood {mood}/100 —— {mood_word(mood)}")
    out.append(f"- 精力 energy {energy}/100")
    out.append(f"- 亲密 affection {affection}/999（只增不减，随相处累积）")
    out.append("- 数值只影响桌宠的表现与频率（表情变体、走动节奏、打瞌睡早晚），不改变它的人格与说话方式。")
    out.append("")
    out.append("## 人格（soul.md 全文）")
    out.append("```")
    out.append(soul)
    out.append("```")
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
    out.append("在你的回复文本里内嵌一个 pet 围栏块（三个反引号 + 语言标记 pet），块内每行一条 JSON 指令，桌宠会执行并把围栏块从聊天里隐藏：")
    out.append("- `{\"cmd\":\"set_state\",\"state\":\"think|idle|sleep|working|speak…\"}` —— 切状态（10 个合法值，详见 skill）")
    out.append("- `{\"cmd\":\"speak\",\"text\":\"…\"}` —— 让它冒个气泡（≤200 字，别复述正文）")
    out.append("- `{\"cmd\":\"play_anim\",\"anim\":\"…\"}` —— 播指定动画（键名是 **anim**，不是 name）")
    out.append("- `{\"cmd\":\"set_mood\",\"mood\":65}` —— 改心情（0–100，或 happy / tired / sad…）")
    out.append("- 每轮最多 6 条；写错了会被忽略并记日志。")
    out.append("")
    out.append("（完整规则见随桌宠交付的 skill：`aipet-desktop-pet`。）")
    return "\n".join(out)


TOOLS = [
    {
        "name": "pet_context",
        "description": (
            "读取 AIPet 桌宠的最新上下文：人格、数值（心情/精力/亲密）、用户画像、最近记忆、"
            "待处理事件、最近事件与指令通道说明。每次调用都当场读盘，永远是最新状态。"
            "需要了解桌宠、准备下发指令、或处理它的事件池时调用。"
        ),
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
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
        if name == "pet_context":
            try:
                text = build_context()
                return {"jsonrpc": "2.0", "id": rid, "result": {
                    "content": [{"type": "text", "text": text}], "isError": False}}
            except Exception as e:
                log(f"组装上下文失败: {e}")
                return {"jsonrpc": "2.0", "id": rid, "result": {
                    "content": [{"type": "text", "text": f"读取失败：{e}"}], "isError": True}}
        return {"jsonrpc": "2.0", "id": rid, "result": {
            "content": [{"type": "text", "text": f"未知工具：{name}（本服务只提供 pet_context）"}],
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
