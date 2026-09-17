#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""aipet-mcp 冒烟测试（不依赖 Hermes、不连 Agent）：直接以 stdio 驱动 MCP 服务。

覆盖：
- tools/list 暴露 `pet_context` + `pet_command`
- A 无桌宠：`pet_command` 把请求写进收件箱、2 秒等不到回执 → 返回超时提示
- B 假桌宠：后台线程见请求就写回执 → 工具拿到回执并返回给调用方
- C `pet_context`：当场读盘组装正常返回

用法：`python tools/aipet_mcp_smoke.py`（退出码 = 失败数；改过 `dist/aipet-mcp/aipet_mcp.py` 后跑一次）
"""
import json
import os
import subprocess
import sys
import tempfile
import threading
import time

SCRIPT = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "dist", "aipet-mcp", "aipet_mcp.py"))

失败 = 0


def 检查(条件: bool, 描述: str):
    global 失败
    print(("PASS  " if 条件 else "FAIL  ") + 描述)
    if not 条件:
        失败 += 1


def 消息(rid, method, params=None):
    m = {"jsonrpc": "2.0", "id": rid, "method": method}
    if params is not None:
        m["params"] = params
    return m


def 跑(消息列表, 环境):
    """把消息整体喂给服务进程，读回全部响应。"""
    inp = "\n".join(json.dumps(m, ensure_ascii=False) for m in 消息列表) + "\n"
    p = subprocess.run([sys.executable, SCRIPT], input=inp.encode("utf-8"),
                       capture_output=True, timeout=60, env=环境)
    out = p.stdout.decode("utf-8").strip().splitlines()
    return [json.loads(x) for x in out], p.stderr.decode("utf-8", "replace")


基础 = [
    消息(1, "initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                          "clientInfo": {"name": "smoke", "version": "0"}}),
    {"jsonrpc": "2.0", "method": "notifications/initialized"},
    消息(2, "tools/list"),
]

# ---------------------------------------------------------------- 场景 A ----
tmpA = tempfile.mkdtemp(prefix="aipet_mcp_A_")
envA = {**os.environ, "AIPET_DATA": tmpA}
respA, _ = 跑(基础 + [消息(3, "tools/call", {
    "name": "pet_command", "arguments": {"cmd": "set_state", "state": "think"}})], envA)

tools = next(r["result"]["tools"] for r in respA if r.get("id") == 2)
名字 = [t["name"] for t in tools]
检查(名字 == ["pet_context", "pet_command"], f"tools/list = {名字}")

文本A = next(r["result"]["content"][0]["text"] for r in respA if r.get("id") == 3)
检查("没收到回执" in 文本A, "A 无桌宠 → 超时提示")

请求行 = open(os.path.join(tmpA, "actions.jsonl"), encoding="utf-8").read().strip()
检查('"cmd": "set_state"' in 请求行 and '"id"' in 请求行, "A 请求已写进收件箱")

# ---------------------------------------------------------------- 场景 B ----
tmpB = tempfile.mkdtemp(prefix="aipet_mcp_B_")
envB = {**os.environ, "AIPET_DATA": tmpB}
文件B = os.path.join(tmpB, "actions.jsonl")
停止 = False


def 假桌宠():
    seen = set()
    while not 停止:
        try:
            if os.path.exists(文件B):
                for 行 in open(文件B, encoding="utf-8"):
                    行 = 行.strip()
                    if not 行:
                        continue
                    try:
                        row = json.loads(行)
                    except Exception:
                        continue
                    rid = row.get("id")
                    if rid and rid not in seen and "cmd" in row:
                        seen.add(rid)
                        with open(文件B, "a", encoding="utf-8") as f:
                            f.write(json.dumps({"repl": rid, "ok": True,
                                                "note": f"✓ 假桌宠执行了 {row.get('cmd')}"},
                                               ensure_ascii=False) + "\n")
        except Exception:
            pass
        time.sleep(0.05)


线程 = threading.Thread(target=假桌宠, daemon=True)
线程.start()
respB, _ = 跑(基础 + [消息(4, "tools/call", {
    "name": "pet_command", "arguments": {"cmd": "speak", "text": "你好"}})], envB)
停止 = True
文本B = next(r["result"]["content"][0]["text"] for r in respB if r.get("id") == 4)
检查("假桌宠执行了 speak" in 文本B, "B 有桌宠 → 回执返回")

# ---------------------------------------------------------------- 场景 C ----
respC, _ = 跑(基础 + [消息(5, "tools/call", {"name": "pet_context", "arguments": {}})], envA)
文本C = next(r["result"]["content"][0]["text"] for r in respC if r.get("id") == 5)
检查("AIPet 桌宠上下文" in 文本C, f"C pet_context 正常返回（{len(文本C)} 字）")

print(f"\n失败数 = {失败}")
sys.exit(1 if 失败 else 0)
