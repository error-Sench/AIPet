using Godot;
using desktop.script.UX;

namespace desktop.script.Game;

/// <summary>
/// 游戏模式的玩家控制（横板最小可玩）：**方向键移动 + C 跳跃 + X 攻击**（主人 2026-09-22 改键位），相机跟随，掉落回出生点。
/// 桌宠精灵（CharAnim）就挂在这个物理体上 —— 本体不销毁、只换壳；游戏期间
/// CharAnim 的办公播完回调被闸掉（见 CharAnim.OnAnimationFinished），动画归这里管。
/// 手感参数都是常数（主人可调，见代码顶部）。
/// </summary>
public partial class GamePlayer : CharacterBody2D
{
    // —— 手感旋钮（先给一套能玩的默认；改数值不用动结构） ——
    public const float 跑速 = 260f;          // 像素/秒
    public const float 重力 = 1500f;         // 像素/秒²
    public const float 起跳速度 = 620f;      // 起跳初速（向上，像素/秒）→ 跳高 ≈ 128px
    public const float 土狼时间秒 = 0.10f;   // 走离台缘后仍可起跳的宽限（coyote time）
    public const float 跳跃缓冲秒 = 0.12f;   // 落地前按下 → 落地自动起跳（jump buffer）
    public const float 松手保留系数 = 0.62f; // 可变跳高：上升中松手 → 升速截到这里（轻点=小跳 ≈68px）
    public const float 下落重力倍率 = 1.3f;  // 下落段重力加成：落地更快、不飘（起跳段不受影响）
    public const float 掉落重生Y = 900f;     // 掉出世界 → 回出生点
    public const float 检查点间隔秒 = 5f;    // 游玩中每 N 秒静默记一次检查点
    /// <summary>相机纵向比例（主人 2026-09-22「人物在屏幕中下」）：0.5 = 正中，越大人物越靠下。</summary>
    public const float 屏幕纵向比例 = 0.66f;
    public const int 掉落扣血 = 20;          // 掉出世界一次扣多少血（0 血 → 「玩累了」退场，见 GameHost）
    /// <summary>攻击状态时长（秒；期间再按不刷新 = 冷却即时长）。非 const：探针会临时改小以省时间轴（GameProbe）。</summary>
    public static float 攻击时长秒 = 0.45f;
    // —— 占位符（主人 2026-09-22「空缺动画用占位符替代」）：真素材到位后自动让位（回退链末位，见 播攻击动画/更新动画） ——
    /// <summary>攻击占位：挥拍暂代挥击（真素材 `attack-left/right` 到位即自动优先）。</summary>
    public const string 攻击占位 = "fidget-tennis";
    /// <summary>起跳占位：窜入→腾空→落地 暂代起跳（`enter-happy-2` 首帧空白是它自带的出场效果；
    /// 2026-09-24 动画组G：enter 池重导后旧名 `enter-2` → `enter-happy-2`）。</summary>
    public const string 起跳占位 = "enter-happy-2";

    /// <summary>探针注入：非 0 时优先于真实键盘（确定性测试用；±1 = 左/右）。</summary>
    public float 探针_水平输入;

    /// <summary>探针注入：跳跃按住状态（确定性测试用；边沿触发，按住不会连跳）。</summary>
    public bool 探针_跳;

    /// <summary>探针注入：攻击按下状态（确定性测试用；边沿触发）。</summary>
    public bool 探针_攻击;

    // 空中拆两相（主人 2026-09-20）：升 = 上升期保持起跳前姿态；落 = 过最高点才播 fall 素材
    private enum 姿态 { 站, 走, 升, 落 }

    private Vector2 _出生点;
    private Camera2D _相机;
    private float _检查点计时 = 检查点间隔秒;
    private bool _跳跃上次;
    private 姿态 _姿态 = 姿态.升;
    private Polygon2D _影子;
    private float _朝向 = 1f;
    private float _离地长 = 99f;     // 离地时长（土狼窗口用）
    private float _跳跃缓冲 = 99f;   // 「跳」按下沿距今时长（缓冲窗口用）
    private float _攻击剩余;         // 攻击状态剩余秒（>0 = 攻击中）
    private bool _攻击上次;          // 攻击按键沿检测
    private bool _攻击有素材;        // 攻击素材到位才保持攻击姿态（未到 = 只走状态/冷却，不干扰常规动画）
    private bool _动画脏;            // 强制下一帧重评估姿态（攻击收势用）

    public override void _Ready()
    {
        // 影子（跳跃落点提示）：渲染在玩家下面、平台上面；跳出时留在地面，越远越淡
        var 点集 = new Vector2[24];
        for (var i = 0; i < 24; i++)
        {
            var a = i / 24f * Mathf.Tau;
            点集[i] = new Vector2(Mathf.Cos(a) * 50f, Mathf.Sin(a) * 13f);
        }
        _影子 = new Polygon2D { Color = new Color(0.08f, 0.12f, 0.25f, 0.32f), Polygon = 点集 };
        var 世界 = GetParent();
        if (世界 != null)
        {
            世界.AddChild(_影子);
            世界.MoveChild(_影子, GetIndex());   // 排在玩家前一位：盖过平台、被玩家盖住
        }
    }

    /// <summary>由 GameHost 装配（出生点 + 相机）。</summary>
    public void 装配(Vector2 出生点, Camera2D 相机)
    {
        _出生点 = 出生点;
        _相机 = 相机;
        _跳跃上次 = Input.IsPhysicalKeyPressed(Key.C);   // 进入瞬间若 C 已按住 → 不当成起跳（空格现为弹窗确认键，无冲突）
        GameSession.设关卡(GameHost.关卡id);
    }

    public override void _PhysicsProcess(double delta)
    {
        var dt = (float)delta;

        // —— 水平输入（物理键位 = 键盘布局无关；方向键，主人 2026-09-22 改） ——
        // 失焦不响应操作（主人 2026-09-22）：真实键盘只在窗口聚焦时生效；探针注入不受影响
        var 聚焦 = GameHost.单例?.聚焦中 == true;
        var 水平 = 探针_水平输入 != 0f
            ? Mathf.Sign(探针_水平输入)
            : 聚焦 ? (Input.IsPhysicalKeyPressed(Key.Left) ? -1f : 0f) + (Input.IsPhysicalKeyPressed(Key.Right) ? 1f : 0f)
            : 0f;

        // —— 跳跃输入：按下沿记缓冲；离地计时供土狼窗口 ——
        var 按跳 = 探针_跳 || (聚焦 && Input.IsPhysicalKeyPressed(Key.C));
        if (按跳 && !_跳跃上次) _跳跃缓冲 = 0f; else _跳跃缓冲 += dt;
        _跳跃上次 = 按跳;
        if (IsOnFloor()) _离地长 = 0f; else _离地长 += dt;

        // —— 攻击（X，主人 2026-09-22）：边沿触发；攻击时长内再按无效（冷却 = 时长） ——
        var 按攻击 = 探针_攻击 || (聚焦 && Input.IsPhysicalKeyPressed(Key.X));
        if (按攻击 && !_攻击上次 && _攻击剩余 <= 0f)
        {
            _攻击剩余 = 攻击时长秒;
            播攻击动画();
        }
        _攻击上次 = 按攻击;
        if (_攻击剩余 > 0f)
        {
            _攻击剩余 -= dt;
            if (_攻击剩余 <= 0f) { _攻击剩余 = 0f; _动画脏 = true; }   // 收势：常规姿态重播接管
        }

        // —— 重力（下落段加成）+ 起跳（土狼时间 + 跳跃缓冲：经典三件套） ——
        var 重力系数 = Velocity.Y > 0f ? 下落重力倍率 : 1f;
        Velocity = new Vector2(水平 * 跑速, Velocity.Y + 重力 * 重力系数 * dt);
        if (_跳跃缓冲 <= 跳跃缓冲秒 && _离地长 <= 土狼时间秒)
        {
            Velocity = new Vector2(Velocity.X, -起跳速度);
            _跳跃缓冲 = 99f;   // 用掉：同一次按键不二连跳
            _离地长 = 99f;     // 用掉：土狼窗口关闸
        }

        // —— 可变跳高：上升途中松手 → 升速截到保留系数（轻点 = 小跳，按住 = 满跳） ——
        if (!按跳 && Velocity.Y < -起跳速度 * 松手保留系数)
            Velocity = new Vector2(Velocity.X, -起跳速度 * 松手保留系数);

        MoveAndSlide();

        // —— 动画（姿态/朝向变化才重播，避免每帧重播乱相位） ——
        更新动画(水平);

        // —— 影子跟随（射线找落点） ——
        更新影子();

        // —— 掉出世界 → 回出生点 ——
        if (GlobalPosition.Y > 掉落重生Y) 重生();

        // —— 相机跟随（指数平滑，不跟死；目标含「人物在屏幕中下」的纵向偏置） ——
        if (_相机 != null && IsInstanceValid(_相机))
            _相机.GlobalPosition = _相机.GlobalPosition.Lerp(相机目标(), 1f - Mathf.Exp(-7f * dt));

        // —— 检查点节流（进度保留；退出时 GameHost 还会立刻补记一次） ——
        _检查点计时 -= dt;
        if (_检查点计时 <= 0f)
        {
            _检查点计时 = 检查点间隔秒;
            GameSession.设检查点(GlobalPosition.X, GlobalPosition.Y);
        }
    }

    private void 更新动画(float 水平)
    {
        var 有输入 = Mathf.Abs(水平) > 0.1f;
        // 攻击中（且素材已到）：地面保持攻击姿态（换向 = 换手重播）；空中交给 升/落（计时照走）
        if (_攻击剩余 > 0f && _攻击有素材 && IsOnFloor())
        {
            if (有输入 && Mathf.Sign(水平) != _朝向)
            {
                _朝向 = Mathf.Sign(水平);
                播攻击动画();
            }
            return;
        }
        var 新姿态 = !IsOnFloor() ? (Velocity.Y < 0f ? 姿态.升 : 姿态.落)
            : 有输入 ? 姿态.走
            : 姿态.站;
        var 姿态变了 = 新姿态 != _姿态;
        var 朝向变了 = 有输入 && Mathf.Sign(水平) != _朝向;
        if (!姿态变了 && !朝向变了 && !_动画脏) return;   // 反向输入 = 立刻转身（主人 2026-09-20：地面走中 / 空中都要及时）
        _动画脏 = false;
        _姿态 = 新姿态;
        if (有输入) _朝向 = Mathf.Sign(水平);
        switch (新姿态)
        {
            case 姿态.走:
                CharAnim.PlayNamed(_朝向 < 0f ? "walk-left" : "walk-right");
                break;
            case 姿态.站:
                CharAnim.PlayState("idle");   // idle 池随机取一个姿势
                break;
            case 姿态.升:
            {
                // 上升期：优先播起跳素材（主人 2026-09-20 点名——素材未到、逻辑先接：
                // `jump-left/right` 缺则回退 `jump`，再缺保持起跳前姿态；下坠素材只在过最高点后才播）
                var 起跳名 = _朝向 < 0f ? "jump-left" : "jump-right";
                if (CharAnim.有动画(起跳名)) CharAnim.PlayNamed(起跳名);
                else if (CharAnim.有动画("jump")) CharAnim.PlayNamed("jump");
                else if (CharAnim.有动画(起跳占位))
                {
                    // 占位（真素材到位自动走上面两条）；已在播就不重播——空中转身不重启
                    if (CharAnim.当前动画名_只读 != 起跳占位) CharAnim.PlayNamed(起跳占位);
                }
                else if (朝向变了) CharAnim.PlayNamed(_朝向 < 0f ? "walk-left" : "walk-right");
                break;
            }
            case 姿态.落:
                // 过最高点 = 下落：VPet `fall-B`（横着下落，循环）；转身换向、落地由 站/走 接管
                CharAnim.PlayNamed(_朝向 < 0f ? "fall-left-b" : "fall-right-b");
                break;
        }
    }

    /// <summary>攻击姿态：`attack-left/right` → 缺则 `attack` → 再缺保持当前姿态（与起跳槽位同规矩：
    /// 素材未到、逻辑先接；请求名留给探针断言，素材到位后随池自动生效）。</summary>
    private void 播攻击动画()
    {
        var 名 = _朝向 < 0f ? "attack-left" : "attack-right";
        探针_最近攻击请求 = 名;
        _攻击有素材 = CharAnim.有动画(名);
        if (_攻击有素材) { if (CharAnim.当前动画名_只读 != 名) CharAnim.PlayNamed(名); return; }
        _攻击有素材 = CharAnim.有动画("attack");
        if (_攻击有素材) { if (CharAnim.当前动画名_只读 != "attack") CharAnim.PlayNamed("attack"); return; }
        // 占位（真素材到位自动走上面两条）
        if (CharAnim.有动画(攻击占位))
        {
            _攻击有素材 = true;
            if (CharAnim.当前动画名_只读 != 攻击占位) CharAnim.PlayNamed(攻击占位);
        }
    }

    /// <summary>相机目标点：玩家位置 + 纵向偏置（人物落在屏幕中下，主人 2026-09-22）。</summary>
    public Vector2 相机目标()
    {
        var 视口高 = GetViewportRect().Size.Y / (_相机?.Zoom.Y ?? 1f);
        return GlobalPosition + new Vector2(0f, -(屏幕纵向比例 - 0.5f) * 视口高);
    }

    /// <summary>相机瞬移吸附到目标点（出生 / 重生用）。</summary>
    public void 相机吸附()
    {
        if (_相机 != null && IsInstanceValid(_相机)) _相机.GlobalPosition = 相机目标();
    }

    /// <summary>回出生点（掉出世界用）。相机瞬移吸附 —— 别从原地慢慢平移过去。扣血、血空 → 「玩累了」。</summary>
    public void 重生()
    {
        GlobalPosition = _出生点;
        Velocity = Vector2.Zero;
        相机吸附();
        GameSession.设血量(GameSession.血量 - 掉落扣血);
        GD.Print($"[Game] 掉出世界 → 回出生点（血量 {GameSession.血量}）");
        GameHost.单例?.刷新状态UI();
        if (GameSession.血量 <= 0) GameHost.单例?.玩累了();
    }

    /// <summary>影子落点：从脚下往下打一条射线，贴到最近的可站面（越远越大越淡）。</summary>
    private void 更新影子()
    {
        if (_影子 == null || !IsInstanceValid(_影子)) return;
        var 查 = PhysicsRayQueryParameters2D.Create(GlobalPosition + new Vector2(0, -8f), GlobalPosition + new Vector2(0, 800f));
        查.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
        var 果 = GetWorld2D().DirectSpaceState.IntersectRay(查);
        if (果.Count == 0) { _影子.Visible = false; return; }
        var 落点 = 果["position"].AsVector2();
        var 距 = Mathf.Max(0f, 落点.Y - GlobalPosition.Y);
        var t = Mathf.Clamp(1f - (距 - 128f) / 420f, 0.30f, 1f);   // 脚贴地最实；越高越淡
        _影子.Visible = true;
        _影子.GlobalPosition = new Vector2(GlobalPosition.X, 落点.Y - 3f);
        _影子.Scale = new Vector2(t, t);
        _影子.Color = new Color(0.08f, 0.12f, 0.25f, 0.32f * t);   // 0.32 起：压在平台高光带上也看得清
    }

    /// <summary>探针：影子节点（只读）。</summary>
    public Node2D 探针_影子 => _影子;

    /// <summary>探针：当前姿态（只读）。</summary>
    public string 探针_姿态_只读 => _姿态.ToString();

    /// <summary>探针：当前朝向（只读；±1）。</summary>
    public float 探针_朝向_只读 => _朝向;

    /// <summary>探针：最近一次攻击请求的素材名（如 attack-left；素材未到也记录）。</summary>
    public string 探针_最近攻击请求 { get; private set; } = "";

    /// <summary>探针：是否攻击中（只读）。</summary>
    public bool 探针_攻击中_只读 => _攻击剩余 > 0f;
}
