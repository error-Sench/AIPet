# VPet 原版动画系统分析（重构参考 · 给自己看的）

> 2026-09-22 逐行读源码整理。源码快照在 `%TEMP%/vpet_cs/`（117 个 .cs），素材底本
> `D:/SteamLibrary/steamapps/common/VPet/mod/0000_core/pet/vup/`。
> 引用格式 `文件:行号` 均指源码快照里的文件名。
> 素材盘点：`%TEMP%/vpet_anim_inventory.json`（子代理产出）+ `vpet_leaf_summary.json`。

## 0. 一眼看懂：原版 vs 我们

| | VPet 原版 | AIPet 现状 | 差距 |
|---|---|---|---|
| 素材规模 | **609 个帧目录 / 6181 帧 / 25 大类** | 26 池 / 191 变体 / 2099 帧 | 帧数只有 1/3 |
| 状态档 | **4 档**（Happy/Nomal/PoorCondition/**Ill 生病**；WORK 素材只有前 3 档） | 3 档（无 Ill） | 少一档 |
| 待机场 | Idel 类 **10 种动画名 / 73 目录 849 帧**（Tennis/Meow/aside/Squat/meowlook/Bubbles/yawning/happy_like520/Boring/amusement_B——**10 种我们全导了**，amusement_B 用作 greet） | fidget 14 变体 340 帧 | 种数齐；缺 Happy/Poor 档位变体与冗余 B 变体（849→340 帧） |
| 干活 | WORK **13 种 × A/B/C × 3 档（Happy 58/Nomal 49/Poor 57 目录）× 多 B 变体 = 180 目录 2214 帧** | 39 变体 493 帧（只取 Nomal 档 + 单个最全 B） | 差 4.5 倍（档位+变体） |
| 帧时长 | **每帧独立 ms**（文件名尾数），主力 125ms=8fps，但 250/375/500 大量存在 | 每池统一 rate（8fps） | 节奏细节丢失 |
| B 循环圈数 | lps `duration:` 表（squat#20 boring#20 sleep#20，默认 10）+ **概率递减退出** | 状态机定时/包裹段播完即切 | 机制不同 |
| 渲染 | **双 Grid 交替缓冲**（无缝切换）+ 运行时拼图缓存（cache/） | AnimatedSprite2D 单节点 | 切换有黑帧风险 |

## 1. 数据模型：一条动画 = 四元组（GraphInfo）

`GraphInfo.cs:22` —— **新版动画类型 = 整体类型 + 名字**：

```
GraphInfo {
    Name      // 动画名（用户自定义；同名 = 同一组，一起随机）如 "tennis" / "walk.left" / "squat"
    Type      // GraphType 枚举（21 个分类，见 §2）——由目录路径里的关键词自动识别
    Animat    // AnimatType: Single | A_Start | B_Loop | C_End ——目录名里的 a/b/c/start/loop/end
    ModeType  // Happy | Nomal | PoorCondition | Ill ——目录名里的 happy/nomal/poorcondition/ill
}
```

### 1.1 目录路径 → 四元组的解析规则（GraphInfo.cs:46-145）

叶子目录全路径按 `\` 拆成词列表 `path_name`（小写），然后**按顺序消词**：

1. **ModeType**：lps 里 `mode#` 显式指定，否则从词表里 **Remove** `happy`/`nomal`/`poorcondition`/`ill`；都没有 = Nomal。
2. **GraphType**：lps 里 `graph#` 显式指定，否则拿枚举名拆下划线（`GraphTypeValue`，GraphHelper.cs:22——如 `Touch_Head` → `["touch","head"]`）在词表里**找连续子序列**，命中即消词。
   - ⚠ 枚举顺序敏感：按 `GraphType` 声明序遍历取**第一个匹配**。`Default` 单词 `["default"]`、`Idel`（注意拼写就是 Idel）`["idel"]`。
   - 匹配不到任何类型 = **Common**（不入 GraphsName 索引，只能按名字点播——music/eat/drink/gift 都是这类）。
3. **AnimatType**：lps `animat#` 指定，否则 Remove `a`/`start` → A_Start；`b`/`loop` → B_Loop；`c`/`end` → C_End；否则 **Single**。
4. **Name**：lps 行的 Info（`名字#xxx`）优先；否则消掉尾部纯数字/`~`开头的词后**取最后一个词**。
   - 例：`IDEL\Tennis\B_Nomal\3` → path_name=[idel,tennis,b,nomal,3] → Type=Idel, Mode=Nomal, Animat=B_Loop, 消词剩 [tennis] → **Name="tennis"**。
   - 例：`MOVE\walk.left\A_Nomal` → 词 [move,walk,left,a,nomal] → Type=Move, Name="walk.left"（`walk.left` 是一个词？不——目录名就叫 `walk.left`，含点不拆；拆的是 `\` 和 `_`）。

> **同一 Name 的所有 A/B/C/档位聚成一个会话族**：`GraphsList[Name][AnimatType] → List<IGraph>`（GraphCore.cs:67）。
> 这就是为什么 tennis 的 B/B_2/B_3/B_4 会互相随机——它们解析出来 Name 全是 "tennis"、Animat 全是 B_Loop，进同一个 List。

### 1.2 检索与降级（GraphCore.cs:112-168）

```
FindName(Type)          → GraphsName[Type] 里随机一个 Name        （类型级随机入口）
FindGraph(Name,Animat,Mode) →
    GraphsList[Name][Animat] 里过滤 ModeType==Mode → 随机
    ↓ 没有则降级：Mode+1 档（Happy→Nomal→Poor，枚举序 0=Happy,1=Nomal,2=Poor,3=Ill）
    ↓ 再没有：Mode-1 档
    ↓ 再没有：除 Ill 外全体随机     ← 「Ill 素材绝不外泄给健康档」
FindGraphs(...) 同逻辑但返回整个 List（调用方自己再随机/过滤 Type）
```

## 2. GraphType 全枚举（GraphInfo.cs:152-236，21 个）

| GraphType | 素材目录 | 语义 | 段结构 |
|---|---|---|---|
| Common | （不匹配任何类型的都是）| 被按名字点播：music/eat/drink/gift/levelup/bday/raise… | 随意 |
| **Default** * | `Default/` | **呼吸待机**（常驻底座）| Single（10 目录 130 帧）|
| **Idel** | `IDEL/` | 空闲小动作场（squat/tennis/bubbles/boring/aside…20+ 种）| A/B/C |
| StateONE / StateTWO | `State/` | 待机模式1/模式2（22 目录）| A/B/C |
| **Move** | `MOVE/` | 一切位移（walk/climb/crawl/fall…）| A/B/C |
| **Work** * | `WORK/` | 工作/学习/玩（13 种）| A/B/C |
| **Sleep** * | `Sleep/` | 睡觉 | A/B/C |
| **Say** * | `Say/` | 说话（4 感情 × 档位）| A/B/C |
| Think | `Think/` | 思考（3 档）| A/B/C |
| **Touch_Head** | `Touch_Head/` | 摸头 | A/B/C |
| **Touch_Body** | `Touch_Body/` | 摸身体（+Happy_Turn 转身躲）| A/B/C |
| **Raised_Dynamic** * | `Raise/` | 被提起·动态（甩来甩去）| Single |
| **Raised_Static** * | `Raise/` | 被提起·静态（拎着不动）| A/B/C |
| Pinch | `Pinch/` | 捏脸 | A/B/C |
| **StartUP** * | `StartUP/` | 开机 | Single |
| Shutdown | `Shutdown/` | 关机 | Single |
| Switch_Up / Switch_Down | `Switch/` | 坐起/坐下（进出工作状态）| Single |
| Switch_Thirsty / Switch_Hunger | `Switch/` | 口渴/饥饿切换 | Single |
| SideHide_Left_Main / _Rise | `SideHide_Left_*` | 左侧贴边躲藏 / 探头 | A/B/C |
| SideHide_Right_Main / _Rise | `SideHide_Right_*` | 右侧同上 | A/B/C |

（* = 注释里标「必须有的动画」。`BDay/Eat/Drink/Gift/LevelUP/Music/Shutdown/StartUP` 等目录 → Common，按名字点播。）

## 3. 播放引擎（PNGAnimation.cs）

### 3.1 每帧独立时长 ★我们没抄的关键细节

```
文件名: 哈皮走路向左_000_125.png
                      ↑序号 ↑该帧显示毫秒数          （PNGAnimation.cs:227）
time = int.Parse(文件名最后一个'_'后的数字)
播放: 显示帧 → Thread.Sleep(time) → 下一帧          （PNGAnimation.cs:272-285）
```

实测分布（6181 帧）：**125ms 占 93%**（=8fps），其余 250/375/500/625/750/1000/1250/2000/2875ms
——慢帧用在「定格/眨眼/持有」上（如 squat 的 B_4 定格 500ms、boring 的长停顿）。
**我们导入时把 rate 统一写死 8fps，丢掉了这些节奏变化** → 这是「细节没打磨好」的另一个根因。

### 3.2 加载与缓存

- PetLoader.LoadGraph（PetLoader.cs:76）：递归目录；有 `info.lps` 就按 lps 行加载（可显式指定 pnganimation/picture/foodanimation + startuppath）；无 lps 时**叶子目录 1 个文件 = Picture（静图），多文件 = PNGAnimation**。
- 运行时把帧序列**拼成横向 spritesheet 缓存**到 `cache/`（GraphCore.cs:57，文件名 `500_<hash>_<n>.png`，500=Resolution；单图上限 60000px 宽，超了自动缩帧宽 PNGAnimation.cs:213）。2 分钟不用自动清（IdleCacheTimeout）。
- **双缓冲渲染**（MainDisplay.cs:544-608）：`PetGrid` / `PetGrid2` 两个 Decorator 交替——新动画在隐藏层 Run 起来、旧的 Stop(true)、再切 Visibility → **段间切换零黑帧**。
- 播放控制 TaskControl：Stop / Continue（循环动画收到 Continue 会在尾帧回卷重播 = 「摸头时 B 循环续命」的机制，见 §5.3 SetContinue）。

### 3.3 空动画兜底（MainDisplay.cs:523-534）

找不到 graph 时 `nodisplayLoop++`：>20 次 → 播 Default 兼容；>100 次 → 弹「未找到可播放动画」并停机。

## 4. 主调度器：EventTimer 骰子（MainLogic.cs:471-533）★核心

```
EventTimer = 15s（LogicInterval 可配，默认 15）
每次 tick，若 IsIdel（当前是 Default 或 Work 且没被按住）:
    rnddisplay = max(20, InteractionCycle - CountNomal)     // InteractionCycle 默认 200（可配 intercycle）
    若当前在 Work: rnddisplay = 2*rnddisplay + 20           // 干活时大幅降低打扰
    掷 Rnd.Next(rnddisplay):
        0,1,2   → DisplayMove()          移动（走/爬/掉…）
        3,4,5   → DisplayIdel()          空闲小动作（IDEL 场）
        6       → DisplayIdel_StateONE() 待机模式1（State 场）
        7       → DisplaySleep()         睡觉（打盹）
        8,9,10  → RandomInteractionAction 插件注册的随机行为（音乐/喝水等在这挂）
        其余    → 什么都不做（继续呼吸 Default）
```

- **CountNomal**：每播一次 Default 呼吸 +1（MainDisplay.cs:74），触发任何互动/小动作时清零。
  → 越久没动静，`rnddisplay` 越小，骰子越容易掷中 0~10 → **闲置越久越活跃**（概率爬坡，不是固定间隔！）。
  刚互动完 CountNomal=0 → rnddisplay=200 → 掷中概率仅 11/200 ≈ 5.5%。
- 我们的实现是「固定空闲秒数 + 倒计时」，**没有这套概率爬坡**，观感上会「要么不来、要么扎堆」。

## 5. 各子系统细机制

### 5.1 Default 呼吸（常驻底座）

```
DisplayNomal = DisplayDefault（Main.xaml.cs:214 绑定）
DisplayDefault(): CountNomal++; Display(Default, Single, 回调=DisplayNomal)
→ 播完一条 Default 又回 DisplayNomal，永动。所有其他动作的「归巢点」。
```

Default 池按 ModeType 出不同呼吸（生病躺床 Ill 有专属素材）。

### 5.2 Idel 空闲小动作（MainDisplay.cs:271-324）★B 循环退出算法

```
DisplayToIdel():
    GraphsName[Idel] 的所有 Name（squat/tennis/bubbles/boring/aside/…20+ 种）随机轮询
    找到有 A_Start 的: Display(A) → DisplayBLoopingToNomal(name, duration)
    只有 Single 的:    Display(Single) → DisplayToNomal
    都没有: 从候选剔除，换一个

DisplayBLoopingToNomal(name, loopLength):        // loopLength = lps duration 表，默认 10
    if Rnd.Next(++looptimes) > loopLength:       // ★概率递减退出
        DisplayCEndtoNomal(name)                 //   第 n 圈退出概率 = loopLength/n
    else:
        Display(name, B_Loop, 递归自己)           //   继续下一圈（同名 B 变体随机 = B/B_2/B_3/B_4）

lps duration: state#10 squat#20 boring#20 sleep#20（其余默认 10）
```

**实测模拟**（Python 复刻 `Rnd.Next(++n) > L`，20 万次）：L=10 → 平均 **15.5 圈**；L=20 → 平均 **27.1 圈**。
同名 B 多变体时每圈还换花样（tennis 的 B/B_2/B_3/B_4），观感不重复；EventTimer 15s 骰子也可能打断。
Move 的 `Rnd.Next(walklength++) < Distance` 同法实测：Distance=7 → 平均 **11.7 段 B**（×0.75s/段 ≈ 走 9s）。

**StateONE/StateTWO 嵌套场**（MainDisplay.cs:207-266）：StateONE 循环中可**概率跳进 StateTWO**（`Rnd.Next(2+CountNomal)` 掷中 0 → StateTWO），StateTWO 播完 C 又回 StateONEing——**两套待机场互相嵌套轮换**，我们完全没有这层。

### 5.3 Touch 摸头/摸身体（MainDisplay.cs:135-205）

```
触发 → 数值: 体力-2 心情+1（我们不抄）
若当前已在 Touch_Head:
    A_Start 中 → 忽略这次触摸
    B_Loop 中 → ig.SetContinue()  ★不重播，给循环「续命」（TaskControl.Continue 尾帧回卷）
否则: Display(A) → Display(B) → DisplayCEndtoNomal     // B 只播一遍！不循环
```

Touch_Body 同构。Happy_Turn（转身躲）在 Touch_Body 目录里是独立 Name 的 Common 动画？——素材在 `Touch_Body/Happy_Turn/`，解析后 Type=Touch_Body Name="happy.turn"？词表 [touch,body,happy,turn] → Remove happy(Mode=Happy) → [touch,body,turn] → Type=Touch_Body, Name="turn" → **摸身体 Happy 档时 FindGraph("touchbody"?)** ——实际由 MainWindow 侧代码按名字点播（我们已抄成 30% 概率 turn）。

### 5.4 Move 智能移动（GraphHelper.cs:220-548）★最复杂的子系统

lps 每行 `move:` = 一条 Move 定义（16 条：climb×6 / fall×2 / walk×6 / crawl×2）：

```
move:|graph#walk.left:|TriggerType#16:|TriggerLeft#200:|CheckType#16:|CheckLeft#100:
     |SpeedX#-14:|Distance#7:|ModeType#12:|Interval#125(默认):|LocateType#None:|LocateLength#0:|
```

- **TriggerType/CheckType** = DirectionType 位标志（Left=1,Right=2,Top=4,Bottom=8,*Greater=16..128）：
  - Trigger* = 「能否开始」的屏幕边缘距离条件（walk.left 要左边空 ≥200px）
  - Check* = 「能否继续」的条件（左边空 ≥100px，不足就停/换）
- **ModeType 位标志**（Happy=2,Nomal=4,Poor=8,Ill=16）：walk.left ModeType#12=Nomal|Poor；faster#2=Happy 专属；slow#8=Poor 专属；climb/fall/crawl#14=Nomal|Poor|Ill。
  → **「快慢档」本质是按心情档过滤的独立 Move 条目**，不是一个 walk 的三个变体！
- **Display 流程**（GraphHelper.cs:463-548）：

```
DisplayMove(): lps 的 Moves 列表随机轮询，第一条 Triggered() 通过的开跑
move.Display(m):
    Display(graph, A_Start) →                       // 起步
        若 MoveTimerSmartMove 开启: 按 LocateType 先把窗口瞬移贴边（爬墙用的吸附）
        MoveTimer(Interval=125ms) 每 tick 按 (SpeedX,SpeedY) 挪窗口
    Displaying(m):                                   // B 循环
        !Checked() → 40% 概率(Rnd.Next(TreeRND=5)<=1)换兼容 Move(GetCompatibilityMove:
                     同方向加分/反向减分,分数>=0 且 Triggered 的随机) 否则 StopMoving
        Rnd.Next(walklength++) < Distance → 继续 B_Loop 递归
            // ★Distance=7: 走得越久越容易停（实测平均 11.7 段 ≈ 9s 路程，负反馈）
        否则 40% 换兼容 Move，否则 StopMoving
    StopMoving(): 停 MoveTimer → Display(graph, C_End) → DisplayToNomal   // 停步收势
```

- **GetCompatibilityMove 接力**：walk 到头 → 40% 直接接 climb（爬墙）→ climb 到顶 40% 接 climb.top → 到角 40% 接 fall → 落地停。**整条「走→爬→顶爬→掉落」是 Move 条目间概率接力自然涌现的，不是一个写死的爬边状态机！**（我们的 Climb.cs 是自己写的相位机——行为像，结构完全不同。）

### 5.5 Work 工作（GraphHelper.cs:79-215 + WorkTimer）

```
lps work: 行 = Work 定义（Type#Work/Study/Play, Name#文案, Graph#workone, MoneyBase/Time/LevelLimit…数值我们不抄）
开工: WorkTimer.Start(work) → State=WorkingState.Work
     work.Display(m) = Display(Graph, A_Start) → DisplayBLoopingForce(Graph)   // B 无限循环（不概率退出）
收工: WorkTimer.Stop → DisplayStop(回Nomal) = 播当前 DisplayType.Name 的 C_End → DisplayToNomal
     （MainDisplay.cs:84-98: DisplayStop 用 C_End；没有 C 就直接回）
IsIdel 包含 Work → 干活期间 EventTimer 仍掷骰但 rnddisplay 翻倍+20（几乎不打扰）
```

13 种工作素材 = `WORK/<Name>/Nomal|Happy|PoorCondition/A|B(_n)|C`（180 目录 2214 帧；**无 Ill 档**）。
**B 有多变体 B_Nomal/B_Nomal_2/…每圈随机换**（同 Name 同 Animat 进一个 List）。

### 5.6 Sleep（MainDisplay.cs:326-344）

```
DisplaySleep(force):
    force=true（真睡觉 State=Sleep）: Display(A) → DisplayBLoopingForce     // B 死循环，DisplayStop 才醒
    force=false（EventTimer 掷中 7 的打盹）: Display(A) → DisplayBLoopingToNomal(duration=20)  // 概率醒
```

### 5.7 Raise 被提起（MainDisplay.cs:351-418）

```
rasetype 状态机: 0,1,2 → Raised_Dynamic Single ×3（甩动）
              → 3 → Raised_Static A_Start（拎定）
              → 4 → Raised_Static B_Loop 循环
松手(rasetype=-1) → MoveSideHideCheck() 先判贴边 → 不贴边才 Raised_Static C_End → Nomal
RaisePoint（lps raisepoint: 按档位的抓握定位点）→ 鼠标位置-定位点 = 窗口偏移
```

### 5.8 SideHide 贴边躲藏（MainLogic.cs:540-580）

```
每次 Move 结束/松手: MoveSideHideCheck()
    窗口左缘出屏 >50px → 贴左: MoveWindows 定位（lps side: left#219 right#281 微调）
        → Display(SideHide_Left_Main, A_Start) → DisplayBLoopingForce    // 挂边上死循环
鼠标碰到（Touch 区域 0,0,500,500 全屏）→ C_End 回正（Main.xaml.cs:269-280）
Rise 素材（SideHide_Left_Rise）= 探头动画，由 MainWindow 侧「鼠标靠近屏幕边」逻辑点播
```

### 5.9 Music（MainWindow.cs:1061-1271，Common 按名字点播的样板）

```
MusicTimer(200ms) 检测系统音量 → 超阈值:
    Display(FindGraph("music", A_Start, Mode)) → Display_Music 循环:
        B_Loop 递归；音量高潮 → FindGraph("music", Single) 插播一段「嗨」动作再回 B
    音乐停 → Display("music", C_End) → DisplayToNomal
```

（我们已按此抄了 MusicSense，参数一致：3s 识别/6s 收场/0.02 阈值/0.25 嗨档。）

### 5.10 Say / 说话（MainDisplay.cs + SayRndFunction）

```
SayRndFunction = () => FindName(GraphType.Say) ?? FindName(Default)   // Main.xaml.cs:222
说话时: Display(sayname, A) → B（随语音/文字节奏）→ C 回 Nomal
Say 池 = 4 感情（Shining/Self/Serious/Shy）× 档位，Name 各不同
```

## 6. lps 配置层（vup.lps，49 行）

```
pet#vup:|path#vup:|petname#萝莉斯:            ← 人物注册（名字就叫萝莉斯！）
tag#all,vup,girl:                              ← 说话风格 tag
touchhead: px#159 py#16 sw#189 sh#178          ← 命中区（500 空间）
touchbody: px#166 py#206 sw#163 sh#136
touchraised: happy_px#0 py#50 sw#500 sh#200:…（按档位 4 组）
pinch: px#149 py#128 sw#56 sh#59
raisepoint: happy_x#290 y#128:…（按档位抓握点）
work: ×13（数值列全在这）
move: ×16（§5.4）
duration: state#10 squat#20 boring#20 sleep#20  ← B 循环期望圈数上限参数
```

子代理摘录版：`%TEMP%/vpet_lps_anim_lines.txt`。

## 7. 重构待办清单（按性价比排序，先记着不动手）

1. **每帧时长**：导入器读文件名尾数 ms → info.json 存 per-frame 时长（Godot SpriteFrames 支持 `set_frame_duration`）——找回 250/500ms 的定格节奏。★影响最大、改动最小
2. **B 循环概率退出**（`Rnd.Next(++n) > L`）替换现在的固定时长/播完即切——观感「活」的关键。lps duration 表抄进 behavior.json。
3. **EventTimer 概率爬坡**：`rnddisplay = max(20, 200 - CountNomal)` 的骰子调度替换固定倒计时——闲置越久越活跃、互动后安静。
4. **Move 接力**：走→爬→顶→掉用「兼容 Move 40% 接力」模型重写（现在是自写相位机，行为对但扩展性差；加新移动方式要改代码，VPet 只要加一行 lps）。
5. **Touch B 循环续命**（SetContinue）：连续摸头不重播 A，循环续命——手感细节。
6. **Ill 第 4 档** + 档位降级检索链（Happy↔Nomal↔Poor 互备、Ill 不外泄）。
7. **档位补全**：WORK 的 Happy/Poor 档（现只有 Nomal）+ IDEL 各 B 变体（原版每圈换花样，我们钉死一个）。**IDEL 10 种动画名已全部导入**（2026-09-22 盘点确认，amusement_B 用作 greet）——种数不缺，缺的是档位/变体维度。
8. **双缓冲渲染**：Godot 侧两个 AnimatedSprite2D 交替（段切换零黑帧）——现在靠「同名不重播」规避，跨段切换仍有 1 帧风险。
9. StateONE/TWO 嵌套待机场（低优先，State 素材 22 目录还没导）。

## 8. 索引（源码快照文件名速查）

```
GraphInfo.cs        四元组定义 + 目录解析规则 + GraphType/AnimatType 枚举
GraphCore.cs        索引字典（GraphsName/GraphsList）+ FindName/FindGraph 降级链 + 缓存清理
GraphHelper.cs      Work 类（79）/ Move 类（220）/ DirectionType / TreeRND 接力
PNGAnimation.cs     帧时长解析（227）/ 播放循环（272）/ spritesheet 缓存（213）
PetLoader.cs        目录递归加载（76）/ info.lps 手动加载
MainDisplay.cs      DisplayToNomal（28）/ Default（74）/ Idel 场（271）/ StateONE TWO（207）
                    / BLoopingToNomal 概率退出（314）/ Sleep（326）/ Raise（351）/ Touch（135,173）
                    / 双缓冲 Display(IGraph)（523-608）/ DisplayStop=C_End（84）
MainLogic.cs        EventTimer 主骰子（471）/ IsIdel（466）/ TreeRND=5（21）/ SideHideCheck（540）
Main.xaml.cs        DisplayNomal 绑定（214）/ SayRndFunction（222）/ SideHide 触摸回正（269）
WorkTimer.xaml.cs   开工收工（Start/Stop→C_End）
MainWindow.cs       Music 点播（1061-1271）/ DisplayIdel/Move 调试指令（826-841）
```
