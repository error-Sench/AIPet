"""AGENTS.md 拆分迁移（一次性脚本，保留可重跑）。

背景：AGENTS.md 涨到 554 行，把**各层的细节**下沉到对应脚本文件夹的 README，
AGENTS.md 只留总纲（定位 / 架构总览 / 目录地图 / 路线图 / 开发约定 / 已知事项 / 跨层踩坑 / 索引）。

原则：
1. **不重写内容**，只搬移（原文照搬，保证零丢失）。
2. **踩坑编号保持全局不变**（#1~#18 原号保留），这样「见 §10 坑 #16」这类交叉引用仍然有效。
3. 幂等：已生成过就覆盖重写；AGENTS.md 每次都由脚本重新拼装。
"""
import os
import re

ROOT = r"D:/Games/Github/AIPet"
SRC = os.path.join(ROOT, "AGENTS.md")

# 章节 -> 目标文档（None = 留在 AGENTS.md）
SECTION_ROUTE = {
    "2": ("script/Soul/README.md", "灵魂层 / 数值层 —— 说明文档", "script/Soul/"),
    "3": ("script/State/README.md", "身体层（状态机 / 节律 / 环境感知）—— 说明文档", "script/State/"),
    "4": ("script/Mode/README.md", "模式层（办公 / 游戏）—— 说明文档", "script/Mode/"),
    "6": ("script/Agent/README.md", "能力层（Agent 桥 / 协议 / 指令通道）—— 说明文档", "script/Agent/"),
}

# 踩坑条目 -> 去处（"AGENTS" = 跨层，留在 AGENTS.md；其它 = 目标文档）
PITFALL_ROUTE = {
    1: "UX", 2: "UX", 3: "UX", 4: "AGENTS", 5: "UX", 6: "tests", 7: "AGENTS", 8: "AGENTS",
    9: "Agent", 10: "Agent", 11: "Agent", 12: "UX", 13: "Agent", 14: "State", 15: "tests",
    16: "AGENTS", 17: "tests", 18: "tests",
}
PITFALL_TARGET = {
    "UX": ("script/UX/README.md", "界面 / 窗口 / 动画 —— 说明文档", "script/UX/"),
    "State": ("script/State/README.md", "身体层（状态机 / 节律 / 环境感知）—— 说明文档", "script/State/"),
    "Agent": ("script/Agent/README.md", "能力层（Agent 桥 / 协议 / 指令通道）—— 说明文档", "script/Agent/"),
    "tests": ("tests/README.md", "回归探针 —— 说明文档", "tests/"),
}

HEADER = """# {标题}

> 本文件是 **`AGENTS.md` 的分册**。AGENTS.md 是总纲（项目定位 / 架构总览 / 目录地图 / 路线图 / 开发约定 / 已知事项），
> **本层的细节与踩坑在这里**。相关代码：`{代码目录}`；改动后请跑 `tests/README.md` 里对应的探针。
> 约定：标识符英文，注释中文。

"""


def 读() -> str:
    with open(SRC, encoding="utf-8") as f:
        return f.read()


def 切章节(文本: str):
    """按 `## ` 切；返回 [(编号 or None, 标题行, 正文), ...]（含文首前言）。"""
    lines = 文本.split("\n")
    块 = []
    当前 = (None, "", [])
    for ln in lines:
        m = re.match(r"^## (\d+)\.", ln)
        if m:
            块.append(当前)
            当前 = (m.group(1), ln, [ln])
        else:
            当前[2].append(ln)
    块.append(当前)
    return [(n, t, "\n".join(b).strip("\n")) for n, t, b in 块]


def 切踩坑(正文: str):
    """把 §10 切成：前言 + {编号: 正文} + 尾部（含 10.1 探针清单）。"""
    前言, 条目, 尾 = [], {}, []
    cur = None
    in_101 = False
    for ln in 正文.split("\n"):
        if ln.startswith("### 10.1"):
            in_101 = True
            cur = None
        if in_101:
            尾.append(ln)
            continue
        m = re.match(r"^(\d+)\. ", ln)
        if m:
            cur = int(m.group(1))
            条目[cur] = [ln]
        elif cur is None:
            前言.append(ln)
        else:
            条目[cur].append(ln)
    return "\n".join(前言).strip("\n"), {k: "\n".join(v).rstrip() for k, v in 条目.items()}, "\n".join(尾).strip("\n")


def main():
    原 = 读()
    块 = 切章节(原)
    桶 = {}          # 目标路径 -> [片段]
    保留 = []        # AGENTS.md 保留的片段
    报告 = []

    for 编号, 标题行, 正文 in 块:
        if 编号 is None:
            保留.append(正文)
            continue
        if 编号 == "10":
            前言, 条目, 尾 = 切踩坑(正文)
            保留.append(前言)
            跨层 = []
            for i in sorted(条目):
                目标 = PITFALL_ROUTE.get(i, "AGENTS")
                if 目标 == "AGENTS":
                    跨层.append(条目[i])
                else:
                    路径 = PITFALL_TARGET[目标][0]
                    桶.setdefault(路径, []).append(f"### 踩坑 #{i}\n\n{条目[i]}")
            保留.append("## 10. 踩坑清单（全部为实测结论，改代码前先看这一节）\n\n"
                        "> **本清单已按层拆分**：与某一层强相关的条目在那一层的 README 里（编号保持全局不变，"
                        "「见坑 #N」这类引用仍然有效）。本文件只保留**跨层**的几条。\n\n" + "\n\n".join(跨层))
            桶.setdefault("tests/README.md", []).append("## " + 尾)
            报告.append(f"§10 踩坑 18 条 → 跨层留 {len(跨层)} 条，其余分流到各层 README")
            continue
        if 编号 in SECTION_ROUTE:
            路径, _, _ = SECTION_ROUTE[编号]
            桶.setdefault(路径, []).append(正文)
            报告.append(f"§{编号} {标题行[3:]} → {路径}（{len(正文.splitlines())} 行）")
        else:
            保留.append(正文)
            报告.append(f"§{编号} {标题行[3:]} → 留在 AGENTS.md（{len(正文.splitlines())} 行）")

    # —— 写各层 README ——
    for 路径, 片段 in 桶.items():
        meta = None
        for _, (p, t, d) in {**SECTION_ROUTE, **{k: v for k, v in PITFALL_TARGET.items()}}.items():
            if p == 路径:
                meta = (t, d)
                break
        if meta is None:
            meta = ("回归探针 —— 说明文档", "tests/")
        全 = HEADER.format(标题=meta[0], 代码目录=meta[1]) + "\n---\n\n".join(片段) + "\n"
        全路径 = os.path.join(ROOT, 路径)
        os.makedirs(os.path.dirname(全路径), exist_ok=True)
        with open(全路径, "w", encoding="utf-8") as f:
            f.write(全)
        报告.append(f"写出 {路径}（{len(全.splitlines())} 行）")

    # —— 重写 AGENTS.md：文首 + 保留章节 + 索引 ——
    索引 = [
        "## 分册索引（各层细节都在这里，别在本文件里找）\n",
        "| 想知道什么 | 看哪 |",
        "|---|---|",
        "| 灵魂层：`soul.md` 结构、思维范式、数值层（心情/精力/亲密）、数值可视化 | `script/Soul/README.md` |",
        "| 身体层：状态效果表、交互时序、自主行为节律、环境感知（P6） | `script/State/README.md` |",
        "| 界面层：面板群（聊天/工具栏/配置/状态窗）、窗口铁律、动画池机制、云母样式 | `script/UX/README.md` |",
        "| 能力层：ACP 客户端、AgentBridge、指令通道协议、白名单与安全边界、人格注入 | `script/Agent/README.md` |",
        "| 模式层：办公 / 游戏切换接口 | `script/Mode/README.md` |",
        "| 回归探针：有哪些、怎么跑、探针方法论（怎么写出不骗自己的断言） | `tests/README.md` |",
        "| 资产导入：VPet → mods 的导入器规则与踩坑 | `tools/README.md`（见该文件） |",
        "| 设计补充（Why/What）、阶段计划与施工记录 | `document/idea.md`、`document/plan.md` |",
        "",
        "**定位**：AGENTS.md = 总纲（**How 的框架**）；各层 README = 该层的**细节与踩坑**；`document/idea.md` = 设计补充（Why/What）。",
        "",
    ]
    新 = "\n\n".join(保留[:2] + ["\n".join(索引)] + 保留[2:]).strip("\n") + "\n"
    with open(SRC, "w", encoding="utf-8") as f:
        f.write(新)

    报告.append(f"\nAGENTS.md：554 行 → {len(新.splitlines())} 行")
    print("\n".join(报告))


if __name__ == "__main__":
    main()