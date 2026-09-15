"""导入 VPet 走路资产 -> mods/main_anim/anim/loris/walk/{left,right}/

规则（对齐 AGENTS.md §10 / plan.md P2）：
  1. 帧名重编号为三位零填充从 000 起（CharAnim 的帧排序是字符串序，非数值序）
  2. 缩放到 512x512 画布，且**按角色包围盒对齐**到现有 loris 素材的基线，
     避免切换动画时角色跳位/变大变小（直接 1000->512 缩放会导致尺寸不一致）
  3. 对齐用「整段动画的并集包围盒」算一次偏移，逐帧套用同一偏移 —— 保住帧间位移（走动的动作本身）
  4. info.json 只写 rate（125ms/帧 -> 8fps）
"""
import os
import shutil
from PIL import Image

SRC = r"D:/SteamLibrary/steamapps/common/VPet/mod/0000_core/pet/vup/MOVE"
DST = r"D:/Games/Github/AIPet/mods/main_anim/anim/loris/walk"
REF = r"D:/Games/Github/AIPet/mods/main_anim/anim/loris/idle/1/00.png"
CANVAS = 512
RATE = 8
VARIANT = "B_Nomal"   # A/B/C 是 VPet 的体态档；取中间档，6 帧最平滑

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

def main():
    # 参考基线：现有 loris 待机帧的角色底边中心
    ref_bb = Image.open(REF).convert("RGBA").getchannel("A").getbbox()
    ref_bottom = ref_bb[3]
    ref_cx = (ref_bb[0] + ref_bb[2]) / 2
    ref_h = ref_bb[3] - ref_bb[1]
    print(f"参考基线: 底边={ref_bottom} 中心x={ref_cx} 角色高={ref_h}")

    for side, src_dir in (("left", "walk.left"), ("right", "walk.right")):
        d = os.path.join(SRC, src_dir, VARIANT)
        if not os.path.isdir(d):
            print(f"跳过（目录不存在）: {d}")
            continue
        frames = sorted(f for f in os.listdir(d) if f.lower().endswith(".png"))
        paths = [os.path.join(d, f) for f in frames]
        ub = union_bbox(paths)
        src_h = ub[3] - ub[1]
        scale = ref_h / src_h
        # 逐帧套用的统一偏移：把缩放后的并集包围盒底边中心，对齐到参考底边中心
        off_x = ref_cx - ((ub[0] + ub[2]) / 2) * scale
        off_y = ref_bottom - ub[3] * scale

        out = os.path.join(DST, side)
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
        with open(os.path.join(out, "info.json"), "w", encoding="utf-8") as fp:
            fp.write('{\n    "rate": %d\n}\n' % RATE)

        nb = Image.open(os.path.join(out, "000.png")).convert("RGBA").getchannel("A").getbbox()
        print(f"{side}: {len(paths)} 帧 并集高={src_h} scale={scale:.4f} -> 已写入 {out}（首帧角色高={nb[3]-nb[1]} 底边={nb[3]}）")

if __name__ == "__main__":
    main()