"""通用 VPet 动画导入器：VPet vup 资产 -> mods/main_anim/anim/loris/<语义池>/<变体>/

用法： python tools/import_vpet_anim.py            # 导入 SPEC 里全部池
       python tools/import_vpet_anim.py think      # 只导某个池

硬规则（全部来自实测，见 AGENTS.md §3 / plan.md P2）：
  1. 帧名重编号为三位零填充从 000 起 —— CharAnim 的帧排序是**字符串序**，不是数值序
  2. 缩放到 512x512 并按**角色包围盒**对齐到现有 loris 素材基线 —— 直接 1000->512 会尺寸不一致、切动画时跳位
  3. 整段动画只算**一次**偏移逐帧套用 —— 否则会抹掉帧间位移（动作本身）
  4. 单目录多序列必须拆开：一个叶子目录里若出现**多于一个文件名前缀**就跳过并告警
     （VPet 常在单目录塞两条序列，如 `1毛笔开心_*` + `2…退出通用_*`、`FLA_*` + `FLB_*`）
  5. 丢弃 1bit/灰度遮罩层（`*_lay` / `front` / `back` 这类不是帧序列）
  6. info.json 只写 rate（由文件名里的 `_<ms>` 后缀折算，取众数）
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

# 池 -> [(源叶子相对路径, 目标变体名), ...]
# 语义说明：Touch_Head/Touch_Body 是「被摸的反应」→ interact；greet 用开心姿势（VPet 无专门打招呼动作）
SPEC = {
    "think": [
        ("Think/Nomal/B", "nomal"),
        ("Think/Happy/B", "happy"),
        ("Think/PoorCondition/B", "poor"),
    ],
    "sleep": [
        ("Sleep/B_Nomal", "loop"),
        ("Sleep/B_Happy", "happy"),
    ],
    "greet": [
        ("IDEL/amusement_B", "amuse"),
        ("IDEL/Meow/Happy/1", "meow"),
    ],
    "say": [
        ("Say/Shining/B_2", "smile"),
        ("Say/Self/B_1", "self"),
        ("Say/Serious/B", "serious"),
    ],
    "work": [
        ("WORK/WorkTWO/A_Nomal", "pc"),
        ("WORK/Study/A_Nomal", "read"),
        ("WORK/WorkONE/A_Nomal", "write"),
    ],
    "interact": [
        # 摸头反应其实是三段：A=进入(中立→抱头) B=保持(抱头) C=退出(抱头→中立)。
        # 三段连着播 = 自然的「被摸 → 回到待机」，单播 B 会在抱头姿势上硬切到待机（用户反馈：突兀）。
        ("Touch_Head/A_Nomal", "a"),
        ("Touch_Head/B_Nomal", "b"),
        ("Touch_Head/C_Nomal", "c"),
    ],
    # 贴边隐藏（VPet SideHide_*）。语义已由**文件名 + 帧序**双重确认（先看图再动手）：
    #   Main = 隐藏时的姿态序列：A(进入 9帧) → B_1(稳定 4帧) → B_2(单帧长保持 500ms) → C(退出 7帧)
    #   Rise = 鼠标靠近时：文件名直接写着「左藏鼠标近普通A/C」= 探出 / 缩回
    #   两侧素材是镜像（Left_* / Right_*），逐侧各导 6 段。
    "edge_hide": [
        ("SideHide_Left_Main/Nomal/A", "left-in"),
        ("SideHide_Left_Main/Nomal/B_1", "left-keep"),
        ("SideHide_Left_Main/Nomal/B_2", "left-hold"),
        ("SideHide_Left_Main/Nomal/C", "left-out"),
        ("SideHide_Left_Rise/Nomal/A", "left-peek"),
        ("SideHide_Left_Rise/Nomal/C", "left-unpeek"),
        ("SideHide_Right_Main/Nomal/A", "right-in"),
        ("SideHide_Right_Main/Nomal/B_1", "right-keep"),
        ("SideHide_Right_Main/Nomal/B_2", "right-hold"),
        ("SideHide_Right_Main/Nomal/C", "right-out"),
        ("SideHide_Right_Rise/Nomal/A", "right-peek"),
        ("SideHide_Right_Rise/Nomal/C", "right-unpeek"),
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


def 导入一个动画(池, 源叶子, 变体, 基线值, 报告):
    frames = sorted(f for f in os.listdir(源叶子) if f.lower().endswith(".png"))
    # 规则 4：单目录多序列 -> 跳过。
    # 判据必须是**帧序号重复**，不能是「文件名前缀不同」——VPet 里有帧名拼写不一致的真实案例：
    #   SideHide_Right_Main/Nomal/A 里 A_000..A_013 少一个 A_011，而第 11 帧被命名成 A01_011。
    # 按前缀判会把它误判成两条序列（实测踩过）；按序号判既能正确合并，也能挡住真正混装的多序列。
    序号集 = [帧序(f) for f in frames]
    if len(set(序号集)) != len(序号集):
        重复 = [s for s, c in Counter(序号集).items() if c > 1]
        报告.append(f"  [跳过] {池}/{变体}: 帧序号重复 {sorted(重复)[:3]} —— 确实混了两条序列，需人工拆目录")
        return 0
    frames = [f for _, f in sorted(zip(序号集, frames))]  # 按帧序号排序（而非文件名序）
    # 规则 5：丢灰度/1bit 遮罩
    有效 = []
    for f in frames:
        im = Image.open(os.path.join(源叶子, f))
        if im.mode in ("1", "L", "LA"):
            报告.append(f"  [丢弃] {池}/{变体}/{f}: 灰度遮罩层（mode={im.mode}）")
            continue
        有效.append(f)
    if not 有效:
        报告.append(f"  [跳过] {池}/{变体}: 无有效帧")
        return 0

    paths = [os.path.join(源叶子, f) for f in 有效]
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
    档 = Counter(时长秒(f) for f in 有效)
    ms = 档.most_common(1)[0][0] or 125
    rate = max(1, round(1000 / ms))
    with open(os.path.join(out, "info.json"), "w", encoding="utf-8") as fp:
        fp.write('{\n    "rate": %d\n}\n' % rate)

    bb = Image.open(os.path.join(out, "000.png")).convert("RGBA").getchannel("A").getbbox()
    报告.append(f"  [完成] {池}/{变体}: {len(有效)}帧 源包围盒={ub[2]-ub[0]}x{ub[3]-ub[1]} scale={scale:.4f} rate={rate} "
              f"-> 角色高={bb[3]-bb[1]} 底边={bb[3]}")
    return len(有效)


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
        for 源, 变体 in 项列表:
            源叶子 = os.path.join(VPET, 源.replace("\\", "/"))
            if not os.path.isdir(源叶子):
                报告.append(f"  [跳过] {池}/{变体}: 源目录不存在 {源叶子}")
                continue
            总帧 += 导入一个动画(池, 源叶子, 变体, 基线值, 报告)
    print("\n".join(报告))
    print(f"\n共导入 {总帧} 帧")


if __name__ == "__main__":
    main()