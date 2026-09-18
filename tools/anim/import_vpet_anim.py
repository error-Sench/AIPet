"""通用 VPet 动画导入器：VPet vup 资产 -> mods/main_anim/anim/loris/<语义池>/<变体>/

用法： python tools/anim/import_vpet_anim.py            # 导入 SPEC 里全部池
       python tools/anim/import_vpet_anim.py edge_hide  # 只导某个池

硬规则（全部来自实测，见 AGENTS.md §3 / plan.md P2）：
  1. 帧名重编号为三位零填充从 000 起 —— CharAnim 的帧排序是**字符串序**，不是数值序
  2. 缩放到 512x512 并按**角色包围盒**对齐到现有 loris 素材基线 —— 直接 1000->512 会尺寸不一致、切动画时跳位
  3. 整段动画只算**一次**偏移逐帧套用 —— 否则会抹掉帧间位移（动作本身）
  4. 单目录多序列必须拆开：一个叶子目录里若出现**多于一个文件名前缀**就跳过并告警
     （VPet 常在单目录塞两条序列，如 `1毛笔开心_*` + `2…退出通用_*`、`FLA_*` + `FLB_*`）
  5. 丢弃 1bit/灰度遮罩层（`*_lay` / `front` / `back` 这类不是帧序列）
  6. info.json 只写 rate（由文件名里的 `_<ms>` 后缀折算，取众数）
  7. **帧切片**：VPet 有的目录里混了不属于该段的帧（实测 `SideHide_Right_Main/Nomal/A` 多粘了
     2 帧「迸出」开头 + 3 帧收尾）。片段写法 `(源, 起帧序号, 止帧序号)`（含端点，None = 全段），
     一个变体可以由**多个片段拼接**（顺序即拼接顺序）。
"""
import os
import re
import sys
import shutil
from collections import Counter
from PIL import Image

VPET = r"D:/SteamLibrary/steamapps/common/VPet/mod/0000_core/pet/vup"
DST_ROOT = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "mods", "main_anim", "anim", "loris")
REF = os.path.join(DST_ROOT, "idle/1/00.png")
CANVAS = 512

# 固定缩放（不用每段动画各自的包围盒高度来反推！）
#   VPet 站立姿态角色高 ~948px，现有 loris 参考帧角色高 485px -> 485/948
#   为什么必须固定：躺下/蹲下这类姿势的包围盒本来就矮，按包围盒对齐会把它们**放大**
#   （实测 sleep 被放大到 1.021 倍），而且含道具的动画（写字/电脑）会把道具算进包围盒导致角色缩水。
#   固定缩放 = 所有姿势保持同一角色比例，姿势天然变矮就是变矮。
固定缩放 = 485 / 948

# 池 -> [(变体, [片段, ...]), ...]；片段 = (源叶子相对路径, 起帧序号|None, 止帧序号|None)
# 语义说明：Touch_Head/Touch_Body 是「被摸的反应」→ interact；greet 用开心姿势（VPet 无专门打招呼动作）
SPEC = {
    "think": [
        ("nomal", [("Think/Nomal/B", None, None)]),
        ("happy", [("Think/Happy/B", None, None)]),
        ("poor", [("Think/PoorCondition/B", None, None)]),
    ],
    "sleep": [
        ("loop", [("Sleep/B_Nomal", None, None)]),
        ("happy", [("Sleep/B_Happy", None, None)]),
    ],
    "greet": [
        ("amuse", [("IDEL/amusement_B", None, None)]),
        ("meow", [("IDEL/Meow/Happy/1", None, None)]),
    ],
    "say": [
        ("smile", [("Say/Shining/B_2", None, None)]),
        ("self", [("Say/Self/B_1", None, None)]),
        ("serious", [("Say/Serious/B", None, None)]),
    ],
    "work": [
        ("pc", [("WORK/WorkTWO/A_Nomal", None, None)]),
        ("read", [("WORK/Study/A_Nomal", None, None)]),
        ("write", [("WORK/WorkONE/A_Nomal", None, None)]),
    ],
    "interact": [
        # 摸头反应其实是三段：A=进入(中立→抱头) B=保持(抱头) C=退出(抱头→中立)。
        # 三段连着播 = 自然的「被摸 → 回到待机」，单播 B 会在抱头姿势上硬切到待机（用户反馈：突兀）。
        ("a", [("Touch_Head/A_Nomal", None, None)]),
        ("b", [("Touch_Head/B_Nomal", None, None)]),
        ("c", [("Touch_Head/C_Nomal", None, None)]),
    ],
    # 贴边隐藏（VPet SideHide_*）。**官方用法**（VPet 源码 `Main.xaml.cs` / `MainLogic.cs`）：
    #   躲到边缘  → 播 `SideHide_<侧>_Main` 的 A_Start 然后循环 B
    #   鼠标进入  → 播 `SideHide_<侧>_Rise` 的 **A_Start 然后循环 B**（这就是「探出」）
    #   鼠标离开  → 播 `SideHide_<侧>_Rise` 的 C_End，回到 Main 的 B 循环（这就是「缩回」）
    # 于是每侧三段：Main A(进) / Main B_1+B_2(保持) / Main C(出)；Rise A(弹出) / Rise B(探出后微动循环) / Rise C(缩回)。
    #
    # 两个实测坑（数值核对 + 镜像比对，2026-09-19）：
    #   * `SideHide_Right_Main/Nomal/A` 里**多粘了 5 帧**：前 9 帧才是「缩进」（与左 A 逐帧镜像一致），
    #     第 9/10 帧其实是「退出」的起跳两帧（与左 C 的第 0/1 帧镜像一致），11-13 帧是收尾。
    #     → 右「缩进」切 0..8，那两帧起跳帧拼进右「退出」，两侧这才真正一一对应。
    #   * `SideHide_*_Rise/Nomal/B`（10 帧）是**探出后的微动循环**，早先漏导 → 表现为「探出没动画」。
    "edge_hide": [
        ("left-in",     [("SideHide_Left_Main/Nomal/A", None, None)]),
        ("left-keep",   [("SideHide_Left_Main/Nomal/B_1", None, None)]),
        ("left-hold",   [("SideHide_Left_Main/Nomal/B_2", None, None)]),
        ("left-out",    [("SideHide_Left_Main/Nomal/C", None, None)]),
        ("left-peek",   [("SideHide_Left_Rise/Nomal/A", None, None)]),
        ("left-rise",   [("SideHide_Left_Rise/Nomal/B", None, None)]),
        ("left-unpeek", [("SideHide_Left_Rise/Nomal/C", None, None)]),
        ("right-in",    [("SideHide_Right_Main/Nomal/A", 0, 8)]),
        ("right-keep",  [("SideHide_Right_Main/Nomal/B_1", None, None)]),
        ("right-hold",  [("SideHide_Right_Main/Nomal/B_2", None, None)]),
        ("right-out",   [("SideHide_Right_Main/Nomal/A", 9, 10), ("SideHide_Right_Main/Nomal/C", None, None)]),
        ("right-peek",  [("SideHide_Right_Rise/Nomal/A", None, None)]),
        ("right-rise",  [("SideHide_Right_Rise/Nomal/B", None, None)]),
        ("right-unpeek",[("SideHide_Right_Rise/Nomal/C", None, None)]),
    ],
}


def 帧序(name):
    """从 `<前缀>_<序号>_<时长>.png` 抽帧序号（按序号排序，不按文件名——见规则 4 的说明）。"""
    m = re.search(r"_(\d+)_(\d+)\.png$", name)
    return int(m.group(1)) if m else 0


def 前缀(name):
    """取 `前缀_帧序_时长.png` 里的前缀；不符合该结构返回 None。"""
    m = re.match(r"^(.*?)_\d+_\d+\.png$", name, re.IGNORECASE)
    return m.group(1) if m else None


def 时长秒(name):
    m = re.search(r"_(\d+)\.png$", name, re.IGNORECASE)
    return int(m.group(1)) if m else None


def union_bbox(paths):
    x0 = y0 = 10 ** 9
    x1 = y1 = -1
    for p in paths:
        bb = Image.open(p).convert("RGBA").getchannel("A").getbbox()
        if not bb:
            continue
        x0, y0 = min(x0, bb[0]), min(y0, bb[1])
        x1, y1 = max(x1, bb[2]), max(y1, bb[3])
    return (x0, y0, x1, y1)


def 基线():
    bb = Image.open(REF).convert("RGBA").getchannel("A").getbbox()
    return {"bottom": bb[3], "cx": (bb[0] + bb[2]) / 2, "h": bb[3] - bb[1]}


def 收集片段(片段列表, 报告):
    """按片段收集 (绝对路径, 文件名) 列表：逐源排序 → 序号去重检查 → 灰度遮罩丢弃 → 帧区间切片。"""
    结果 = []
    for 源, 起, 止 in 片段列表:
        源叶子 = os.path.join(VPET, 源.replace("\\", "/"))
        if not os.path.isdir(源叶子):
            报告.append(f"  [跳过] {源}: 源目录不存在")
            continue
        frames = sorted(f for f in os.listdir(源叶子) if f.lower().endswith(".png"))
        # 规则 4：判据必须是**帧序号重复**，不能是「文件名前缀不同」——VPet 里有帧名拼写不一致的真实案例：
        #   SideHide_Right_Main/Nomal/A 里 A_000..A_013 少一个 A_011，而第 11 帧被命名成 A01_011。
        序号集 = [帧序(f) for f in frames]
        if len(set(序号集)) != len(序号集):
            重复 = [s for s, c in Counter(序号集).items() if c > 1]
            报告.append(f"  [跳过] {源}: 帧序号重复 {sorted(重复)[:3]} —— 确实混了两条序列，需人工拆目录")
            continue
        frames = [f for _, f in sorted(zip(序号集, frames))]  # 按帧序号排序（而非文件名序）
        # 规则 7：帧切片（含端点）
        if 起 is not None or 止 is not None:
            lo = 起 if 起 is not None else 帧序(frames[0])
            hi = 止 if 止 is not None else 帧序(frames[-1])
            frames = [f for f in frames if lo <= 帧序(f) <= hi]
            if not frames:
                报告.append(f"  [跳过] {源}: 帧区间 {起}..{止} 切出来是空的")
                continue
        # 规则 5：丢灰度/1bit 遮罩
        for f in frames:
            im = Image.open(os.path.join(源叶子, f))
            if im.mode in ("1", "L", "LA"):
                报告.append(f"  [丢弃] {源}/{f}: 灰度遮罩层（mode={im.mode}）")
                continue
            结果.append((os.path.join(源叶子, f), f))
    return 结果


def 导入一个动画(池, 变体, 片段列表, 基线值, 报告):
    frames = 收集片段(片段列表, 报告)
    if not frames:
        报告.append(f"  [跳过] {池}/{变体}: 无有效帧")
        return 0

    paths = [p for p, _ in frames]
    ub = union_bbox(paths)
    if ub[2] < 0:
        报告.append(f"  [跳过] {池}/{变体}: 全透明，无有效包围盒")
        return 0
    scale = 固定缩放
    off_x = 基线值["cx"] - ((ub[0] + ub[2]) / 2) * scale
    off_y = 基线值["bottom"] - ub[3] * scale

    out = os.path.join(DST_ROOT, 池, 变体)
    os.makedirs(out, exist_ok=True)
    for old in os.listdir(out):
        os.remove(os.path.join(out, old))
    for i, p in enumerate(paths):
        im = Image.open(p).convert("RGBA")
        w, h = im.size
        im = im.resize((round(w * scale), round(h * scale)), Image.LANCZOS)
        canvas = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
        canvas.alpha_composite(im, (round(off_x), round(off_y)))
        canvas.save(os.path.join(out, f"{i:03d}.png"))

    # 规则 6：rate 取文件名时长档的众数
    档 = Counter(时长秒(f) for _, f in frames)
    ms = 档.most_common(1)[0][0] or 125
    rate = max(1, round(1000 / ms))
    with open(os.path.join(out, "info.json"), "w", encoding="utf-8") as fp:
        fp.write('{\n    "rate": %d\n}\n' % rate)

    bb = Image.open(os.path.join(out, "000.png")).convert("RGBA").getchannel("A").getbbox()
    报告.append(f"  [完成] {池}/{变体}: {len(frames)}帧 源包围盒={ub[2]-ub[0]}x{ub[3]-ub[1]} scale={scale:.4f} rate={rate} "
              f"-> 角色高={bb[3]-bb[1]} 底边={bb[3]}")
    return len(frames)


def main():
    only = sys.argv[1:] or None
    基线值 = 基线()
    print(f"参考基线: 底边={基线值['bottom']} 中心x={基线值['cx']} 角色高={基线值['h']}")
    报告 = []
    总帧 = 0
    for 池, 项列表 in SPEC.items():
        if only and 池 not in only:
            continue
        # 整池重导：先清空池目录，避免旧变体残留（`进入状态` 是池内随机取一项）
        池目录 = os.path.join(DST_ROOT, 池)
        if os.path.isdir(池目录):
            shutil.rmtree(池目录)
        报告.append(f"### {池}")
        for 变体, 片段列表 in 项列表:
            总帧 += 导入一个动画(池, 变体, 片段列表, 基线值, 报告)
    print("\n".join(报告))
    print(f"\n共导入 {总帧} 帧")


if __name__ == "__main__":
    main()
