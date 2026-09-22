using System;
using System.Collections.Generic;
using Godot;
using desktop.script.State;
using desktop.script.UX;
using desktop.script.Util;

namespace desktop.script.Game;

/// <summary>
/// 游戏模式挂载器（模式层的 Game 分支「换壳 + 挂载/卸载」中枢）。
/// <para>
/// 契约（见 `script/Game/README.md` 第一批②③、`script/Mode/README.md`）：
/// 1. **桌宠本体不销毁，只换表现壳**：进入时把主场景的 `AnimatedSprite2D`（CharAnim）
///    **Reparent** 进游戏玩家物理体；退出时放回原位（办公居中）。全程同一节点实例。
/// 2. **换壳 = 窗口**：进游戏 → 窗口铺满所在屏幕（全屏无边框）；退出 → 尺寸/位置原样还原。
/// 3. **办公表现让位**：进入前收起面板 + 回 idle（自动让贴边隐藏复位、取消捏脸、停止走动）；
///    期间 `StateMachine._Process` 顶闸冻结办公自主行为（数值心跳除外）。
/// 4. 进度：退出时立刻记检查点（`GameSession`），游玩中每 5 秒静默记一次（防 Agent 等外部切模式丢掉位置）。
/// </para>
/// 约定：标识符英文，注释中文（AIPet-Agent.md §8）。
/// </summary>
public partial class GameHost : Node
{
    /// <summary>当前关卡 id（最小可玩只有一关；关卡内容 = 本文件里的建世界拓扑）。</summary>
    public const string 关卡id = "level_1";

    /// <summary>默认出生点（地面顶 360 - 脚线 121 = 脚贴地；脚线按素材实测 +120.5）。</summary>
    private static readonly Vector2 默认出生 = new(0, 239);

    /// <summary>收集品（玩法切片一）：每颗星 = id + 世界坐标（摆在各平台上方一点，跳上去够得到）。</summary>
    private static readonly (string Id, Vector2 位)[] 星星表 =
    [
        ("star-1", new Vector2(670f, 200f)),    // 台1 上方（台心 670、台面 250；星心 = 台面-50，与 star-2/3 同规矩）
        ("star-2", new Vector2(970f, 90f)),     // 台2 上方（台面 y=140）
        ("star-3", new Vector2(-670f, 200f)),   // 台3 上方（台面 y=250）
    ];

    /// <summary>每颗星回多少血。</summary>
    public const int 星星回血 = 15;

    // —— 关卡配色（想微调就改这几个；视觉验收标准见 tests/GameProbe） ——
    // 背板**全透明**（主人 2026-09-22：只留关卡本体浮在桌面上）；地面/平台仍不透明（可读性）
    private const float 背板不透明度 = 0f;

    /// <summary>相机倍率（主人 2026-09-22「还是太大」再调小：1.3 → 1.1 → 0.8）。</summary>
    private const float 相机倍率 = 0.8f;
    private static readonly Color 背板色 = new(0.87f, 0.92f, 1.0f, 背板不透明度);
    private static readonly Color 地面色 = new(0.40f, 0.47f, 0.62f);
    private static readonly Color 平台色 = new(0.44f, 0.57f, 0.84f);
    // 平台可读性（2026-09-20 视觉复核后调）：顶面高光带 + 深色描边 —— 半透明底上边界要立得住
    private static readonly Color 顶面色 = new(0.72f, 0.82f, 0.98f);
    private static readonly Color 描边色 = new(0.14f, 0.19f, 0.30f);

    public static GameHost 单例 { get; private set; }

    /// <summary>是否已挂载（游戏世界活着）。探针只读。</summary>
    public static bool 挂载中 { get; private set; }

    private Vector2I _原尺寸;
    private Vector2I _原位置;
    private bool _有存壳;

    private CanvasLayer _背板层;
    private Node2D _世界;
    private GamePlayer _玩家;
    private Camera2D _相机;
    private CanvasLayer _界面层;
    private Node2D _精灵;

    // —— 玩法状态（玩法切片一） ——
    private readonly List<(string Id, Vector2 位, Node2D 节点)> _星星 = new();
    private Label _状态标签;
    private float _时间;
    private bool _累了;
    private float _透明度 = 1f;
    private Control _界面根;
    private bool _鼠标穿透 = true;   // 运行时状态；初值从 config/game.json 读（默认 true）

    // —— 失焦表现（主人 2026-09-22）：不聚焦 → 整窗 30% 不透明（像一片影子）+ 操作不响应 ——
    private const float 失焦透明度 = 0.30f;   // 不聚焦时整窗不透明度
    private const float 淡变速度 = 3.5f;      // 透明度过渡速度（每秒；1.0 ↔ 0.30 约 0.2s）

    // —— 探针访问器（只读） ——
    public Node2D 探针_世界 => _世界;
    public GamePlayer 探针_玩家 => _玩家;
    public Camera2D 探针_相机 => _相机;
    public Node2D 探针_精灵 => _精灵;
    public Node 探针_主场景根 => 主场景根();
    public bool 探针_累了 => _累了;

    /// <summary>探针：强制失焦（测「失焦变淡 / 失焦不响应」，不去真抢焦点）。</summary>
    public bool 探针_强制失焦 { get; set; }

    /// <summary>窗口是否聚焦（游戏操作只在聚焦时响应；失焦 → 变淡 + 忽略键盘）。</summary>
    public bool 聚焦中 => !探针_强制失焦 && GetWindow().HasFocus();
    public IReadOnlyList<(string Id, Vector2 位, Node2D 节点)> 探针_星星 => _星星;
    public string 探针_状态文本 => _状态标签?.Text ?? "";
    public static IReadOnlyList<(string Id, Vector2 位)> 探针_星星表 => 星星表;

    public override void _Ready()
    {
        单例 = this;
        Mode.ModeManager.ModeChanged += OnModeChanged;
    }

    private void OnModeChanged(Mode.ModeManager.Mode 模式)
    {
        if (模式 == Mode.ModeManager.Mode.Game) 进入游戏();
        else 退出游戏();
    }

    // ================= 进入 / 退出 =================

    private void 进入游戏()
    {
        if (挂载中) return;
        try
        {
            _累了 = false;
            // 1) 收起办公面板（游戏时不见；退出后不自动重开）
            ChatBox.隐藏();
            ToolBar.隐藏();
            SettingsWindow.隐藏();
            StatsWindow.隐藏();

            // 2) 办公表现让位：回 idle（自动让贴边隐藏复位、取消捏脸、停止走动）；
            //    随后 StateMachine._Process 顶闸会冻结所有办公自主行为
            StateMachine.SetState(StateMachine.Idle);

            // 3) 记壳 + 换壳：窗口铺满所在屏幕（全屏无边框）
            _原尺寸 = DisplayServer.WindowGetSize();
            _原位置 = DisplayServer.WindowGetPosition();
            _有存壳 = true;
            var 屏号 = DisplayServer.WindowGetCurrentScreen();
            DisplayServer.WindowSetSize(DisplayServer.ScreenGetSize(屏号));
            DisplayServer.WindowSetPosition(DisplayServer.ScreenGetPosition(屏号));
            _鼠标穿透 = 读鼠标穿透配置();
            设置鼠标穿透(_鼠标穿透);   // 纯键盘（主人 2026-09-22）：默认穿透点击桌面（M 键可切）

            // 4) 建世界 + 挂载
            建世界();
            挂载中 = true;
            GD.Print($"[Game] 进入游戏模式（窗口 {DisplayServer.WindowGetSize()} @ {DisplayServer.WindowGetPosition()}）");
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Game] 进入失败（回滚）: {e.Message}");
            卸载世界();
            还原壳();
        }
    }

    private void 退出游戏()
    {
        if (!挂载中)
        {
            还原壳();   // 保险：壳在、挂载没起来（进入半途/异常）→ 只还原
            return;
        }
        挂载中 = false;
        卸载世界();
        还原壳();
        StateMachine.SetState(StateMachine.Idle);   // 办公待机；动画由状态机重新接管
        GD.Print($"[Game] 退出游戏模式（窗口还原 {DisplayServer.WindowGetSize()} @ {DisplayServer.WindowGetPosition()}）");
    }

    /// <summary>鼠标穿透（主人 2026-09-22「纯键盘交互，鼠标穿透点击桌面」）：
    /// 引擎侧 = WM_NCHITTEST → HTTRANSPARENT，点击直达下方窗口/桌面；游戏内一律键盘。</summary>
    private void 设置鼠标穿透(bool 开) => GetWindow().SetFlag(Window.Flags.MousePassthrough, 开);

    /// <summary>读「鼠标穿透」配置（config/game.json；缺省 = true = 穿透）。</summary>
    private static bool 读鼠标穿透配置() =>
        !string.Equals(ConfigEdit.读文本("config/game.json", "鼠标穿透", "true").Trim(), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>M 键：鼠标穿透 ⇄ 可点击（主人 2026-09-22）；选择写回 config/game.json。</summary>
    private void 切换鼠标穿透()
    {
        _鼠标穿透 = !_鼠标穿透;
        设置鼠标穿透(_鼠标穿透);
        ConfigEdit.写("config/game.json", "鼠标穿透", _鼠标穿透);
        显示提示(_鼠标穿透 ? "鼠标：穿透（点桌面）" : "鼠标：可点击游戏", 1.2f);
        GD.Print($"[Game] 鼠标穿透 = {_鼠标穿透}（已写回配置）");
    }

    /// <summary>整窗透明度（世界 + 界面一起变；失焦变淡用）。</summary>
    private void 应用透明度(float α)
    {
        if (_世界 != null && IsInstanceValid(_世界)) _世界.Modulate = new Color(1f, 1f, 1f, α);
        if (_界面根 != null && IsInstanceValid(_界面根)) _界面根.Modulate = new Color(1f, 1f, 1f, α);
    }

    /// <summary>外部请求退出（Esc；纯键盘 —— 原右上角 ✕ 按钮已移除，主人 2026-09-22）。</summary>
    public static void 请求退出()
    {
        if (单例 == null || !挂载中) return;
        单例.记录检查点();
        Mode.ModeManager.SwitchMode(Mode.ModeManager.Mode.Office);
    }

    private void 记录检查点()
    {
        if (_玩家 != null && IsInstanceValid(_玩家))
            GameSession.设检查点(_玩家.GlobalPosition.X, _玩家.GlobalPosition.Y);
    }

    public override void _Input(InputEvent @event)
    {
        if (!挂载中) return;
        if (@event is InputEventKey { Pressed: true, PhysicalKeycode: Key.Escape })
        {
            请求退出();
            GetViewport().SetInputAsHandled();
        }
        if (@event is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.M })
        {
            切换鼠标穿透();
            GetViewport().SetInputAsHandled();
        }
    }

    // ================= 世界 =================

    private Node 主场景根() => GetParent();

    private void 建世界()
    {
        GameSession.载入();   // 懒载入先手：星星/血量/办公星都要按存档状态摆
        // 背板：整个屏幕一块浅色底（半透明 —— 游戏浮在桌面上；窗口本身是透明的）
        _背板层 = new CanvasLayer { Name = "GameBackdrop", Layer = -1 };
        AddChild(_背板层);
        var 底 = new ColorRect { Name = "Backdrop", Color = 背板色, MouseFilter = Control.MouseFilterEnum.Ignore };
        底.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _背板层.AddChild(底);

        _世界 = new Node2D { Name = "GameWorld" };
        AddChild(_世界);

        // 关卡拓扑（最小可玩：地面 + 三块浮台；后续要换场景文件从这里搬）
        // 布局约束（跳高 ≈128px、角色高 ≈240px）：①台面离可站面 100~128（跳得上去）；
        // ②一档档爬（台1→台2 再升 110）；③出生区两侧留空走廊 —— 台侧壁会拦人，别贴着出生点摆
        建平台(360f, -1000f, 1400f, 300f, 地面色);   // 地面
        建平台(250f, 520f, 820f, 22f, 平台色);       // 台1（右；地面→台1 升 110）
        建平台(140f, 820f, 1120f, 22f, 平台色);      // 台2（右高层；台1→台2 升 110）
        建平台(250f, -820f, -520f, 22f, 平台色);     // 台3（左；地面→台3 升 110）

        // 玩家：桌宠精灵**不销毁**，Reparent 进物理体（无缝切换的关键）
        _精灵 = 主场景根()?.GetNodeOrNull<Node2D>("AnimatedSprite2D");
        _玩家 = new GamePlayer { Name = "PetPlayer" };
        _玩家.AddChild(new CollisionShape2D
        {
            Name = "站姿碰撞",
            Shape = new RectangleShape2D { Size = new Vector2(88, 240) },
            Position = new Vector2(0, 1),
        });
        _世界.AddChild(_玩家);
        if (_精灵 != null)
        {
            _精灵.Reparent(_玩家, false);
            _精灵.Position = Vector2.Zero;
        }
        else GD.PrintErr("[Game] 找不到 AnimatedSprite2D —— 玩家没有外观");

        // 相机：世界子节点，由玩家每帧平滑跟随
        _相机 = new Camera2D { Name = "GameCamera", Zoom = new Vector2(相机倍率, 相机倍率) };
        _世界.AddChild(_相机);
        _相机.MakeCurrent();

        // 游戏内 UI（操作提示 + 状态行；纯键盘 —— 不设鼠标按钮，主人 2026-09-22）
        _界面层 = new CanvasLayer { Name = "GameUi", Layer = 10 };
        AddChild(_界面层);
        构建界面();

        // 出生：读检查点（进度保留）
        var 出生 = 出生点();
        _玩家.Position = 出生;
        _玩家.装配(出生, _相机);
        _玩家.相机吸附();   // 相机目标含「人物在屏幕中下」的纵向偏置（主人 2026-09-22）
        建星星();            // 收集品（已收集过的不再出现）
        刷新状态UI();
        GD.Print($"[Game] 世界就绪：出生 {出生}");
    }

    private void 卸载世界()
    {
        // 精灵放回主场景（同一节点；位置回办公居中）
        var 根 = 主场景根();
        if (_精灵 != null && IsInstanceValid(_精灵) && 根 != null && _精灵.GetParent() != 根)
        {
            _精灵.Reparent(根, false);
            PetWindow.同步角色();
        }
        if (_世界 != null) { _世界.QueueFree(); _世界 = null; }
        if (_背板层 != null) { _背板层.QueueFree(); _背板层 = null; }
        if (_界面层 != null) { _界面层.QueueFree(); _界面层 = null; }
        _星星.Clear();
        _相机 = null;
        _玩家 = null;
        _精灵 = null;
        _界面根 = null;
    }

    private void 还原壳()
    {
        设置鼠标穿透(false);   // 保险：所有退出路径都先把鼠标交还桌面
        if (!_有存壳) return;
        DisplayServer.WindowSetSize(_原尺寸);
        DisplayServer.WindowSetPosition(_原位置);
        _有存壳 = false;
    }

    private void 建平台(float 顶Y, float 左X, float 右X, float 厚, Color 色)
    {
        var 体 = new StaticBody2D { Name = $"平台_{顶Y:0}_{左X:0}" };
        体.AddChild(new CollisionShape2D
        {
            Shape = new RectangleShape2D { Size = new Vector2(右X - 左X, 厚) },
            Position = new Vector2((左X + 右X) / 2f, 顶Y + 厚 / 2f),
        });
        体.AddChild(new Polygon2D   // 描边（外扩 2.5px 深色底，先画 → 压在体下）
        {
            Color = 描边色,
            Polygon =
            [
                new Vector2(左X - 2.5f, 顶Y - 2.5f), new Vector2(右X + 2.5f, 顶Y - 2.5f),
                new Vector2(右X + 2.5f, 顶Y + 厚 + 2.5f), new Vector2(左X - 2.5f, 顶Y + 厚 + 2.5f),
            ],
        });
        体.AddChild(new Polygon2D
        {
            Color = 色,
            Polygon =
            [
                new Vector2(左X, 顶Y), new Vector2(右X, 顶Y),
                new Vector2(右X, 顶Y + 厚), new Vector2(左X, 顶Y + 厚),
            ],
        });
        体.AddChild(new Polygon2D   // 顶面高光带（可站面提示，9px）
        {
            Color = 顶面色,
            Polygon =
            [
                new Vector2(左X, 顶Y), new Vector2(右X, 顶Y),
                new Vector2(右X, 顶Y + 9f), new Vector2(左X, 顶Y + 9f),
            ],
        });
        _世界.AddChild(体);
    }

    private void 构建界面()
    {
        var 容器 = new Control { Name = "UiRoot", MouseFilter = Control.MouseFilterEnum.Ignore };
        容器.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _界面层.AddChild(容器);
        _界面根 = 容器;

        var 提示 = new Label { Name = "Hint", Text = "WASD 移动 · 空格 跳跃 · M 鼠标穿透 · Esc 退出游戏" };
        MicaTheme.应用(提示, 13);
        提示.AddThemeColorOverride("font_color", new Color(0.93f, 0.96f, 1.0f));   // 浅字
        提示.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.55f));
        提示.AddThemeConstantOverride("outline_size", 5);   // 深描边：砖面/天空都读得清
        提示.AnchorTop = 1f; 提示.AnchorBottom = 1f;
        提示.OffsetLeft = 16f; 提示.OffsetTop = -40f; 提示.OffsetBottom = -14f;
        容器.AddChild(提示);

        // 状态行（左上）：血量 / 收集进度 / 办公星 —— 深色药丸底 + 浅字（半透明背景下也读得清）
        var 状态底 = new PanelContainer { Name = "StatusChip", MouseFilter = Control.MouseFilterEnum.Ignore };
        var 底样式 = new StyleBoxFlat { BgColor = new Color(0.08f, 0.12f, 0.20f, 0.55f) };
        底样式.SetCornerRadiusAll(12);
        底样式.SetContentMarginAll(11f);
        状态底.AddThemeStyleboxOverride("panel", 底样式);
        状态底.OffsetLeft = 16f; 状态底.OffsetTop = 14f;
        _状态标签 = new Label { Name = "Status", Text = "" };
        MicaTheme.应用(_状态标签, 15);
        _状态标签.AddThemeColorOverride("font_color", new Color(0.93f, 0.96f, 1.0f));
        状态底.AddChild(_状态标签);
        容器.AddChild(状态底);
    }

    // ================= 玩法（切片一：hp / 收集品 / 玩累了） =================

    /// <summary>收集品落位：已收集过的不再出现（存档去重）。</summary>
    private void 建星星()
    {
        foreach (var (id, 位) in 星星表)
        {
            if (GameSession.有道具(id)) continue;
            var 星 = 造星星(位, $"星_{id}");
            _世界.AddChild(星);
            _星星.Add((id, 位, 星));
        }
    }

    /// <summary>五角星（纯多边形，不依赖素材）：金色本体 + 浅色内芯。</summary>
    private static Polygon2D 造星星(Vector2 位, string 名)
    {
        var 点 = new Vector2[10];
        for (var i = 0; i < 10; i++)
        {
            var 半径 = i % 2 == 0 ? 22f : 9f;
            var a = -Mathf.Pi / 2f + i * Mathf.Pi / 5f;
            点[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 半径;
        }
        var 星 = new Polygon2D { Name = 名, Color = new Color(1f, 0.84f, 0.30f), Polygon = 点, Position = 位 };
        var 芯点 = new Vector2[10];
        for (var i = 0; i < 10; i++) 芯点[i] = 点[i] * 0.45f;
        星.AddChild(new Polygon2D { Name = "芯", Color = new Color(1f, 0.95f, 0.75f), Polygon = 芯点 });
        return 星;
    }

    /// <summary>刷新左上状态行（血量 / 收集进度 / 办公星）。</summary>
    public void 刷新状态UI()
    {
        if (_状态标签 == null) return;
        var 收 = 0;
        foreach (var (id, _) in 星星表)
            if (GameSession.有道具(id)) 收++;
        _状态标签.Text = $"❤ {GameSession.血量}　⭐ {收}/{星星表.Length}　办公星 ×{GameSession.办公星}";
    }

    /// <summary>「玩累了」：血量见底 → 回满 + 回出生点 + 居中提示，**不退出游戏**（主人 2026-09-22：
    /// 死亡不该把人赶出游戏；要退出按 Esc）。只动游戏容器，办公侧无副作用。</summary>
    public void 玩累了()
    {
        if (!挂载中 || _累了) return;
        _累了 = true;
        GameSession.设血量(GameSession.血量上限);
        刷新状态UI();
        GD.Print("[Game] 玩累了 → 回满血、原地满血复活（不退出）");
        显示提示("玩累了…拍拍灰，满血复活！", 2.5f);
        GetTree().CreateTimer(2.5f).Timeout += () => { _累了 = false; };   // 提示消散后解锁：下一次见底还能触发
    }

    /// <summary>居中大号提示（秒数到自动消散；世界卸载时一并清掉）。</summary>
    private void 显示提示(string 文案, float 秒)
    {
        if (_界面层 == null) return;
        var 标签 = new Label
        {
            Name = "CenterToast",
            Text = 文案,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        MicaTheme.应用(标签, 22);
        标签.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _界面根.AddChild(标签);
        GetTree().CreateTimer(秒).Timeout += () => { if (IsInstanceValid(标签)) 标签.QueueFree(); };
    }

    public override void _Process(double delta)
    {
        if (!挂载中) return;
        _时间 += (float)delta;

        // 失焦 = 暂停变淡（主人 2026-09-22）：不聚焦时整窗降到 30% 不透明、操作不响应；聚焦即恢复
        var 目标α = 聚焦中 ? 1f : 失焦透明度;
        _透明度 = Mathf.MoveToward(_透明度, 目标α, 淡变速度 * (float)delta);
        应用透明度(_透明度);

        // 星星呼吸浮动（视觉提示）
        for (var i = 0; i < _星星.Count; i++)
        {
            var s = _星星[i];
            if (IsInstanceValid(s.节点))
                s.节点.Position = s.位 + new Vector2(0, Mathf.Sin(_时间 * 2.2f + i * 1.7f) * 5f);
        }

        // 收集判定（AABB 近似：玩家碰撞 88×240，星星半径 ~22；站着走过/跳过去都能碰到）
        if (_玩家 == null || !IsInstanceValid(_玩家)) return;
        for (var i = _星星.Count - 1; i >= 0; i--)
        {
            var s = _星星[i];
            var d = _玩家.GlobalPosition - s.位;
            if (Mathf.Abs(d.X) >= 64f || Mathf.Abs(d.Y) >= 140f) continue;
            GameSession.加道具(s.Id);
            GameSession.设血量(GameSession.血量 + 星星回血);
            if (IsInstanceValid(s.节点)) s.节点.QueueFree();
            _星星.RemoveAt(i);
            刷新状态UI();
            GD.Print($"[Game] 收集 {s.Id} → 血量 {GameSession.血量}");
        }
    }

    /// <summary>出生点：优先检查点（夹进关卡范围），否则默认出生。</summary>
    private Vector2 出生点()
    {
        GameSession.载入();
        if (GameSession.关卡 == 关卡id && (GameSession.检查点X != 0f || GameSession.检查点Y != 0f))
            return new Vector2(
                Mathf.Clamp(GameSession.检查点X, -950f, 950f),
                Mathf.Clamp(GameSession.检查点Y, -800f, 800f));
        return 默认出生;
    }
}
