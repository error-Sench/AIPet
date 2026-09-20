using Godot;
using desktop.script.UX;

namespace desktop.script.Game;

/// <summary>
/// 游戏模式的玩家控制（横板最小可玩）：**WSAD + 空格跳跃**，相机跟随，掉落回出生点。
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
    public const float 掉落重生Y = 900f;     // 掉出世界 → 回出生点
    public const float 检查点间隔秒 = 5f;    // 游玩中每 N 秒静默记一次检查点

    /// <summary>探针注入：非 0 时优先于真实键盘（确定性测试用；±1 = 左/右）。</summary>
    public float 探针_水平输入;

    /// <summary>探针注入：跳跃按住状态（确定性测试用；边沿触发，按住不会连跳）。</summary>
    public bool 探针_跳;

    private enum 姿态 { 站, 走, 空 }

    private Vector2 _出生点;
    private Camera2D _相机;
    private float _检查点计时 = 检查点间隔秒;
    private bool _跳跃上次;
    private 姿态 _姿态 = 姿态.空;
    private Polygon2D _影子;

    public override void _Ready()
    {
        // 影子（跳跃落点提示）：渲染在玩家下面、平台上面；跳出时留在地面，越远越淡
        var 点集 = new Vector2[24];
        for (var i = 0; i < 24; i++)
        {
            var a = i / 24f * Mathf.Tau;
            点集[i] = new Vector2(Mathf.Cos(a) * 46f, Mathf.Sin(a) * 12f);
        }
        _影子 = new Polygon2D { Color = new Color(0.08f, 0.12f, 0.25f, 0.20f), Polygon = 点集 };
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
        _跳跃上次 = Input.IsPhysicalKeyPressed(Key.Space);   // 确认弹窗的空格可能还按着 → 不当成跳跃
        GameSession.设关卡(GameHost.关卡id);
    }

    public override void _PhysicsProcess(double delta)
    {
        var dt = (float)delta;

        // —— 水平输入（物理键位 = 键盘布局无关） ——
        var 水平 = 探针_水平输入 != 0f
            ? Mathf.Sign(探针_水平输入)
            : (Input.IsPhysicalKeyPressed(Key.A) ? -1f : 0f) + (Input.IsPhysicalKeyPressed(Key.D) ? 1f : 0f);

        // —— 重力 + 跳跃（边沿触发，按住不连跳） ——
        Velocity = new Vector2(水平 * 跑速, Velocity.Y + 重力 * dt);
        var 按跳 = 探针_跳 || Input.IsPhysicalKeyPressed(Key.Space);
        if (按跳 && !_跳跃上次 && IsOnFloor()) Velocity = new Vector2(Velocity.X, -起跳速度);
        _跳跃上次 = 按跳;

        MoveAndSlide();

        // —— 动画（姿态切换才重播，避免每帧重播乱相位） ——
        更新动画(水平);

        // —— 影子跟随（射线找落点） ——
        更新影子();

        // —— 掉出世界 → 回出生点 ——
        if (GlobalPosition.Y > 掉落重生Y) 重生();

        // —— 相机跟随（指数平滑，不跟死） ——
        if (_相机 != null && IsInstanceValid(_相机))
            _相机.GlobalPosition = _相机.GlobalPosition.Lerp(GlobalPosition, 1f - Mathf.Exp(-7f * dt));

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
        var 新姿态 = !IsOnFloor() ? 姿态.空
            : Mathf.Abs(水平) > 0.1f ? 姿态.走
            : 姿态.站;
        if (新姿态 == _姿态) return;
        _姿态 = 新姿态;
        switch (新姿态)
        {
            case 姿态.走:
                CharAnim.PlayNamed(水平 < 0f ? "walk-left" : "walk-right");
                break;
            case 姿态.站:
                CharAnim.PlayState("idle");   // idle 池随机取一个姿势
                break;
            case 姿态.空:
                // 空中暂无专用素材（VPet MOVE 组：爬边/掉落/爬行 引入后再接）——保持当前帧
                break;
        }
    }

    /// <summary>回出生点（掉出世界用）。</summary>
    public void 重生()
    {
        GlobalPosition = _出生点;
        Velocity = Vector2.Zero;
        GD.Print("[Game] 掉出世界 → 回出生点");
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
        _影子.Color = new Color(0.08f, 0.12f, 0.25f, 0.20f * t);
    }

    /// <summary>探针：影子节点（只读）。</summary>
    public Node2D 探针_影子 => _影子;

    /// <summary>探针：当前姿态（只读）。</summary>
    public string 探针_姿态_只读 => _姿态.ToString();
}
