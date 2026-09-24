# VPet 原版动画系统分析（重构参考 · 给自己看的）

> 2026-09-22 逐行读源码整理；**2026-09-24 按主人逐素材目检审视（`VPet动画主人审视.md`）全面校正**——新增 §9 审视对照表（24 类逐一：用/不用/怎么用），§2/§5 相关行同步修正。
> 源码快照在 `%TEMP%/vpet_cs/`（117 个 .cs），素材底本
> `D:/SteamLibrary/steamapps/common/VPet/mod/0000_core/pet/vup/`。
> 引用格式 `文件:行号` 均指源码快照里的文件名。
> 素材盘点：`%TEMP%/vpet_anim_inventory.json`（子代理产出）+ `vpet_leaf_summary.json`。

## 0. 一眼看懂：原版 vs 我们

| | VPet 原版 | AIPet 现状 | 差距 |
|---|---|---|---|
| 素材规模 | **609 个帧目录 / 6181 帧 / 25 大类** | 26 池 / 191 变体 / 2099 帧 | 帧数只有 1/3 |
| 状态档 | **4 档**（Happy/Nomal/PoorCondition/**Ill 生病**；WORK 素材只有前 3 档） | 3 档（无 Ill） | 少一档 |
| 待机场 | Idel 类 **10 种动画名 / 73 目录 849 帧**（Tennis/Meow/aside/Squat/meowlook/Bubbles/yawning/happy_like520/Boring/amusement_B——**10 种我们全导了**：Meow → `greet`（9 变体三档，2026-09-24 动画组A）、amusement_B → fidget 的 `amuse`，其余作 fidget 变体） | fidget 14 变体 340 帧 | 种数齐；缺 Happy/Poor 档位变体与冗余 B 变体（849→340 帧） |
| 干活 | WORK **13 种 × A/B/C × 3 档（Happy 58/Nomal 49/Poor 57 目录）× 多 B 变体 = 180 目录 2214 帧** | **107 变体 1369 帧**（3 档全；每类型每档取单个 B） | 档位齐（重构#7）；余「多 B 变体」维度 |
| 帧时长 | **每帧独立 ms**（文件名尾数），主力 125ms=8fps，但 250/375/500 大量存在 | 每池统一 rate（8fps） | 节奏细节丢失 |
| B 循环圈数 | lps `duration:` 表（squat#20 boring#20 sleep#20，默认 10）+ **概率递减退出** | 状态机定时/包裹段播完即切 | 机制不同 |
| 渲染 | **双 Grid 交替缓冲**（无缝切换）+ 运行时拼图缓存（cache/） | AnimatedSprite2D 单节点（启动全量预载） | ~~切换有黑帧风险~~ 2026-09-22 BufferProbe 实证零空窗——VPet 双缓冲是 WPF 异步读盘的补丁，预载架构不需要（见 §7.8） |

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

### 1.2 目录命名五种风格（609 叶子实测，2026-09-22 子代理盘点 + 抽查校正）

1. **纯字母段**（183 叶子）：`A` / `B` / `C`，常再套数字变体 `B/1`、`B/2`（Music/B/Happy/3 等）
2. **字母_状态**（137 叶子）：`A_Nomal` / `B_Happy` / `C_PoorCondition`，多变体 `B_PoorCondition_1/2/3`、`B_Happy/1..9`
3. **状态_字母**（反序！）：`PoorCondition_A` / `PoorCondition_C`（walk.*.slow 用这种）——解析靠消词，顺序无关
4. **状态作父目录**（341 叶子处于状态名下）：`WORK/Calligraphy/Happy/A`、`IDEL/aside/Nomal/B_2`、`Default/Happy/1`
5. **纯状态名叶子**（45 个，无段字母 = Single）：`LevelUP/Happy`、`Switch/Down/Happy`
- ⚠ 官方拼写就是 `Nomal`（非 Normal）；`Ill` 大小写不统一（Ill/ill/A_Ill/B_ill）——解析走 ToLowerInvariant 所以无所谓，**但我们写匹配逻辑时别区分大小写**。
- ⚠ 子代理报告中「不完整组仅 10 个（Squat/climb.top 无 B 等）」**抽查 3 处全错，不采信**；判断某动画段结构一律以 `find` 实查为准。

### 1.3 检索与降级（GraphCore.cs:112-168）

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
| StateONE / StateTWO | `State/` | 待机模式1/模式2（22 目录）→ **已导入** = `sit`/`lie` 池（重构#9）| A/B/C |
| **Move** | `MOVE/` | 一切位移（walk/climb/crawl/fall…）| A/B/C |
| **Work** * | `WORK/` | 工作/学习/玩（13 种）| A/B/C |
| **Sleep** * | `Sleep/` | 睡觉 | A/B/C |
| **Say** * | `Say/` | 说话（4 感情 × 档位）| A/B/C |
| Think | `Think/` | 思考（3 档）。主人目检：**全都是挠头动画，当思考动画使用也行**（2026-09-24 改口，维持现状）；源里每档还有 B_2..B_5 未导 | A/B/C |
| **Touch_Head** | `Touch_Head/` | 摸头 | A/B/C |
| **Touch_Body** | `Touch_Body/` | 摸身体（+Happy_Turn 转身躲）| A/B/C |
| **Raised_Dynamic** * | `Raise/` | 被提起·动态（甩来甩去）。主人目检：**刚拖拽 = 动态；一直挂着 4 秒 → 进静态循环**（Happy 22 帧 / Nomal 摇晃 8+狗刨 11 / Poor 11 / ill 不用）| Single |
| **Raised_Static** * | `Raise/` | 被提起·静态（拎着不动）= 挂起 4 秒后的落点；C 段有 FLA+FLB 双前缀混装（导入需拆片拼接，参照 fall 池先例）| A/B/C |
| Pinch | `Pinch/` | 捏脸 | A/B/C |
| **StartUP** * | `StartUP/` | 开机。主人目检：**除 Ill 外全可用，心情分档作用不明显**（Happy/Happy_1/Nomal/PoorCondition 可导 57 帧；newyear 节日皮肤暂缓）| Single |
| Shutdown | `Shutdown/` | 关机。主人目检：**除 Ill 外全可用**（2/{Happy,Nomal,Poor} + Happy_1/Nomal_1/Nomal_2/Poor 可导 151 帧）| Single |
| Switch_Up / Switch_Down | `Switch/` | 坐起/坐下（进出工作状态）——我们已用作 WorkIn/WorkOut 的固定动画（`switch-up`/`switch-down`）；主人对源 Switch 类整体评价「没想到太好的使用方式」（另含饿了/口渴切换，我们不引入）| Single |
| Switch_Thirsty / Switch_Hunger | `Switch/` | 口渴/饥饿切换 | Single |
| SideHide_Left_Main / _Rise | `SideHide_Left_*` | 左侧贴边躲藏 / 探头 | A/B/C |
| SideHide_Right_Main / _Rise | `SideHide_Right_*` | 右侧同上 | A/B/C |

（* = 注释里标「必须有的动画」。`BDay/Eat/Drink/Gift/LevelUP/Music/Shutdown/StartUP` 等目录 → Common，按名字点播。）

**未导入的类**（我们 26 池之外）：`Eat/Drink/Gift`（**front_lay/back_lay 前后双层拆分**——角色在食物前/后两层叠加渲染，重构吃喝系统时要处理双图层合成）、`LevelUP`、`Raise` 的档位细分。（`State`（StateONE/TWO 嵌套场）已在重构#9 导入 → `sit`/`lie` 池。）
**多外观机制**：一个 `pet/<name>/` 目录 + 同名 `.lps` = 一套皮（mod 平级并列）；vup 内**只有单角色**（petname#萝莉斯）。`StartUP/Happy_newyear` 是节日皮肤变体。我们的 mods/ 结构天然兼容这个模型。

## 3. 播放引擎（PNGAnimation.cs）

### 3.1 每帧独立时长 ★我们没抄的关键细节

```
文件名: 哈皮走路向左_000_125.png
                      ↑序号 ↑该帧显示毫秒数          （PNGAnimation.cs:227）
time = int.Parse(文件名最后一个'_'后的数字)
播放: 显示帧 → Thread.Sleep(time) → 下一帧          （PNGAnimation.cs:272-285）
```

实测分布（6181 帧）：**125ms 占 92.6%**（=8fps），其余**全部是 125 的整数倍**（逐帧时长而非固定帧率）：
- 250ms（212 帧）：MOVE/climb.* 的 B 循环（爬墙慢动作）+ Music/B 乐器循环
- 375/500ms（70/82 帧）：Eat/Drink/Gift 的 front_lay 咀嚼停顿、IDEL/Boring 打呼噜、State/StateTWO 长停顿
- 625~2875ms（36 帧）：单帧长定格（StudyTWO 思考 1250ms、Gift 收礼呆滞 2875ms）
- ⚠ WORK 有 1 帧写成 **124ms（原版笔误）**；Touch_Head 用 `_0_250` 单位序号不补零——导入器解析要容错
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
Move 的 `Rnd.Next(walklength++) < Distance` 同法实测（30 万次，C# 复刻 post-increment 口径 = `while (Rnd.Next(walk++) < D)`，源码行 `GraphHelper.cs:519`）：Distance=5 → **8.19 段 B**；**Distance=7 → 10.69 段 B**（×0.75s/段 ≈ 8.0s）；Distance=8 → 11.92；Distance=10 → 14.34。注意 `walklength` 从 0 起 post-increment → **头两次检查恒为 `Next(0)=0` / `Next(1)=0`，必续圈**。（旧记「11.7」为口径偏差，已按实测修正。）

**StateONE/StateTWO 嵌套场**（MainDisplay.cs:207-266）：StateONE 循环中可**概率跳进 StateTWO**（`Rnd.Next(2+CountNomal)` 掷中 0 → StateTWO），StateTWO 播完 C 又回 StateONEing——**两套待机场互相嵌套轮换**。→ **✅ 重构#9 已实现**：`sit`（StateONE）/`lie`（StateTWO）两池 + CharAnim 嵌套会话（进 sit / 进 lie / 退 lie 三处 looptimes 清零、CountNomal 已躺次数压重复躺下、会话期间不掷显示骰子）；验证 SitProbe 36 断言。

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
            // ★Distance=7: 走得越久越容易停（实测平均 10.7 段 ≈ 8s 路程，负反馈）
        否则 40% 换兼容 Move，否则 StopMoving
    StopMoving(): 停 MoveTimer → Display(graph, C_End) → DisplayToNomal   // 停步收势
```

- **GetCompatibilityMove 接力**：walk 到头 → 40% 直接接 climb（爬墙）→ climb 到顶 40% 接 climb.top → 到角 40% 接 fall → 落地停。**整条「走→爬→顶爬→掉落」是 Move 条目间概率接力自然涌现的，不是一个写死的爬边状态机！**（我们最初自写的 `Climb.cs` 相位机行为像、结构完全不同；**2026-09-22 重构#4 已按原库模型重写为 `MoveRunner.cs` + `config/moves.json`**，见 §7 第 4 条。）

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

## 7. 重构进度与待办（按性价比排序）

1. ✅ **每帧时长**（2026-09-22 完成）：导入器解析文件名尾数 ms → info.json 写 `durations`（相对时长 = ms÷基准取整；缺失/笔误容错为 1）；`CharAnim.加载动画` 用 `AddFrame(名, 纹理, 时长)` 逐帧带上；`动画时长()` 改 Σduration÷rate（定格帧不再被低估）。全量重导 1777 帧（fidget/interact 追加模式不动原项目素材）。验证：PoolProbe 加 4 断言——fidget-squat 定格帧（1000ms→8 / 875ms→7）+ 总时长 4.50s（旧算法只有 2.375s）+ idle-nomal-1 呼吸停顿（250ms→2）全 PASS。
2. ✅ **B 循环概率退出**（2026-09-22 完成）：fidget 待机小动作七个三段变体（squat/tennis/bubbles/boring/aside/state-one/state-two——**后两个在重构#9 升级为独立 `sit`/`lie` 嵌套会话池**）拆回 `-a`/主段/`-c` 三段（导入器 SPEC 重写，与包裹段同命名约定）；CharAnim 加 fidget 会话——A 播完 → B 每圈掷 `Next(圈数) > L`（VPet `DisplayBLoopingToNomal` 原样，首圈恒不过线）→ 命中播 C → idle。单段变体（spin/bubble/doze 等 = VPet Single 型）保持一次过。L 进 `behavior.json`（`fidget循环L`，默认 2 = 平均 5.6 圈 ≈ 8~16s 会话；VPet 原版 10~20 = 分钟级，**机制照抄、数值按观感重定标**）。think/say/work/sleep/music 不上骰子（定时气泡 / 持续态强制循环 = VPet `DisplayBLoopingForce` 语义）。验证：FidgetProbe 14 断言（段推进/首圈保底/单段直通/L=2 大样本平均 5.57 圈）+ PoolProbe durations 断言随拆段更新，全 PASS。
3. ✅ **EventTimer 概率爬坡**（2026-09-22 完成）：首次走动之后的自主走动改 VPet 式爬坡骰子（`MainLogic.cs:489-494` 同款）——每「爬坡秒」（15s）掷 `Next(max(爬坡下限, 爬坡周期 - 连续待机秒))`，命中前「爬坡移动槽」（3）个值 → 走动；**闲置越久窗口越小越走得勤，互动/走动后清零重新爬坡**（= CountNomal 语义）。参数进 `behavior.json`（爬坡秒/周期/下限/移动槽，默认 15/200/20/3 = VPet intercycle 默认；模拟中位 ~210s，与旧固定 120~300s 倒计时量级一致）。旧「走动间隔最小/最大秒」均匀倒计时删除（无爬坡、机械）。骰子数学提取为纯函数 `爬坡窗口()`/`爬坡掷骰()`。验证：RampProbe 10 断言（窗口公式/触底不破/大样本命中率 1.54%→2.99%→15.28% 与理论一致/互动清零）+ WalkProbe（端到端实机走位）全 PASS。
4. ✅ **Move 接力**（2026-09-22 完成，重构#4）：走→爬→顶→掉改为**抄 VPet 原库的移动方式**——`MoveRunner.cs`（替换自写相位机 `Climb.cs` 与走链）＋ `config/moves.json` 移动定义表（`vup.lps` 16 条 `move:` 一行一条）。机制照抄：调度 = 触发通过的移动里随机轮询（`DisplayToMove`）；触发/检查 = 近边 ≤ / 远边 ≥ 的距离门；圈推进 = 检查 + **距离骰** `Rnd.Next(圈数) < 距离`（`Move.Length`）；**兼容接力** = 方向评分（同向 +1/反向 −1，某轴为 0 不参与）≥0 过滤后随机（`GetCompatibilityMove`；VPet 40%，我们默认 `接力概率` 0.8）；吸附 = 挂边/顶挂几何（VPet×0.5 边距换算）；收势 = 回位（`ResetPosition`：任一轴推出 >25% → 贴回）+ C 段；落地 = 重力移动触地即完成 → `移动冷却秒` 冷却（保留的防重复观感）。**加新移动方式 = 加一行数据，不改代码**。验证：MoveProbe 48 断言（纯函数/表加载/档位过滤/触发检查/冷却/方向评分/全流程 上墙→吸附→爬→顶爬→角上接力→下落→落地→回位→收势→idle + 下爬 junction + 让位拉回）全 PASS；WalkProbe 按新模型重写。
5. ✅ **Touch B 循环续命**（2026-09-22 完成，VPet `SetContinue` 语义，`PNGAnimation.cs:556-574` + `MainDisplay.cs:146-165`）：同类触摸序列进行中又摸——A 段忽略（进场不打断）、**B 段续命**（这圈播完重播 B、不进 C）、C 段照常排队开新一轮。实现：`_序列续命` 标记 + `推进序列()` 消费；`摸摸()` 按段序分流。此前连续摸会排队重播整条 A→B→C（反复重演进场动作，观感差）。验证：TouchProbe F 组 8 断言（A 段续摸忽略/B 段续命置位/续命消费后仍在 B/无续命正常进 C/C 播完回 idle）全 PASS。
6. ✅ **Ill 第 4 档 + 档位降级检索链**（2026-09-22 完成，`1dde4bd`）。**Ill 不引入**（决策）：Ill 素材仅 14/609 目录、全在未导入类（Eat/Drink/Gift/Raise），且我们无生病玩法（三档=手动档位）——降级链里没有 ill 档。**降级链**（对齐 VPet `GraphCore.FindGraphs` 的 ModeType 相邻降级）：`挑主名` = 精确档 → **无档基名**（Nomal 素材落点，先于升档试）→ 相邻档（happy↔nomal↔poor，序号相邻）→ 候选随机；`情绪变体` 修正——三档关/普通返回 `"nomal"` 而非空串（旧行为 = 整池随机串到 happy/poor 变体，违背「默认普通」口径）；`CharAnim.进入状态` 统一走 `挑主名`（idle 不再整池随机串档）。验证：GradeProbe 10 断言（200 次择档零串档/精确档命中/say+sleep 降级/800 次零段漏/链序=VPet 相邻档/无 ill）全 PASS。
7. ✅ **档位补全：WORK Happy/Poor**（2026-09-22 完成）：13 类型里 12 个补 Happy/PoorCondition（WorkTWO 无 Happy 源、Study 无档位源 → 降级链兜底，开心/不良档里不出现）；命名 `{档}-{类型}`（与 idle/music 的 `{档}-{n}` 同口径，`挑主名` 按 `-happy-`/`-poor-` 前缀收组）；新增 **876 帧 / 68 变体**（work 池 39→107 变体、493→1369 帧；B 取与 Nomal 同名的那条 = 跨档「同一动作换表情」）。`段名` 加「**同类无档**」降级（`work-happy-study2-c` → `work-study2-c`——study2 的 Happy 源就没有 C 段，VPet 自己留白；对齐 VPet「每段动画各自找档」语义，不补假素材）。验证：PoolProbe 新增 12 必存在 + GradeProbe ⑥ 组 6 断言（档位择档/精确段命中/同类降级/既有段不受影响）全 PASS。余下：IDEL 各 B 变体维度（原版每圈换花样，我们钉死一个——低优先，见 #9 行下注）。
8. ✅ **双缓冲渲染——判定：不需要**（2026-09-22，BufferProbe 实证）。VPet 的双 Grid 交替（MainDisplay.cs:556-608 `petgridcrlf` 翻转）是给 WPF 打的补丁：新动画要**运行时异步读盘+拼图**（cache/），加载期间旧 Grid 已 Stop → 不交替就会黑帧。我们启动时把全部纹理预载进 SpriteFrames（AddFrame(ImageTexture)），`Play(新名)` 同帧生效、不存在加载空窗。实证：BufferProbe 模拟段切换风暴（每 3 帧跨池换名 ~136 次），390+ 帧全程断言「当前动画存在且帧数 ≥1」——**零空窗帧**。单节点架构已覆盖该问题，不引入第二个 AnimatedSprite2D（省一半 draw call 与状态同步复杂度）。
9. ✅ **StateONE/TWO 嵌套待机场**（2026-09-22 完成，重构#9）：State 素材 22 目录 116 帧导入为 `sit`/`lie` 两池（旧 `fidget-state-one/two` 拼接变体删除）；CharAnim 加**嵌套会话**——`sit.A → sit.B 每圈随机换 B 变体 + 掷 Rnd.Next(圈数) > 坐卧循环L → 1/(躺下基数+已躺次数) 进 lie → lie.A→B 同款 → lie.C 起身回 sit 的 B 判定（可再躺）→ …→ sit.C → idle`（`MainDisplay.cs:207-266` 照抄：looptimes 三处清零、CountNomal 进 sit 清零/进 lie +1、A 只播一次）。调度 = 爬坡骰子从「移动 3 槽」扩成「移动 3 槽 + 坐卧 2 槽 + 其余无操作」（`爬坡掷槽`/`掷爬坡一次`，心跳与探针同一路径；VPet 1 槽/200 = 分钟级，我们 2 槽按观感重定标）。闸门 = 会话期间不入睡/不掷移动骰子（原版 IsIdel=false → 显示骰子整块跳过）、别的状态接管 → 会话作废。配置 = `坐卧启用/坐卧槽/坐卧循环L/躺下基数`。验证：SitProbe 36 断言（嵌套全流程 + 接管作废 + 分派集成 + 槽位分布）全 PASS；FidgetProbe/PoolProbe 随素材迁移更新。（导入器顺带修一处 rate 退化 bug：时长档无真众数 → 退回 125ms 基准，否则 rate=1 时长塌缩；影响面 7 个变体重写。）

## 9. 主人逐素材审视对照表（2026-09-24，重构施工依据）

> 来源：`VPet动画主人审视.md`（主人用眼睛逐一看过 VPet 全部素材后的分类判断）。
> **这是「用不用 / 怎么用」的权威口径**——与源码机制（§1-§6）冲突时，机制照源码、取舍照本表。
> 字母 = 动画阶段（A 起始 / B 循环 / C 结束），数字 = 变体，Single = 顺序动画只放一次；每类基本有心情档，个别有 Ill 生病档（我们一律不引入 Ill）。

| VPet 类 | 主人判断 | 序列 | 我们的处置（重构口径） |
|---|---|---|---|
| **BDay** | 名义生日，**看着很适合做 music 动画** | A/B/C | 已导入 `bday` 池作生日彩蛋；**music 池另用 `Music/`**（见下）——BDay 不挪用为 music |
| **Default** | 待机呼吸，全循环帧，数字=循环变体；**默认普通级，切换要留** | 循环 | `idle` 池已对齐（happy/nomal/poor × 1-3）；**#24：idle 变体改加权随机**（nomal 晃头为主、其他低频），不再均匀概览 |
| **Drink / Eat / Gift** | 循环 back_lay+front_lay 与物品同贴图；**暂不引入，不拟人** | 双层 | **不导入**（吃喝送礼玩法不在范围；引入需处理前后双图层合成） |
| **IDEL** | 空闲小动作场（10 种）；逐一点评见下 | A/B/C + Single | `fidget` 池；**#23：amusement_B 是循环动画**（退出帧=首帧非尾帧，循环 2-5 次）需修正 |
| **LevelUP** | 升级动画；**不引入** | Single | 不导入 |
| **MOVE** | 各位移（垂直爬/横爬/就地爬/行走，前三不受边框限制）；**行走快慢原版由心情决定，我们只做普通档** | A/B/C | `move` 池 + MoveRunner；**#22：删 walk 的 fast/slow 变体**（不按 happy/poor 分快慢，变体只跟配置心情档） |
| **Music** | 音乐动画；**暂定随机抽取，后续考虑按音量变动画** | A/B/C + Single | `music` 池已对齐（happy/nomal/poor × B 变体 + single 高潮 + a/c）；音量驱动列后续 |
| **Pinch** | 捏脸，萌点十足；**我们没接入玩法系统** | A/B/C | `pinch` 池已导入（FacePinch 三段自管）；玩法不接 |
| **Raise** | 拖拽；**动态+静态两种：刚拖=动态，挂 4 秒→静止态循环** | 动态 Single / 静态 A/B/C | `drag/dragup/dragdown` 仅 Happy；**补：动态 3 档 + 新建 `draghold` 静态挂起池 + 代码接「拖拽满 4 秒切静态循环」** |
| **Say** | 说话，4 感情细分（见下） | A/B/C，数字=B 变体 | `say` 池 9 主变体（4 感情）+ 4 份感情级 a/c 段——**2026-09-24 动画组E 完成**：self-smile(B_2)/self-tease(B_3)/shining-excited(B_1)/shining-calm(B_3)/shy-wry(B_3) 已导；主名 `smile`→`shining`（同感情统一前缀）；同感情共用一份 A/C（段名解析前缀递减）；Shy/B_2 与 B 近同未导 |
| **Shutdown** | 退出；**除 Ill 外全可用，心情分档作用不明显** | Single | **新建 `exit` 池**（7 条：2/{Happy,Nomal,Poor}+Happy_1/Nomal_1/Nomal_2/Poor，除 Ill） |
| **SideHide_Left/Right_Main** | 左/右贴边隐藏 | A/B/C | `edge_hide` 池已导入 |
| **SideHide_Left/Right_Rise** | 左/右隐藏鼠标悬浮探头 | A/B/C | 探头表现（EdgeHide 自管）；素材随 edge_hide |
| **Sleep** | 睡觉 | A/B/C | `sleep` 池已对齐 |
| **StartUP** | 启动；**除 Ill 外全可用，心情分档作用不明显** | A/B/C（实为 Single 顺序） | **新建 `enter` 池**（4 条：Happy/Happy_1/Nomal/PoorCondition，除 Ill；newyear 节日皮肤暂缓） |
| **State** | 坐下/躺下；**状态1坐、状态2躺，躺只能从坐的 B 进；可定义为空闲态，B 循环可久一点** | A/B/C，数字=循环变体 | `sit/lie` 池 + 嵌套会话（重构#9 已实现）；**B 循环调长**（`坐卧循环L`↑，符合「空闲态可久一点」） |
| **Switch** | 心情档切换 + 饿了/口渴；**没想到太好的使用方式** | Single | 我们已借 `switch-up/down` 作 WorkIn/WorkOut 固定动画；饿了/口渴切换**不引入** |
| **Think** | **全是挠头动画，当思考动画使用也行**（2026-09-24 改口） | A/B/C，数字=变体 | `think` 池维持现状（挠头当思考用）；源里 B_2..B_5 未导，本次不扩 |
| **Touch_Body** | 身体互动；**开心档和普通档共用 happy**；Happy_Turn=转圈圈 | A/B/C，数字=线程 | `interact_body`（取 Happy）+ `turn`（Happy_Turn）已导入 |
| **Touch_Head** | 摸头；**除 Ill 档都能用** | A/B/C | `interact` 池已对齐 |
| **WORK** | 13 种，逐一点评见下（语义映射到工作类型） | A/B/C，数字=变体 | `work` 池 107 变体（3 档全）；**补：按主人语义把 13 类映射到「工作类型」**（Agent 指定类型→选对应动画） |

### 9.1 IDEL 10 种逐一点评（主人目检）

| 种 | 主人判断 | 处置 |
|---|---|---|
| amusement_B | 左右扭身循环 | fidget 变体；**#23 循环修正**（退出帧=首帧、循环 2-5 次） |
| aside | happy 时手按屏幕＝「想接触主人」 | fidget 变体（保留） |
| Nomal | 左右晃头 | ＝ Default 呼吸承担（IDEL 下无独立 Nomal 目录） |
| Boring | **实则是打瞌睡动画** | fidget 变体（doze 语义） |
| Bubbles | 迪斯科舞动 | fidget 变体 |
| happy_like520 | 卖萌送爱心 | fidget 变体（happy520） |
| **Meow** | **手敲屏幕，非常适合做问候语动画（因此最好不做空闲动画）** | **改作 `greet` 池**（整池换 Meow 9 变体三档）；**从 fidget 移除**（不做空闲动画） |
| meowlook | 傲娇害羞的偷瞄 | fidget 变体 |
| Squat | 蹲下看主人，**B 段循环可做久一点增萌点** | fidget 变体；B 循环可单独调长 |
| Tennis | 打网球 | fidget 变体 |
| yawning | 打哈欠 | fidget 变体 |

### 9.2 Say 4 感情细分（主人目检）

| 感情 | 变体细分 |
|---|---|
| **Self**（侧耳说） | 变体1 普通 / 变体2 微笑侧耳（高兴）/ 变体3 眯眼侧耳（吐槽态） |
| **Serious**（双手交叉说） | 警示、教育意味（仅 B） |
| **Shining**（手指舞动说） | 变体1 兴奋 / 变体2 普通略高兴 / 变体3 眯眼平静 |
| **Shy**（托腮说） | 变体1、2 相同＝普通可爱 / 变体3 略无奈 |

### 9.3 WORK 13 种 → 工作类型语义映射（主人目检，供 Agent 选动画）

| WORK 素材 | 动作 | 映射工作类型 |
|---|---|---|
| Calligraphy | 书法写字 | 创作类 |
| FixMenu | 修理屏幕线路 | 计算机类 |
| GrilledSausage | 烧烤香肠 | 美食/烹饪类 |
| PlayONE | 玩手柄游戏 | 游戏类 |
| PlayWater | 游泳圈泡水 | （无合适场景，不映射） |
| RemoveObject | 钢笔写字 | 写正文 |
| RopeSkipping | 跳绳 | 其他工作 |
| Study | 读书 | 浏览网页/资料收集整理 |
| StudyPaint | 绘画 | 素材/图片生成 |
| StudyTWO | 读书 | 同 Study |
| WorkClean | 屏幕清洁 | 清理类 |
| WorkONE | 写字 | 同 RemoveObject |
| WorkTWO | 连麦互动 | 声音类 |

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
