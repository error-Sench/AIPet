"""新画素材质检器：导入前先把「风格/规格及格线」过一遍（对着官方基线打分）。

用法： python tools/anim/qc_new_asset.py            # 检 new_assets 下全部池
       python tools/anim/qc_new_asset.py attack     # 只检某池

检查项（阈值来自官方帧实测，见 document/游戏动画需求清单.md §一）：
  1. 画布 512×512 + RGBA 透明背景
  2. 站位：底边（脚线）在 495~505（基线 500）；角色高 440~510（基线 489）——腾空帧可豁免高度下限
  3. 颜色数 ≤ 9,000（官方 3,100~6,300；AI 重绘会爆到 13,000+）
  4. 纯黑线稿占比 5%~18%（官方 7~15%；发灰/缺线都出界）
  5. 局部色差噪点 ≤ 26%（官方 ≤ 22.7）
  6. 帧名规范 <前缀>_<三位序号>_<毫秒>.png；序号连续从 000 起
每项 PASS/WARN/FAIL；FAIL 会阻断导入建议（先修再导）。
"""
import os
import re
import sys
import glob
from PIL import Image

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "new_assets")
基线 = {"bottom": 500, "cx": 270.5, "h": 489}
帧名RE = re.compile(r"^(?P<前缀>.+)_(?P<序号>\d{3})_(?P<ms>\d+)\.png$")


def 检一帧(路径):
    问题, 提示 = [], []
    im = Image.open(路径)
    if im.size != (512, 512):
        问题.append(f"画布 {im.size} ≠ 512×512")
    if im.mode != "RGBA":
        im = im.convert("RGBA")
        提示.append("非 RGBA（已自动转换检查，导出时请用 RGBA）")
    bb = im.getbbox()
    if bb is None:
        问题.append("全透明空帧")
        return 问题, 提示, None
    底边, 高 = bb[3], bb[3] - bb[1]
    if not (495 <= 底边 <= 505):
        问题.append(f"脚线 y={底边}（应在 {基线['bottom']}±5）")
    if 高 > 510:
        问题.append(f"角色高 {高} 超上限 510")
    elif 高 < 440:
        提示.append(f"角色高 {高} 偏小（蹲压/腾空帧正常；站立帧应对齐 489±25）")

    px = list(im.getdata())
    不透 = [p for p in px if p[3] > 0]
    色数 = len({(r, g, b) for r, g, b, a in 不透})
    if 色数 > 9000:
        问题.append(f"颜色数 {色数:,} > 9,000（官方 ≤6,300；渐变噪点嫌疑）")
    黑 = sum(1 for r, g, b, a in 不透 if a > 240 and (r + g + b) // 3 < 40)
    黑占比 = 黑 * 100.0 / max(1, len(不透))
    if not (5.0 <= 黑占比 <= 18.0):
        问题.append(f"纯黑线稿占比 {黑占比:.1f}%（官方 7~15%；描边发灰或缺失）")

    # 噪点：相邻像素大色差密度（采样步长 3）
    w, h = im.size
    d = im.load()
    突变 = 样本 = 0
    for y in range(60, 460, 3):
        for x in range(60, 460, 3):
            a0, a1, a2 = d[x, y], d[x + 1, y], d[x, y + 1]
            if a0[3] < 200 or a1[3] < 200 or a2[3] < 200:
                continue
            样本 += 1
            if (abs(a0[0] - a1[0]) + abs(a0[1] - a1[1]) + abs(a0[2] - a1[2]) > 90
                    or abs(a0[0] - a2[0]) + abs(a0[1] - a2[1]) + abs(a0[2] - a2[2]) > 90):
                突变 += 1
    噪 = 突变 * 100.0 / max(1, 样本)
    if 噪 > 26.0:
        问题.append(f"噪点密度 {噪:.1f}% > 26（官方 ≤22.7）")
    提示.append(f"高={高} 脚线={底边} 色数={色数:,} 黑线={黑占比:.1f}% 噪点={噪:.1f}%")
    return 问题, 提示, bb


def 检池(池):
    目录 = os.path.join(ROOT, 池)
    if not os.path.isdir(目录):
        print(f"[跳过] {池}: 目录不存在")
        return True
    全过 = True
    for 向 in sorted(os.listdir(目录)):
        帧们 = sorted(glob.glob(os.path.join(目录, 向, "*.png")))
        if not 帧们:
            continue
        print(f"—— {池}/{向}（{len(帧们)} 帧）")
        序号, 前缀集 = [], set()
        for fp in 帧们:
            m = 帧名RE.match(os.path.basename(fp))
            if not m:
                print(f"  [FAIL] {os.path.basename(fp)}: 帧名不符 <前缀>_<000>_<ms>.png")
                全过 = False
                continue
            前缀集.add(m.group("前缀"))
            序号.append(int(m.group("序号")))
            问题, 提示, _ = 检一帧(fp)
            tag = "FAIL" if 问题 else ("WARN" if any("偏小" in t for t in 提示) else "PASS")
            print(f"  [{tag}] {os.path.basename(fp)}  {提示[-1] if 提示 else ''}")
            for q in 问题:
                print(f"        ✗ {q}")
                全过 = False
            for t in 提示[:-1]:
                print(f"        · {t}")
        if len(前缀集) > 1:
            print(f"  [FAIL] 同目录多前缀 {sorted(前缀集)}（导入器会整目录跳过）")
            全过 = False
        if 序号 and (序号 != list(range(len(序号)))):
            print(f"  [FAIL] 序号不连续/不从 000 起：{序号}")
            全过 = False
    return 全过


if __name__ == "__main__":
    池们 = sys.argv[1:] or [d for d in sorted(os.listdir(ROOT))
                          if os.path.isdir(os.path.join(ROOT, d)) and not d.startswith("_")]
    结果 = {池: 检池(池) for 池 in 池们}
    print("\n===== 汇总 =====")
    for 池, ok in 结果.items():
        print(f"  {池}: {'✔ 可导入' if ok else '✘ 有 FAIL，先修再导'}")
    sys.exit(0 if all(结果.values()) else 1)
