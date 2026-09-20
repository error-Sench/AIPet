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
REF = os.path.join(DST_ROOT, "idle/happy-1/000.png")   # 2026-09-20：idle 变体重命名 {档}-{n} 后跟着更新；注意帧名是 3 位（%03d），重导后会覆盖旧 2 位文件
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
    # 干活：VPet 的 WORK 图共 13 种（直播/学习/写字 已导）—— P10 补齐余下 10 种（Nomal 段，与既有三个同口径）。
    # 池内随机播 → 干活不再千篇一律。**VPet 的金钱/体力/心情收益一律不抄**（主人 2026-09-19）。
    "work": [
        ("pc", [("WORK/WorkTWO/A_Nomal", None, None)]),
        ("read", [("WORK/Study/A_Nomal", None, None)]),
        ("write", [("WORK/WorkONE/A_Nomal", None, None)]),
        ("calligraphy", [("WORK/Calligraphy/Nomal/A", None, None)]),
        ("paint", [("WORK/StudyPaint/Nomal/A", None, None)]),
        ("study2", [("WORK/StudyTWO/Nomal/A", None, None)]),
        ("sausage", [("WORK/GrilledSausage/Nomal/A", None, None)]),
        ("clean", [("WORK/WorkClean/Nomal/A", None, None)]),
        ("fixmenu", [("WORK/FixMenu/Nomal/A", None, None)]),
        ("game", [("WORK/PlayONE/Nomal/A", None, None)]),
        ("water", [("WORK/PlayWater/Nomal/A", None, None)]),
        ("remove", [("WORK/RemoveObject/Nomal/A", None, None)]),
        ("rope", [("WORK/RopeSkipping/Nomal/A", None, None)]),
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
        ("left",      [("MOVE/walk.left/B_Nomal", None, None)]),
        ("right",     [("MOVE/walk.right/B_Nomal", None, None)]),
        ("left-fast", [("MOVE/walk.left.faster/B_Happy", None, None)]),
        ("right-fast",[("MOVE/walk.right.faster/B_Happy", None, None)]),
        ("left-slow", [("MOVE/walk.left.slow/B_PoorCondition_1", None, None)]),
        ("right-slow",[("MOVE/walk.right.slow/B_PoorCondition_1", None, None)]),
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
        ("squat",   [("IDEL/Squat/B_Nomal/1", None, None)]),
        ("tennis",  [("IDEL/Tennis/Nomal/B", None, None)]),
        ("bubbles", [("IDEL/Bubbles/B", None, None)]),
        ("boring",  [("IDEL/Boring/B_Nomal", None, None)]),
        ("aside",   [("IDEL/aside/Nomal/B", None, None)]),
        # 2026-09-20 组①：
        # State 待机变体 —— VPet StateONE=坐下待机 / StateTWO=躺下休息（逐帧看图核对过：
        # A=进入过渡、B=循环微动、C=退出过渡）。VPet 原件是加权待机链（MainDisplay.cs：B 循环到
        # looptimes>GetDuration 才退出，还能 ONE↔TWO 互转）——我们简化为**一次过**：
        # A + B_1 + B_2 + C 顺序拼成一条（B 只走一遍），当 fidget 小动作随机冒一下。
        ("state-one", [("State/StateONE/A_Nomal", None, None), ("State/StateONE/B_Nomal/1", None, None),
                       ("State/StateONE/B_Nomal/2", None, None), ("State/StateONE/C_Nomal", None, None)]),
        ("state-two", [("State/StateTWO/A_Nomal", None, None), ("State/StateTWO/B_Nomal", None, None),
                       ("State/StateTWO/C_Nomal", None, None)]),
        # IDEL 彩蛋：一连串比心/爱心（VPet happy_like520）。
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
