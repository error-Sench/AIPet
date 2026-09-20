using System;
using Godot;
using desktop.script.State;
using desktop.script.UX;

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

    // —— 关卡配色（想微调就改这三个；视觉验收标准见 tests/GameProbe） ——
    private static readonly Color 背板色 = new(0.87f, 0.92f, 1.0f);
    private static readonly Color 地面色 = new(0.40f, 0.47f, 0.62f);
    private static readonly Color 平台色 = new(0.44f, 0.57f, 0.84f);

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

    // —— 探针访问器（只读） ——
    public Node2D 探针_世界 => _世界;
    public GamePlayer 探针_玩家 => _玩家;
    public Camera2D 探针_相机 => _相机;
    public Node2D 探针_精灵 => _精灵;
    public Node 探针_主场景根 => 主场景根();

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

    /// <summary>外部请求退出（右上角 ✕ / Esc）：先补记检查点再切模式（切换会自动存盘）。</summary>
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
    }

    // ================= 世界 =================

    private Node 主场景根() => GetParent();

    private void 建世界()
    {
        // 背板：整个屏幕一块浅色底（窗口是透明的，得自己铺不透明背景）
        _背板层 = new CanvasLayer { Name = "GameBackdrop", Layer = -1 };
        AddChild(_背板层);
        var 底 = new ColorRect { Name = "Backdrop", Color = 背板色, MouseFilter = Control.MouseFilterEnum.Ignore };
        底.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _背板层.AddChild(底);

        _世界 = new Node2D { Name = "GameWorld" };
        AddChild(_世界);

        // 关卡拓扑（最小可玩：地面 + 三块浮台；后续要换场景文件从这里搬）
        建平台(360f, -1000f, 1000f, 300f, 地面色);   // 地面
        建平台(240f, 120f, 430f, 22f, 平台色);
        建平台(140f, -430f, -120f, 22f, 平台色);
        建平台(250f, -880f, -620f, 22f, 平台色);

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
        _相机 = new Camera2D { Name = "GameCamera", Zoom = new Vector2(1.3f, 1.3f) };
        _世界.AddChild(_相机);
        _相机.MakeCurrent();

        // 游戏内 UI（退出按钮 + 操作提示）
        _界面层 = new CanvasLayer { Name = "GameUi", Layer = 10 };
        AddChild(_界面层);
        构建界面();

        // 出生：读检查点（进度保留）
        var 出生 = 出生点();
        _玩家.Position = 出生;
        _相机.GlobalPosition = 出生;
        _玩家.装配(出生, _相机);
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
        _相机 = null;
        _玩家 = null;
        _精灵 = null;
    }

    private void 还原壳()
    {
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
        体.AddChild(new Polygon2D
        {
            Color = 色,
            Polygon =
            [
                new Vector2(左X, 顶Y), new Vector2(右X, 顶Y),
                new Vector2(右X, 顶Y + 厚), new Vector2(左X, 顶Y + 厚),
            ],
        });
        _世界.AddChild(体);
    }

    private void 构建界面()
    {
        var 容器 = new Control { Name = "UiRoot", MouseFilter = Control.MouseFilterEnum.Ignore };
        容器.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _界面层.AddChild(容器);

        var 退出 = new Button { Name = "ExitGame", Text = "✕ 退出游戏", TooltipText = "切回办公模式（Esc）" };
        MicaTheme.应用强调按钮(退出, 13);
        退出.AnchorLeft = 1f; 退出.AnchorRight = 1f; 退出.AnchorTop = 0f; 退出.AnchorBottom = 0f;
        退出.OffsetLeft = -132f; 退出.OffsetRight = -16f; 退出.OffsetTop = 16f; 退出.OffsetBottom = 50f;
        退出.Pressed += 请求退出;
        容器.AddChild(退出);

        var 提示 = new Label { Name = "Hint", Text = "WASD 移动 · 空格 跳跃 · Esc 退出游戏" };
        MicaTheme.应用(提示, 13);   // 主文字色 + 13px：浅背景上够醒目（视觉复核后调过一次）
        提示.AnchorTop = 1f; 提示.AnchorBottom = 1f;
        提示.OffsetLeft = 16f; 提示.OffsetTop = -40f; 提示.OffsetBottom = -14f;
        容器.AddChild(提示);
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
