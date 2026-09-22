"""通用 VPet 动画导入器：VPet vup 资产 -> mods/main_anim/anim/loris/<语义池>/<变体>/

用法： python tools/anim/import_vpet_anim.py            # 导入 SPEC 里全部池
       python tools/anim/import_vpet_anim.py edge_hide  # 只导某个池

硬规则（全部来自实测，见 AIPet-Agent.md §3 / plan.md P2）：
  1. 帧名重编号为三位零填充从 000 起 —— CharAnim 的帧排序是**字符串序**，不是数值序
  2. 缩放到 512x512 并按**角色包围盒**对齐到现有 loris 素材基线 —— 直接 1000->512 会尺寸不一致、切动画时跳位
  3. 整段动画只算**一次**偏移逐帧套用 —— 否则会抹掉帧间位移（动作本身）
  4. 单目录多序列必须拆开：一个叶子目录里若出现**多于一个文件名前缀**就跳过并告警
     （VPet 常在单目录塞两条序列，如 `1毛笔开心_*` + `2…退出通用_*`、`FLA_*` + `FLB_*`）
  5. 丢弃 1bit/灰度遮罩层（`*_lay` / `front` / `back` 这类不是帧序列）
  6. info.json 写 rate（文件名 `_<ms>` 后缀的众数折算基准帧率）+ durations（每帧相对时长，
     ms÷基准取整——原版定格/慢动作节奏靠它还原；缺字段时加载端全按 1 处理，向后兼容）
  7. **帧切片**：VPet 有的目录里混了不属于该段的帧（实测 `SideHide_Right_Main/Nomal/A` 多粘了
     2 帧「迸出」开头 + 3 帧收尾）。片段写法 `(源, 起帧序号, 止帧序号)`（含端点，None = 全段），
     一个变体可以由**多个片段拼接**（顺序即拼接顺序）。
"""
import os
import re
import sys
import json
import shutil
from collections import Counter
from PIL import Image

VPET = r"D:/SteamLibrary/steamapps/common/VPet/mod/0000_core/pet/vup"
DST_ROOT = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "mods", "main_anim", "anim", "loris")
REF = os.path.join(DST_ROOT, "idle/happy-1/000.png")   # 2026-09-20：idle 变体重命名 {档}-{n} 后跟着更新；注意帧名是 3 位（%03d），重导后会覆盖旧 2 位文件
基线快照 = os.path.join(os.path.dirname(os.path.abspath(__file__)), "baseline.json")   # 站位锚点快照（防漂移，见 基线()）
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
    # 待机（核心池）：VPet Default 三档 —— 1/2/3 = Happy 档三条（原来导入为 idle/1..3）。
    # 2026-09-20 组①：补 Nomal/Poor 档并对齐三档系统 —— 变体命名 `{档}-{n}`（idle-happy-1 / idle-nomal-1 / idle-poor-1 …），
    # 三档=开心/不良时 StateMachine 按 `idle-{档}-` 前缀随机取一条（普通/关闭 = 池内随机混演，与 think 等池同口径）。
    "idle": [
        ("happy-1", [("Default/Happy/1", None, None)]),
        ("happy-2", [("Default/Happy/2", None, None)]),
        ("happy-3", [("Default/Happy/3", None, None)]),
        ("nomal-1", [("Default/Nomal/1", None, None)]),
        ("nomal-2", [("Default/Nomal/2", None, None)]),
        ("nomal-3", [("Default/Nomal/3", None, None)]),
        ("poor-1", [("Default/PoorCondition/1", None, None)]),
        ("poor-2", [("Default/PoorCondition/2", None, None)]),
    ],
    "think": [
        ("nomal", [("Think/Nomal/B", None, None)]),
        ("happy", [("Think/Happy/B", None, None)]),
        ("poor", [("Think/PoorCondition/B", None, None)]),
        # 2026-09-20 组①·过渡段：A=进入思考姿态 / C=退出（各 2 帧）。运行时按 `{主名}-a` / `{主名}-c`
        # 解析（StateMachine「包裹段」：进入播 A → 循环 B → 退出播 C）——三档×2 共 6 个变体。
        ("nomal-a", [("Think/Nomal/A", None, None)]),
        ("nomal-c", [("Think/Nomal/C", None, None)]),
        ("happy-a", [("Think/Happy/A_2", None, None)]),
        ("happy-c", [("Think/Happy/C_2", None, None)]),
        ("poor-a", [("Think/PoorCondition/A", None, None)]),
        ("poor-c", [("Think/PoorCondition/C", None, None)]),
    ],
    "sleep": [
        ("loop", [("Sleep/B_Nomal", None, None)]),
        ("happy", [("Sleep/B_Happy", None, None)]),
        # 2026-09-20 组①·过渡段：A=躺下入睡 / C=醒来起身（首尾与站姿衔接，逐帧看图核对过）。
        # 主名 sleep-happy 的段按精确名命中；sleep-loop 的段用池级回退（sleep-a/sleep-c）。
        ("a", [("Sleep/A_Nomal", None, None)]),
        ("c", [("Sleep/C_Nomal", None, None)]),
        ("happy-a", [("Sleep/A_Happy", None, None)]),
        ("happy-c", [("Sleep/C_Happy", None, None)]),
    ],
    "greet": [
        ("amuse", [("IDEL/amusement_B", None, None)]),
        ("meow", [("IDEL/Meow/Happy/1", None, None)]),
    ],
    "say": [
        ("smile", [("Say/Shining/B_2", None, None)]),
        ("self", [("Say/Self/B_1", None, None)]),
        ("serious", [("Say/Serious/B", None, None)]),
        ("shy", [("Say/Shy/B", None, None)]),        # P10：VPet 的害羞档（说话池第 4 个变体）
        # 2026-09-20 组①·过渡段：A=进入说话姿态 / C=退出（与站姿衔接，逐帧看图核对过）。
        ("smile-a", [("Say/Shining/A", None, None)]),
        ("smile-c", [("Say/Shining/C", None, None)]),
        ("self-a", [("Say/Self/A", None, None)]),
        ("self-c", [("Say/Self/C", None, None)]),
        ("serious-a", [("Say/Serious/A", None, None)]),
        ("serious-c", [("Say/Serious/C", None, None)]),
        ("shy-a", [("Say/Shy/A", None, None)]),
        ("shy-c", [("Say/Shy/C", None, None)]),
    ],
    # 干活：VPet 的 WORK 图共 13 种—— 2026-09-20 细节打磨：**改为包裹段结构**（A 进入 → B 干活
    # 循环 → C 收尾）。VPet 语义：干活期间 B 循环、停止时播 `C_End` 回常态（WorkTimer.Stop）。
    # 原先只导了 A 段 → 实机上「反复做准备动作、永远不干活」。现在：主变体 = B（取环内最多帧的
    # B 变体——VPet 每圈随机换 B_n，我们固定最丰富那个）；A/C 拆成 `-a`/`-c` 段（StateMachine
    # 包裹段机制自动按 进入/退出 播放，主段钉死循环；见 `.worktrees` 无关，机制在 StateMachine）。
    # **VPet 的金钱/体力/心情收益一律不抄**（主人 2026-09-19）。
    "work": [
        ("pc",          [("WORK/WorkTWO/B_1_Nomal", None, None)]),
        ("pc-a",        [("WORK/WorkTWO/A_Nomal", None, None)]),
        ("pc-c",        [("WORK/WorkTWO/C_Nomal", None, None)]),
        ("read",        [("WORK/Study/B_1_Nomal", None, None)]),
        ("read-a",      [("WORK/Study/A_Nomal", None, None)]),
        ("read-c",      [("WORK/Study/C_Nomal", None, None)]),
        ("write",       [("WORK/WorkONE/B_1_Nomal", None, None)]),
        ("write-a",     [("WORK/WorkONE/A_Nomal", None, None)]),
        ("write-c",     [("WORK/WorkONE/C_Nomal", None, None)]),
        ("calligraphy",   [("WORK/Calligraphy/Nomal/B", None, None)]),
        ("calligraphy-a", [("WORK/Calligraphy/Nomal/A", None, None)]),
        ("calligraphy-c", [("WORK/Calligraphy/Nomal/C", None, None)]),
        ("paint",     [("WORK/StudyPaint/Nomal/B", None, None)]),
        ("paint-a",   [("WORK/StudyPaint/Nomal/A", None, None)]),
        ("paint-c",   [("WORK/StudyPaint/Nomal/C", None, None)]),
        ("study2",    [("WORK/StudyTWO/Nomal/B", None, None)]),
        ("study2-a",  [("WORK/StudyTWO/Nomal/A", None, None)]),
        ("study2-c",  [("WORK/StudyTWO/Nomal/C", None, None)]),
        ("sausage",   [("WORK/GrilledSausage/Nomal/B", None, None)]),
        ("sausage-a", [("WORK/GrilledSausage/Nomal/A", None, None)]),
        ("sausage-c", [("WORK/GrilledSausage/Nomal/C", None, None)]),
        ("clean",     [("WORK/WorkClean/Nomal/B_1", None, None)]),
        ("clean-a",   [("WORK/WorkClean/Nomal/A", None, None)]),
        ("clean-c",   [("WORK/WorkClean/Nomal/C", None, None)]),
        ("fixmenu",   [("WORK/FixMenu/Nomal/B_2", None, None)]),
        ("fixmenu-a", [("WORK/FixMenu/Nomal/A", None, None)]),
        ("fixmenu-c", [("WORK/FixMenu/Nomal/C", None, None)]),
        ("game",      [("WORK/PlayONE/Nomal/B", None, None)]),
        ("game-a",    [("WORK/PlayONE/Nomal/A", None, None)]),
        ("game-c",    [("WORK/PlayONE/Nomal/C", None, None)]),
        ("water",     [("WORK/PlayWater/Nomal/B", None, None)]),
        ("water-a",   [("WORK/PlayWater/Nomal/A", None, None)]),
        ("water-c",   [("WORK/PlayWater/Nomal/C", None, None)]),
        ("remove",    [("WORK/RemoveObject/Nomal/B", None, None)]),
        ("remove-a",  [("WORK/RemoveObject/Nomal/A", None, None)]),
        ("remove-c",  [("WORK/RemoveObject/Nomal/C", None, None)]),
        ("rope",      [("WORK/RopeSkipping/Nomal/B/1", None, None)]),
        ("rope-a",    [("WORK/RopeSkipping/Nomal/A", None, None)]),
        ("rope-c",    [("WORK/RopeSkipping/Nomal/C", None, None)]),
        # ── 重构#7（2026-09-22）：补 Happy / PoorCondition 档位素材（原只导 Nomal，三档切换时 WORK 无档可换）──
        # 命名 `{档}-{类型}`：档位是第一层轴（与 idle/music 的 `{档}-{n}` 同口径），挑主名 按 `-happy-`/-poor- 前缀收组。
        # 每类型每档 = A + 一条 B 循环 + C（B 取与 Nomal 同名的那条，跨档「同一动作换表情」）。
        # WorkTWO 无 Happy 源、Study 无档位源 → 不补，由降级链兜底（开心/不良档里不会出现它们）。
        ("happy-calligraphy",   [("WORK/Calligraphy/Happy/B", None, None)]),
        ("happy-calligraphy-a", [("WORK/Calligraphy/Happy/A", None, None)]),
        ("happy-calligraphy-c", [("WORK/Calligraphy/Happy/C", None, None)]),
        ("poor-calligraphy",    [("WORK/Calligraphy/PoorCondition/B", None, None)]),
        ("poor-calligraphy-a",  [("WORK/Calligraphy/PoorCondition/A", None, None)]),
        ("poor-calligraphy-c",  [("WORK/Calligraphy/PoorCondition/C", None, None)]),
        ("happy-fixmenu",   [("WORK/FixMenu/Happy/B_2", None, None)]),
        ("happy-fixmenu-a", [("WORK/FixMenu/Happy/A", None, None)]),
        ("happy-fixmenu-c", [("WORK/FixMenu/Happy/C", None, None)]),
        ("poor-fixmenu",    [("WORK/FixMenu/PoorCondition/B_2", None, None)]),
        ("poor-fixmenu-a",  [("WORK/FixMenu/PoorCondition/A", None, None)]),
        ("poor-fixmenu-c",  [("WORK/FixMenu/PoorCondition/C", None, None)]),
        ("happy-sausage",   [("WORK/GrilledSausage/Happy/B", None, None)]),
        ("happy-sausage-a", [("WORK/GrilledSausage/Happy/A", None, None)]),
        ("happy-sausage-c", [("WORK/GrilledSausage/Happy/C", None, None)]),
        ("poor-sausage",    [("WORK/GrilledSausage/PoorCondition/B", None, None)]),
        ("poor-sausage-a",  [("WORK/GrilledSausage/PoorCondition/A", None, None)]),
        ("poor-sausage-c",  [("WORK/GrilledSausage/PoorCondition/C", None, None)]),
        ("happy-game",   [("WORK/PlayONE/Happy/B", None, None)]),
        ("happy-game-a", [("WORK/PlayONE/Happy/A", None, None)]),
        ("happy-game-c", [("WORK/PlayONE/Happy/C", None, None)]),
        ("poor-game",    [("WORK/PlayONE/PoorCondition/B", None, None)]),
        ("poor-game-a",  [("WORK/PlayONE/PoorCondition/A", None, None)]),
        ("poor-game-c",  [("WORK/PlayONE/PoorCondition/C", None, None)]),
        ("happy-water",   [("WORK/PlayWater/Happy/B", None, None)]),
        ("happy-water-a", [("WORK/PlayWater/Happy/A", None, None)]),
        ("happy-water-c", [("WORK/PlayWater/Happy/C", None, None)]),
        ("poor-water",    [("WORK/PlayWater/PoorCondition/B", None, None)]),
        ("poor-water-a",  [("WORK/PlayWater/PoorCondition/A", None, None)]),
        ("poor-water-c",  [("WORK/PlayWater/PoorCondition/C", None, None)]),
        ("happy-remove",   [("WORK/RemoveObject/Happy/B", None, None)]),
        ("happy-remove-a", [("WORK/RemoveObject/Happy/A", None, None)]),
        ("happy-remove-c", [("WORK/RemoveObject/Happy/C", None, None)]),
        ("poor-remove",    [("WORK/RemoveObject/PoorCondition/B", None, None)]),
        ("poor-remove-a",  [("WORK/RemoveObject/PoorCondition/A", None, None)]),
        ("poor-remove-c",  [("WORK/RemoveObject/PoorCondition/C", None, None)]),
        ("happy-rope",   [("WORK/RopeSkipping/Happy/B/1", None, None)]),
        ("happy-rope-a", [("WORK/RopeSkipping/Happy/A", None, None)]),
        ("happy-rope-c", [("WORK/RopeSkipping/Happy/C", None, None)]),
        ("poor-rope",    [("WORK/RopeSkipping/PoorCondition/B/1", None, None)]),
        ("poor-rope-a",  [("WORK/RopeSkipping/PoorCondition/A", None, None)]),
        ("poor-rope-c",  [("WORK/RopeSkipping/PoorCondition/C", None, None)]),
        ("happy-paint",   [("WORK/StudyPaint/Happy/B", None, None)]),
        ("happy-paint-a", [("WORK/StudyPaint/Happy/A", None, None)]),
        ("happy-paint-c", [("WORK/StudyPaint/Happy/C", None, None)]),
        ("poor-paint",    [("WORK/StudyPaint/PoorCondition/B", None, None)]),
        ("poor-paint-a",  [("WORK/StudyPaint/PoorCondition/A", None, None)]),
        ("poor-paint-c",  [("WORK/StudyPaint/PoorCondition/C", None, None)]),
        ("happy-study2",   [("WORK/StudyTWO/Happy/B_3", None, None)]),
        ("happy-study2-a", [("WORK/StudyTWO/Happy/A", None, None)]),
        # study2 的 Happy 源没有 C 段（VPet 自己留白）——退出段走 段名() 的「同类无档」降级（work-study2-c），
        # 这正是 VPet「每段动画各自找档」的语义，不要补假素材。
        ("poor-study2",   [("WORK/StudyTWO/PoorCondition/B_3", None, None)]),
        ("poor-study2-a", [("WORK/StudyTWO/PoorCondition/A", None, None)]),
        ("poor-study2-c", [("WORK/StudyTWO/PoorCondition/C", None, None)]),
        ("happy-clean",   [("WORK/WorkClean/Happy/B_1", None, None)]),
        ("happy-clean-a", [("WORK/WorkClean/Happy/A", None, None)]),
        ("happy-clean-c", [("WORK/WorkClean/Happy/C", None, None)]),
        ("poor-clean",    [("WORK/WorkClean/PoorCondition/B_1", None, None)]),
        ("poor-clean-a",  [("WORK/WorkClean/PoorCondition/A", None, None)]),
        ("poor-clean-c",  [("WORK/WorkClean/PoorCondition/C", None, None)]),
        ("happy-write",   [("WORK/WorkONE/Happy/B", None, None)]),
        ("happy-write-a", [("WORK/WorkONE/Happy/A", None, None)]),
        ("happy-write-c", [("WORK/WorkONE/Happy/C", None, None)]),
        ("poor-write",    [("WORK/WorkONE/PoorCondition/B", None, None)]),
        ("poor-write-a",  [("WORK/WorkONE/PoorCondition/A", None, None)]),
        ("poor-write-c",  [("WORK/WorkONE/PoorCondition/c", None, None)]),   # 源目录就小写 c（WorkONE 唯一一处）
        ("poor-pc",   [("WORK/WorkTWO/PoorCondition/B_1", None, None)]),
        ("poor-pc-a", [("WORK/WorkTWO/PoorCondition/A", None, None)]),
        ("poor-pc-c", [("WORK/WorkTWO/PoorCondition/C", None, None)]),
    ],
    "interact": [
        # 摸头反应其实是三段：A=进入(中立→抱头) B=保持(抱头) C=退出(抱头→中立)。
        # 三段连着播 = 自然的「被摸 → 回到待机」，单播 B 会在抱头姿势上硬切到待机（用户反馈：突兀）。
        ("a", [("Touch_Head/A_Nomal", None, None)]),
        ("b", [("Touch_Head/B_Nomal", None, None)]),
        ("c", [("Touch_Head/C_Nomal", None, None)]),
    ],
    # 捏脸（VPet `Pinch`）—— 官方语义：**长按脸**触发（教程「11/24 Update Pinch Face：Long press on the face to pinch the face」
    # + `MainWindow.DisplayPinch`）：A(1帧 进入) → B(6帧 **循环**，按住时连续播) → C(21帧 松手退出)。
    # 命中区在 .lps：`pinch: px#149 py#128 sw#56 sh#59`（500 空间）。
    # 只导 **Nomal** 三段：官方还有 Happy/PoorCondition 变体（按它自己的 Mode 选），
    # 但我们的心情定义与官方不同 → **不做这个映射**（主人 2026-09-19）；数值效果同理不抄。
    "pinch": [
        ("a", [("Pinch/Nomal/A", None, None)]),
        ("b", [("Pinch/Nomal/B", None, None)]),
        ("c", [("Pinch/Nomal/C", None, None)]),
    ],
    # ── 组③（2026-09-20）：音乐反应（VPet `Music/*`；语义 = 官方 `MainWindow.cs` Handle_Music/MusicTimer）──
    # 官方逻辑：系统输出峰值音量持续 3 秒 > MusicCatch → 起身跳舞；音量平均再超 MusicMax → 换「Single」档；
    #   安静后播 C_End 收场。A=起跳过渡（1帧）/ B=三档舞蹈循环（带音符特效；Happy>Nomal>Poor 欢快度）/
    #   C=收尾（取 Nomal_1；另有 Happy/Poor/Nomal_2 变体未用）/ Single=轻快摇摆（14帧，表情三版）。
    # 运行时 = StateMachine「包裹段」（music 池已入包裹池）：A → 主段（按档随机舞蹈 或 嗨档 Single）→ C。
    "music": [
        ("a", [("Music/A/Nomal", None, None)]),
        ("c", [("Music/C/Nomal_1", None, None)]),
        ("nomal-1", [("Music/B/Nomal_1", None, None)]),
        ("nomal-2", [("Music/B/Nomal_2", None, None)]),
        ("nomal-3", [("Music/B/Nomal/3", None, None)]),
        ("nomal-4", [("Music/B/Nomal/4", None, None)]),
        ("nomal-5", [("Music/B/Nomal/5", None, None)]),
        ("happy-1", [("Music/B/Happy_1", None, None)]),
        ("happy-2", [("Music/B/Happy_2", None, None)]),
        ("happy-3", [("Music/B/Happy/3", None, None)]),
        ("happy-4", [("Music/B/Happy/4", None, None)]),
        ("poor-1", [("Music/B/PoorCondition/1", None, None)]),
        ("poor-2", [("Music/B/PoorCondition/2", None, None)]),
        ("poor-3", [("Music/B/PoorCondition/3", None, None)]),
        ("poor-4", [("Music/B/PoorCondition/5", None, None)]),
        ("single-nomal", [("Music/Single/Nomal", None, None)]),
        ("single-happy", [("Music/Single/Happy", None, None)]),
        ("single-poor", [("Music/Single/PoorCondition", None, None)]),
    ],
    # ── 组②（2026-09-20）：爬边 / 顶爬 / 趴行 / 掉落（VPet `MOVE/*`，语义见 `vup.lps` move 行 + GraphHelper.Move）──
    # 侧边爬：A=扑向墙上挂住 B=手脚交替爬（循环，方向由窗口位移决定：Y±10/125ms）C=脱手回站姿。
    #   官方吸附：`LocateType Left/Right` → A 播完把窗口推出屏外 LocateLength（左 145 / 右 185 @Zoom1）。
    # 顶边爬：A=抓住顶边 B=沿顶边横爬（循环，X±8/125ms）C=离开。官方 `LocateType Top`，推出屏顶 150。
    #   注意素材里顶爬角色是横置构图的（挂在顶边、身体垂在屏内）。
    # 趴行：A=趴下 B=贴地爬行（循环）C=起身——**当走动的慢速变体**用（SpeedX 10 vs walk 14），不进行为链。
    # 掉落：A=脱手 B=横着下落（循环）C=落地起身（右版 21 帧长起身）。官方 fall 自带横向漂移（X±14）。
    #   只导 Nomal（与 pinch/switch 同口径：官方按它自己的 Mode 选档，我们的心情定义不同 → 不映射）。
    "climb": [
        ("left-a",  [("MOVE/climb.left/A_Nomal", None, None)]),
        ("left-b",  [("MOVE/climb.left/B_Nomal", None, None)]),
        ("left-c",  [("MOVE/climb.left/C_Nomal", None, None)]),
        ("right-a", [("MOVE/climb.right/A_Nomal", None, None)]),
        ("right-b", [("MOVE/climb.right/B_Nomal", None, None)]),
        ("right-c", [("MOVE/climb.right/C_Nomal", None, None)]),
    ],
    "climb_top": [
        ("left-a",  [("MOVE/climb.top.left/A_Nomal", None, None)]),
        ("left-b",  [("MOVE/climb.top.left/B/Nomal", None, None)]),
        ("left-c",  [("MOVE/climb.top.left/C_Nomal", None, None)]),
        ("right-a", [("MOVE/climb.top.right/A_Nomal", None, None)]),
        ("right-b", [("MOVE/climb.top.right/B/Nomal", None, None)]),
        ("right-c", [("MOVE/climb.top.right/C_Nomal", None, None)]),
    ],
    "crawl": [
        ("left",  [("MOVE/crawl.left/B_Nomal", None, None)]),
        ("right", [("MOVE/crawl.right/B_Nomal", None, None)]),
    ],
    "fall": [
        ("left-a",  [("MOVE/fall.left/A_Nomal", None, None)]),
        ("left-b",  [("MOVE/fall.left/B_Nomal", None, None)]),
        # C_Nomal 里混了两条命名序列（FLA 触地 7 帧 + FLB 起身 14 帧，共享帧序号）→ 按前缀拆开顺序拼接
        ("left-c",  [("MOVE/fall.left/C_Nomal", None, None, "FLA"),
                     ("MOVE/fall.left/C_Nomal", None, None, "FLB")]),
        ("right-a", [("MOVE/fall.right/A_Nomal", None, None)]),
        ("right-b", [("MOVE/fall.right/B_Nomal", None, None)]),
        ("right-c", [("MOVE/fall.right/C_Nomal", None, None, "FRA"),
                     ("MOVE/fall.right/C_Nomal", None, None, "FRB")]),
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
    # 走路：**快/慢 = 心情档**（VPet 里 `walk.*.faster` 就是 Happy、`walk.*.slow` 就是 PoorCondition 的走法）
    # —— 与我们「三档状态 / 心情择档」天然对齐，不要当成两个独立速度档。
    "walk": [
        # 2026-09-20 细节打磨：补 A/C 起步/停步段（`-a`/`-c` 结尾 → CharAnim 自动非循环加载，
        #   状态机走链按 WalkStart/WalkLoop/WalkEnd 三段播；正常/快/慢三档 src 段名不同：
        #   Nomal / Happy / PoorCondition）。
        ("left",      [("MOVE/walk.left/B_Nomal", None, None)]),
        ("right",     [("MOVE/walk.right/B_Nomal", None, None)]),
        ("left-fast", [("MOVE/walk.left.faster/B_Happy", None, None)]),
        ("right-fast",[("MOVE/walk.right.faster/B_Happy", None, None)]),
        ("left-slow", [("MOVE/walk.left.slow/B_PoorCondition_1", None, None)]),
        ("right-slow",[("MOVE/walk.right.slow/B_PoorCondition_1", None, None)]),
        ("left-a",       [("MOVE/walk.left/A_Nomal", None, None)]),
        ("left-c",       [("MOVE/walk.left/C_Nomal", None, None)]),
        ("right-a",      [("MOVE/walk.right/A_Nomal", None, None)]),
        ("right-c",      [("MOVE/walk.right/C_Nomal", None, None)]),
        ("left-fast-a",  [("MOVE/walk.left.faster/A_Happy", None, None)]),
        ("left-fast-c",  [("MOVE/walk.left.faster/C_Happy", None, None)]),
        ("right-fast-a", [("MOVE/walk.right.faster/A_Happy", None, None)]),
        ("right-fast-c", [("MOVE/walk.right.faster/C_Happy", None, None)]),
        ("left-slow-a",  [("MOVE/walk.left.slow/PoorCondition_A", None, None)]),
        ("left-slow-c",  [("MOVE/walk.left.slow/PoorCondition_C", None, None)]),
        ("right-slow-a", [("MOVE/walk.right.slow/PoorCondition_A", None, None)]),
        ("right-slow-c", [("MOVE/walk.right.slow/PoorCondition_C", None, None)]),
    ],
    # 干活进出场：VPet `Switch_Up`（起身开工）/ `Switch_Down`（收工坐下）—— P10 小件三连之一。
    "switch": [
        ("up",   [("Switch/Up/Nomal", None, None)]),
        ("down", [("Switch/Down/Nomal", None, None)]),
    ],
    # 摸身体（P10）：VPet `Touch_Body`（官方只有 Happy/ill 两档；取 Happy）+ `Happy_Turn`（被摸转身）。
    # 命中区在 .lps：`touchbody: px#166 py#206 sw#163 sh#136`（500 空间，换算见 config/behavior.json 的 摸身体命中区）。
    "interact_body": [
        ("a", [("Touch_Body/A_Happy/tb1", None, None)]),
        ("b", [("Touch_Body/B_Happy/tb1", None, None)]),
        ("c", [("Touch_Body/C_Happy/tb1", None, None)]),
    ],
    "turn": [
        ("a", [("Touch_Body/Happy_Turn/A", None, None)]),
        ("b", [("Touch_Body/Happy_Turn/B", None, None)]),
        ("c", [("Touch_Body/Happy_Turn/C", None, None)]),
    ],
    # 追加模式（`+`）：这两个池里混着非 VPet 来源的变体，只补、不清空。
    # 摸头的高兴档（VPet Touch_Head/Happy）→ `interact-happy-a/b/c`，三档状态=开心时用它。
    "interact+": [
        ("happy-a", [("Touch_Head/Happy/A", None, None)]),
        ("happy-b", [("Touch_Head/Happy/B", None, None)]),
        ("happy-c", [("Touch_Head/Happy/C", None, None)]),
    ],
    # 待机小动作扩充：VPet IDEL 的 蹲 / 网球 / 泡泡 / 打呼噜 / 侧看（我们原有 bubble/doze/meow/meowlook/spin/yawning）
    "fidget+": [
        # 2026-09-22 重构#2（B 循环概率退出）：七个 A/B/C 结构变体**拆回三段**（`-a`/主段/`-c`，
        # 与包裹段同一命名约定）——运行时 fidget 会话 = A → B 循环 × 骰子 → C → idle，
        # 骰子对齐 VPet MainDisplay.cs:314-320 DisplayBLoopingToNomal：
        #   每播完第 n 圈掷 Rnd.Next(n+1) > L（L 来自 lps duration 表；我们默认 2 = 最少 3 圈、平均 ~4.6 圈）。
        # 此前是「A+B+C 拼一条一次过」（B 只播一遍 ~5s 就完）——VPet 原味是 B 循环到骰子命中才退
        #（squat 蹲一下平均十几秒、间歇性发作），观感「活」的关键。
        # 单段变体（spin/bubble/doze/meow/meowlook/yawning/happy520 = VPet Single 型，
        # MainDisplay.cs:291-296 播完直接 DisplayToNomal）**保持一次过**，不上骰子。
        # 近重复变体只收一次（逐帧实测）：squat 的 B1/B2/B3 互差 0.06、aside 的 B/B_2/B_3/B_4
        # 互差 0.3~1.0（近重复）；tennis 的 B/B_2 是 B_3 的抽样副本 → 只收 B_3（24 帧完整挥拍循环）。
        ("squat-a",   [("IDEL/Squat/A_Nomal", None, None)]),
        ("squat",     [("IDEL/Squat/B_Nomal/1", None, None)]),
        ("squat-c",   [("IDEL/Squat/C_Nomal", None, None)]),
        ("tennis-a",  [("IDEL/Tennis/Nomal/A", None, None)]),
        ("tennis",    [("IDEL/Tennis/Nomal/B_3", None, None)]),
        ("tennis-c",  [("IDEL/Tennis/Nomal/C", None, None)]),
        ("bubbles-a", [("IDEL/Bubbles/A", None, None)]),
        ("bubbles",   [("IDEL/Bubbles/B", None, None)]),
        ("bubbles-c", [("IDEL/Bubbles/C", None, None)]),
        ("boring-a",  [("IDEL/Boring/A_Nomal", None, None)]),
        ("boring",    [("IDEL/Boring/B_Nomal", None, None)]),
        ("boring-c",  [("IDEL/Boring/C_Nomal", None, None)]),
        ("aside-a",   [("IDEL/aside/Nomal/A", None, None)]),
        ("aside",     [("IDEL/aside/Nomal/B", None, None)]),
        ("aside-c",   [("IDEL/aside/Nomal/C", None, None)]),
        # State 待机变体 —— VPet StateONE=坐下待机 / StateTWO=躺下休息（逐帧看图核对过：
        # A=进入过渡、B=循环微动、C=退出过渡）。state-one 主段 = B/1+B/2 两变体拼一圈（~4.25s）。
        ("state-one-a", [("State/StateONE/A_Nomal", None, None)]),
        ("state-one",   [("State/StateONE/B_Nomal/1", None, None), ("State/StateONE/B_Nomal/2", None, None)]),
        ("state-one-c", [("State/StateONE/C_Nomal", None, None)]),
        ("state-two-a", [("State/StateTWO/A_Nomal", None, None)]),
        ("state-two",   [("State/StateTWO/B_Nomal", None, None)]),
        ("state-two-c", [("State/StateTWO/C_Nomal", None, None)]),
        # IDEL 彩蛋：一连串比心/爱心（VPet happy_like520，Single 型一次过）。
        ("happy520", [("IDEL/happy_like520", None, None)]),
    ],
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
    # 生日彩蛋（2026-09-20 组①）：VPet BDay —— A(惊喜进入) → B(开心摇摆 ~5.6s) → C(比心退出)。
    # 触发：config/config.json 的「生日」（MM-dd）命中当天 → 入场完成后播一遍（Main 接线）。
    "bday": [
        ("a", [("BDay/A", None, None)]),
        ("b", [("BDay/B", None, None)]),
        ("c", [("BDay/C", None, None)]),
    ],
}


def 帧序(name):
    """从 `<前缀>_<序号>_<时长>.png` 抽帧序号（按序号排序，不按文件名——见规则 4 的说明）。"""
    m = re.search(r"_(\d+)_(\d+)\.png$", name)
    return int(m.group(1)) if m else 0


def 帧前缀(name):
    """取 `前缀_帧序_时长.png` 里的前缀（注意别和 收集片段 里的局部变量 `前缀` 混了——2026-09-22 踩过遮蔽坑）；不符合该结构返回 None。"""
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
    """角色站位锚点（底边 y / 中心 x / 角色高）——冻结在 tools/anim/baseline.json 里。
    2026-09-22：旧实现每轮从 REF（= 导入产物 idle/happy-1）现场算，全量重导时
    「拿产物当基准」自反馈，每轮漂移 +8px（教训见 43b4998 提交说明）。
    快照一旦生成就不再变——所有导入对齐同一锚点；删掉快照才会从 REF 重新生成。"""
    if os.path.isfile(基线快照):
        with open(基线快照, encoding="utf-8") as fp:
            return json.load(fp)
    bb = Image.open(REF).convert("RGBA").getchannel("A").getbbox()
    d = {"bottom": bb[3], "cx": (bb[0] + bb[2]) / 2, "h": bb[3] - bb[1]}
    with open(基线快照, "w", encoding="utf-8") as fp:
        json.dump(d, fp, ensure_ascii=False, indent=1)
    print(f"[基线] 首次生成快照 -> {基线快照}（此后不再从 REF 现场计算）")
    return d


def 收集片段(片段列表, 报告):
    """按片段收集 (绝对路径, 文件名) 列表：逐源排序 → 序号去重检查 → 灰度遮罩丢弃 → 帧区间切片。
    片段 = (源, 起帧, 止帧) 或 (源, 起帧, 止帧, 文件名前缀)（含端点，None = 全段）。
    前缀版用于「一个目录混了两条命名序列」：如 `fall*/C_Nomal` = FLA_000..006（触地）+ FLB_000..013（起身），
    两条序列共享帧序号，序号去重挡下 → 只能按前缀先拆、再各自排序/切片（两个片段按顺序拼接）。"""
    结果 = []
    for 片段 in 片段列表:
        if len(片段) == 4:
            源, 起, 止, 前缀 = 片段
        else:
            源, 起, 止 = 片段
            前缀 = None
        源叶子 = os.path.join(VPET, 源.replace("\\", "/"))
        if not os.path.isdir(源叶子):
            报告.append(f"  [跳过] {源}: 源目录不存在")
            continue
        frames = sorted(f for f in os.listdir(源叶子) if f.lower().endswith(".png"))
        if 前缀 is not None:
            frames = [f for f in frames if f.startswith(前缀)]
            if not frames:
                报告.append(f"  [跳过] {源}: 前缀 {前缀} 没有帧")
                continue
        # 规则 4：判据必须是**帧序号重复**，不能是「文件名前缀不同」——VPet 里有帧名拼写不一致的真实案例：
        #   SideHide_Right_Main/Nomal/A 里 A_000..A_013 少一个 A_011，而第 11 帧被命名成 A01_011。
        # 2026-09-22 细化：重号分两种处置——
        #   ① 前缀相同（如 RopeSkipping/Happy/C 的 跳绳开心_000_124 + 跳绳开心_000_125）：源作者没重编号，
        #      仍是同一序列 → **保留**（排序键退化为「序号, 文件名」= 与源收录顺序一致）；
        #   ② 前缀不同（FLA_/FLB_ 两条序列混装）→ 真混装，跳过，用 4 元组的前缀切片拆。
        序号集 = [帧序(f) for f in frames]
        if len(set(序号集)) != len(序号集):
            重复序 = {s for s, c in Counter(序号集).items() if c > 1}
            重复帧 = [f for f in frames if 帧序(f) in 重复序]
            if len({帧前缀(f) for f in 重复帧}) > 1:
                报告.append(f"  [跳过] {源}: 帧序号重复 {sorted(重复序)[:3]}（跨前缀）—— 确实混了两条序列，需人工拆目录")
                continue
            报告.append(f"  [保留] {源}: 帧序号重复 {sorted(重复序)[:3]}（同前缀 = 源未重编号，收入全部帧）")
        frames = [f for _, f in sorted(zip(序号集, frames))]  # 按（帧序号, 文件名）排序（而非纯文件名序）
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

    # 规则 6（2026-09-22 逐帧时长版）：rate = 文件名时长档众数的基准帧率；
    # durations = 每帧相对时长（ms ÷ 基准帧时长，取整；缺失/笔误容错为 1）。
    # 原版 6181 帧里 125ms 占 92.6%，其余全是 125 的整数倍（250=爬墙慢动作、500=咀嚼停顿、
    # 1000+=长定格）——统一 rate 会把这些节奏全压平（见 document/VPet动画系统分析.md §3.1）。
    档 = Counter(时长秒(f) for _, f in frames)
    ms = 档.most_common(1)[0][0] or 125
    rate = max(1, round(1000 / ms))
    基准 = 1000.0 / rate
    durations = []
    for _, f in frames:
        d = 时长秒(f)
        durations.append(max(1, round(d / 基准)) if d else 1)
    with open(os.path.join(out, "info.json"), "w", encoding="utf-8") as fp:
        fp.write('{\n    "rate": %d,\n    "durations": [%s]\n}\n' % (rate, ", ".join(map(str, durations))))

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
    for 池原始, 项列表 in SPEC.items():
        # 池名末尾 `+` = **追加模式**：只补变体、不清空池目录。
        # 用在「池里混着非 VPet 来源的变体」时（如 fidget/interact 里有原项目素材）——整池重导会误删它们。
        追加 = 池原始.endswith("+")
        池 = 池原始.rstrip("+")
        if only and 池 not in only and 池原始 not in only:
            continue
        池目录 = os.path.join(DST_ROOT, 池)
        if not 追加 and os.path.isdir(池目录):
            # 整池重导：先清空池目录，避免旧变体残留（`进入状态` 是池内随机取一项）
            shutil.rmtree(池目录)
        报告.append(f"### {池}{'（追加）' if 追加 else ''}")
        for 变体, 片段列表 in 项列表:
            总帧 += 导入一个动画(池, 变体, 片段列表, 基线值, 报告)
    print("\n".join(报告))
    print(f"\n共导入 {总帧} 帧")


if __name__ == "__main__":
    main()
