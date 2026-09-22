using desktop.script.Game;
using desktop.script.Mode;
using desktop.script.State;
using desktop.script.UX;
using desktop.script.Util;
using Godot;

namespace desktop.tests;

/// <summary>
/// 游戏模式探针（**非 headless**：要真实窗口几何、真实物理与渲染截图）：
/// ① 确认弹窗：Esc 取消（留在办公）/ 空格确认（进入游戏；主人指定空格确认）；
/// ② 挂载：窗口铺满屏幕、世界/玩家/相机就位、桌宠精灵 Reparent 进物理体、办公面板收起；
/// ③ 最小可玩：着地 / 方向键位移（含走动画）/ C 跳跃（起跳-落地，空中播 `fall-B`）/ X 攻击（G 组）/
///    相机跟随（含「人物在屏幕中下」偏置）/ 走中与空中即时转身 / 背板全透明采样（天空 alpha≈0、地面不透明）；
/// ③b 手感三件套：F1 土狼时间（离台缘 3 帧内还能跳）/ F2 跳跃缓冲（落地前按 → 自动起跳）/
///    F3 可变跳高（轻点 = 小跳）；
/// ④ 退出：**窗口几何原样还原**、精灵回主场景居中、世界清理、回到 idle、检查点已写进存档；
/// ⑤ 「办公即游戏」钩子：干完一次活 → 办公星 +1；
/// ⑥ 二进宫（玩法切片一）：收集星星（回血 + 存档去重）/ 掉落扣血 / 血空「玩累了」回满血原地继续（**不退出**）；
/// ⑧ 纯键盘（主人 2026-09-22）：游戏内鼠标穿透开/关断言 + Esc 退出 + **失焦变淡**（α≈0.30）+ 失焦不响应操作 + M 键切换（写回配置，探针重定向）；
/// ⑨ 键位改版（主人 2026-09-22）：方向键移动 / C 跳跃 / X 攻击（真实键注入，G 组）+ 占位符（攻击/起跳）；
/// ⑦ 截两帧 PNG（站立 / 空中）供视觉复核。
/// 隔离：游戏存档走临时档（GameSession.探针_覆盖存盘路径），收尾只删临时档。
/// 用法：Godot_..._console.exe --path &lt;项目&gt; res://tests/GameProbe.tscn
/// </summary>
public partial class GameProbe : Node
{
    private int _帧;
    private int _失败;
    private Vector2I _原尺寸;
    private Vector2I _原位置;
    private float _走前X;
    private float _跳前Y;
    private float _最高Y;
    private float _退出前X;
    private float _失焦前X;
    private float _转身前X;
    private int _F步骤;
    private int _F离地帧;
    private float _F1按下Y;
    private int _F1按帧;
    private bool _F2按了;
    private int _F2按帧;
    private int _F基准;
    private float _F3按下Y;
    private float _F3最低Y;
    private int _退出帧;

    public override void _Ready()
    {
        // 隔离：游戏存档走临时档
        GameSession.探针_覆盖存盘路径 = "user://probe_game_mount_tmp.json";
        try { System.IO.File.Delete(GameSession.探针_存盘路径); } catch { /* 上轮残留 */ }
        // 隔离：配置写回走临时根（M 键切换的写回不碰真配置）
        ConfigEdit.探针_根目录 = ProjectSettings.GlobalizePath("user://probe_cfg_tmp");

        // 省时间轴：攻击时长临时压到 0.15s（9 帧）——G 组只验「触发/冷却/收势」时序（默认 0.45）
        GamePlayer.攻击时长秒 = 0.15f;

        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        _原尺寸 = DisplayServer.WindowGetSize();
        _原位置 = DisplayServer.WindowGetPosition();
        GD.Print($"=== GameProbe: 起始窗口 {_原尺寸} @ {_原位置} ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[GP] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[GP] FAIL  {描述}"); }
    }

    private void 截图(string 名)
    {
        var 图 = GetWindow().GetTexture()?.GetImage();
        if (图 == null) { 断言(false, $"截图失败（{名}）"); return; }
        var 路径 = ProjectSettings.GlobalizePath($"user://{名}");
        图.SavePng(路径);
        GD.Print($"[GP] 截图 {名} = {路径}（{图.GetWidth()}x{图.GetHeight()}）");
    }

    private static void 敲键(Key 键, bool 按下) =>
        Input.ParseInputEvent(new InputEventKey { Keycode = 键, PhysicalKeycode = 键, Pressed = 按下 });

    private GamePlayer 玩家 => GameHost.单例?.探针_玩家;

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            // ===== ① 确认弹窗：Esc 取消 =====
            case 10:
                GameEntryDialog.请求进入();
                break;
            case 18:
                断言(GameEntryDialog.探针_可见, "A1 请求进入 → 弹窗弹出");
                敲键(Key.Escape, true);
                break;
            case 22:
                敲键(Key.Escape, false);
                断言(!GameEntryDialog.探针_可见, "A2 Esc → 弹窗关闭");
                断言(GameEntryDialog.探针_最近动作 == "取消", $"A3 记的是「取消」（实际「{GameEntryDialog.探针_最近动作}」）");
                断言(ModeManager.CurrentMode == ModeManager.Mode.Office, "A4 仍留在办公模式");
                break;

            // ===== ② 确认弹窗：空格确认 → 挂载 =====
            case 30:
                GameEntryDialog.请求进入();
                break;
            case 38:
                断言(GameEntryDialog.探针_可见, "B1 弹窗再次弹出");
                敲键(Key.Space, true);
                break;
            case 42:
                敲键(Key.Space, false);
                断言(GameEntryDialog.探针_最近动作 == "进入", $"B2 空格 → 确认进入（实际「{GameEntryDialog.探针_最近动作}」）");
                断言(ModeManager.CurrentMode == ModeManager.Mode.Game, "B3 切到游戏模式");
                断言(GameHost.挂载中, "B4 世界已挂载");
                break;
            case 44:
                DisplayServer.WindowMoveToForeground();   // 保证聚焦（失焦会变淡 + 不响应，像素断言要确定性）
                break;
            case 48:
            {
                var 屏号 = DisplayServer.WindowGetCurrentScreen();
                断言(DisplayServer.WindowGetSize() == DisplayServer.ScreenGetSize(屏号),
                    $"B5 窗口铺满屏幕（{DisplayServer.WindowGetSize()} vs 屏 {DisplayServer.ScreenGetSize(屏号)}）");
                断言(DisplayServer.WindowGetPosition() == DisplayServer.ScreenGetPosition(屏号), "B6 窗口贴屏幕原点");
                断言(!ChatBox.可见 && !ToolBar.可见 && !SettingsWindow.可见 && !StatsWindow.可见_探针, "B7 办公面板已收起");
                var 宿主 = GameHost.单例;
                断言(宿主?.探针_世界 != null && 宿主.探针_玩家 != null && 宿主.探针_相机 != null, "B8 世界/玩家/相机就位");
                断言(宿主?.探针_精灵?.GetParent() == 宿主.探针_玩家, "B9 桌宠精灵 Reparent 进物理体（本体不销毁）");
                断言(宿主?.探针_相机?.IsCurrent() == true, "B10 相机已接管视图");
                断言(GetWindow().GetFlag(Window.Flags.MousePassthrough), "B11 游戏内鼠标穿透已开（点击直达桌面，纯键盘）");
                断言(GameHost.单例.聚焦中, "B12 进入后窗口聚焦（键盘可操作）");
                break;
            }

            // ===== ③ 最小可玩：着地 / 走 / 跳 =====
            case 56:
                断言(玩家?.IsOnFloor() == true, "C1 出生后着地");
                断言(Mathf.Abs(玩家.Position.X) < 8f, $"C2 出生在检查点/默认点附近（X={玩家.Position.X:0.0}）");
                _走前X = 玩家.Position.X;
                玩家.探针_水平输入 = 1f;
                break;
            case 92:
                玩家.探针_水平输入 = 0f;
                // 阈值 120：出生区走廊要畅通（右侧崖壁在 x≈476）。旧布局台1 贴脸，走到 76 就被侧壁挡，
                // 阈值 60 没揪住 —— 布局重排后按 120 锁死
                断言(玩家.Position.X - _走前X > 120f, $"C3 D 键方向行走 + 走廊畅通（{_走前X:0.0} → {玩家.Position.X:0.0}）");
                断言(CharAnim.当前动画名_只读 == "walk-right", $"C4 行走动画 = walk-right（实际 {CharAnim.当前动画名_只读}）");
                break;
            case 100:
                _跳前Y = 玩家.Position.Y;
                _最高Y = _跳前Y;
                玩家.探针_跳 = true;
                break;
            case 110:
                断言(!CharAnim.当前动画名_只读.StartsWith("fall"), $"C5e 上升期不播下坠素材（fall 只在过最高点后，实际 {CharAnim.当前动画名_只读}）");
                断言(CharAnim.当前动画名_只读 == GamePlayer.起跳占位, $"C5e2 上升期播起跳占位（实际 {CharAnim.当前动画名_只读}）");
                break;
            case 112:
                玩家.探针_跳 = false;   // 按住 12 帧（0.2s）→ 满跳 ≈128px（可变跳高不截）
                break;
            case 130:
                _最高Y = Mathf.Min(_最高Y, 玩家.Position.Y);
                断言(!玩家.IsOnFloor(), "C5 上升段：已离地");
                断言(玩家.探针_影子?.Visible == true, "C5b 空中时影子仍可见（落点提示）");
                断言(玩家.探针_影子 != null && Mathf.Abs(玩家.探针_影子.GlobalPosition.Y - 360f) < 8f,
                    $"C5c 影子落在地面高度（{玩家.探针_影子?.GlobalPosition.Y:0.0} ≈ 360）");
                断言(CharAnim.当前动画名_只读 == "fall-right-b", $"C5d 空中播 fall-B 下落素材（实际 {CharAnim.当前动画名_只读}）");
                break;
            case 136:   // —— 失焦表现（主人 2026-09-22）：变淡 + 不响应操作 ——
                GameHost.单例.探针_强制失焦 = true;
                _失焦前X = 玩家.Position.X;
                敲键(Key.Right, true);      // 注入真实键（方向键；失焦应被无视）
                break;
            case 166:
                断言(Mathf.Abs(玩家.Position.X - _失焦前X) < 2f,
                    $"C5f 失焦不响应操作（→ 按住 30 帧，X 未动 {_失焦前X:0.0} → {玩家.Position.X:0.0}）");
                断言(Mathf.Abs(GameHost.单例.探针_世界.Modulate.A - 0.30f) < 0.05f,
                    $"C5g 失焦变淡：世界 α={GameHost.单例.探针_世界.Modulate.A:0.00} ≈ 0.30");
                {
                    var 图 = GetWindow().GetTexture()?.GetImage();
                    var 地 = 图?.GetPixel(960, 900) ?? new Color(1, 1, 1, 1);
                    // 地面 = 描边层 + 本体层两层叠画：各 0.30 → 合成 1-0.7² = 0.51（全不透明时两层都=1，看不出来）
                    断言(地.A is > 0.3f and < 0.75f, $"C5h 失焦时地面像素也变淡（alpha={地.A:0.00}，叠层混合 ≈0.51）");
                }
                敲键(Key.Right, false);
                GameHost.单例.探针_强制失焦 = false;
                break;
            case 182:
                断言(GameHost.单例.探针_世界.Modulate.A > 0.9f,
                    $"C5i 聚焦恢复：世界 α={GameHost.单例.探针_世界.Modulate.A:0.00} ≈ 1.0");
                break;

            case 100 + 90:
                _最高Y = Mathf.Min(_最高Y, 玩家.Position.Y);
                断言(玩家.IsOnFloor(), "C6 已落回地面");
                断言(Mathf.Abs(玩家.Position.Y - _跳前Y) < 8f, $"C7 落回原高度（{_跳前Y:0.0} → {玩家.Position.Y:0.0}）");
                断言(_最高Y < _跳前Y - 80f, $"C8 真的跳起来了（最高 {_最高Y:0.0}，起跳前 {_跳前Y:0.0}）");
                {
                    var 视口高 = GetViewport().GetVisibleRect().Size.Y / GameHost.单例.探针_相机.Zoom.Y;
                    var 期望 = 玩家.Position + new Vector2(0f, -(GamePlayer.屏幕纵向比例 - 0.5f) * 视口高);
                    断言(GameHost.单例.探针_相机.GlobalPosition.DistanceTo(期望) < 80f,
                        $"C9 相机跟随 + 「人物在屏幕中下」偏置（相机 {GameHost.单例.探针_相机.GlobalPosition:0} vs 期望 {期望:0}）");
                }
                截图("game_stand.png");
                break;
            case 194:
            {
                // 背板全透明（主人 2026-09-22 改）：天空区完全透明、地面区不透明 —— 从窗口画面采样
                var 图 = GetWindow().GetTexture()?.GetImage();
                if (图 == null) { 断言(false, "C10 取窗口画面失败"); break; }
                var 天 = 图.GetPixel(960, 120);
                var 地 = 图.GetPixel(960, 900);   // 别贴地面底缘（倍率变化时容易采样到虚空）
                断言(天.A < 0.05f, $"C10 背板全透明（天空 alpha={天.A:0.00}）");
                断言(地.A > 0.95f, $"C11 地面不透明（alpha={地.A:0.00}）");
                var vp = GetViewport().GetVisibleRect().Size;
                var 屏位 = (玩家.GlobalPosition - GameHost.单例.探针_相机.GlobalPosition) * GameHost.单例.探针_相机.Zoom + vp / 2f;
                断言(Mathf.Abs(屏位.X - vp.X / 2f) < 60f && Mathf.Abs(屏位.Y - vp.Y * GamePlayer.屏幕纵向比例) < 60f,
                    $"C9b 人物在屏幕中下（屏位 {屏位.X:0},{屏位.Y:0} vs 期望 {vp.X / 2f:0},{vp.Y * GamePlayer.屏幕纵向比例:0}）");
                break;
            }
            case 198:
                玩家.探针_水平输入 = 1f;    // 先往右走
                break;
            case 210:
                _转身前X = 玩家.Position.X;
                玩家.探针_水平输入 = -1f;   // 走中反向 → 应立刻转身
                break;
            case 222:
                断言(CharAnim.当前动画名_只读 == "walk-left", $"C12 走中即时转身（实际 {CharAnim.当前动画名_只读}）");
                断言(玩家.Position.X < _转身前X - 20f, $"C12b 确实在往左走（{_转身前X:0.0} → {玩家.Position.X:0.0}）");
                break;
            case 226:
                玩家.探针_跳 = true;        // 带方向起跳（往左）
                break;
            case 230:
                玩家.探针_跳 = false;
                break;
            case 238:
                断言(!玩家.IsOnFloor(), "C13 空中（即将转身）");
                玩家.探针_水平输入 = 1f;    // 空中反向 → 应立刻转身
                break;
            case 248:
                断言(!玩家.IsOnFloor(), "C13b 仍在空中");
                断言(CharAnim.当前动画名_只读 == "fall-right-b", $"C13c 空中即时转身（fall 素材换向；实际 {CharAnim.当前动画名_只读}）");
                break;
            case 252:
                截图("game_jump.png");      // 空中 + 转身后的画面
                break;

            // ===== ④b 手感三件套（F 组，主人 2026-09-20）：土狼 / 缓冲 / 可变跳高 =====
            // ===== B13~B16 M 键鼠标穿透切换（主人 2026-09-22；写回走探针重定向） =====
            case 258:
                敲键(Key.M, true);
                break;
            case 264:
                断言(!GetWindow().GetFlag(Window.Flags.MousePassthrough), "B13 M 键 → 鼠标可点击（穿透关）");
                断言(ConfigEdit.读文本("config/game.json", "鼠标穿透", "?") == "false", $"B14 选择已写回配置（读到 {ConfigEdit.读文本("config/game.json", "鼠标穿透", "?")}）");
                敲键(Key.M, false);
                break;
            case 268:
                敲键(Key.M, true);
                break;
            case 274:
                断言(GetWindow().GetFlag(Window.Flags.MousePassthrough), "B15 再按 M → 恢复穿透");
                断言(ConfigEdit.读文本("config/game.json", "鼠标穿透", "?") == "true", "B16 配置写回 true");
                敲键(Key.M, false);
                break;

            // ===== G 组：攻击 X（主人 2026-09-22；攻击时长已压到 9 帧） =====
            case 276:
                敲键(Key.X, true);   // 真实键位：X 攻击（此刻朝向右）
                break;
            case 279:
                敲键(Key.X, false);
                断言(玩家.探针_攻击中_只读, "G1 X 键 → 进入攻击状态");
                断言(玩家.探针_最近攻击请求 == "attack-right", $"G2 朝右攻击请求 attack-right（实际 {玩家.探针_最近攻击请求}）");
                断言(CharAnim.池已注册("attack"), "G3 攻击池已登记（素材导入即生效）");
                断言(CharAnim.当前动画名_只读 == GamePlayer.攻击占位, $"G3b 攻击播占位（实际 {CharAnim.当前动画名_只读}）");
                break;
            case 282:
                敲键(Key.X, true);   // 冷却内再按：不应刷新时长
                break;
            case 284:
                敲键(Key.X, false);
                break;
            case 287:
                断言(!玩家.探针_攻击中_只读, "G4 攻击时长到 → 自动收势；期间再按不刷新（否则会延续到 291）");
                break;

            case 290:
                断言(玩家.IsOnFloor(), "C14 空中转身后正常落回地面");
                断言(CharAnim.当前动画名_只读 == "walk-right", $"C14b 落地后回常规姿态（实际 {CharAnim.当前动画名_只读}）");
                break;
            case 292:
                玩家.探针_水平输入 = 0f;      // 让真实键盘接管（验证方向键）
                敲键(Key.Left, true);
                break;
            case 294:
                断言(玩家.探针_朝向_只读 < 0f, $"G5 方向键 ← 真实生效（朝向 {玩家.探针_朝向_只读:0}）");
                断言(CharAnim.当前动画名_只读 == "walk-left", $"G5b 走动画同步（实际 {CharAnim.当前动画名_只读}）");
                敲键(Key.Left, false);
                玩家.探针_攻击 = true;        // 朝左攻击 → 请求 attack-left
                break;
            case 295:
                断言(玩家.探针_最近攻击请求 == "attack-left", $"G6 朝左攻击请求 attack-left（实际 {玩家.探针_最近攻击请求}）");
                断言(玩家.探针_攻击中_只读, "G6b 攻击中（第二发）");
                玩家.探针_攻击 = false;
                break;
            case 296:
                玩家.GlobalPosition = new Vector2(560f, 129f);   // 台1 台面左缘附近（250 - 脚线 121）
                玩家.探针_水平输入 = -1f;                        // 往左走出台缘 → 测土狼
                _F步骤 = 1;
                break;
        }

        // ---- F 组步骤机：走位/高度动态判定，步骤完成即推进 ----
        if (_F步骤 > 0) switch (_F步骤)
        {
            case 1:   // 走出台缘 → 离地第 3 帧按跳（土狼窗口 0.1s = 6 帧内）
                if (!玩家.IsOnFloor())
                {
                    _F离地帧++;
                    if (_F离地帧 == 3)
                    {
                        _F1按下Y = 玩家.Position.Y;
                        _F1按帧 = _帧;
                        玩家.探针_跳 = true;
                        _F步骤 = 2;
                    }
                }
                break;
            case 2:   // 松手 → 8 帧后仍在上方 = 土狼起跳成功
                if (_帧 == _F1按帧 + 2) 玩家.探针_跳 = false;
                if (_帧 == _F1按帧 + 8)
                {
                    断言(玩家.Position.Y < _F1按下Y - 10f,
                        $"F1 土狼时间：离台缘 3 帧内仍能起跳（{_F1按下Y:0.0} → {玩家.Position.Y:0.0}）");
                    _F步骤 = 3;
                }
                break;
            case 3:   // 下落中 → 落地前按（缓冲 0.12s = 7 帧）
                if (!_F2按了 && !玩家.IsOnFloor() && 玩家.Velocity.Y > 0f && 玩家.GlobalPosition.Y > 205f)
                {
                    _F2按了 = true;
                    _F2按帧 = _帧;
                    玩家.探针_跳 = true;
                    _F步骤 = 4;
                }
                break;
            case 4:
                if (_帧 == _F2按帧 + 2) 玩家.探针_跳 = false;
                if (_帧 == _F2按帧 + 8)
                {
                    断言(!玩家.IsOnFloor() && 玩家.GlobalPosition.Y < 230f,
                        $"F2 跳跃缓冲：落地前按 → 落地自动起跳（Y={玩家.GlobalPosition.Y:0}）");
                    _F基准 = _帧;
                    _F步骤 = 5;
                }
                break;
            case 5:   // 站稳 → 轻点（可变跳高：短按 = 小跳）
                if (_帧 == _F基准 + 34)
                {
                    玩家.GlobalPosition = new Vector2(0f, 239f);
                    玩家.探针_水平输入 = 0f;
                }
                if (_帧 == _F基准 + 40) { _F3按下Y = 239f; _F3最低Y = 239f; 玩家.探针_跳 = true; }
                if (_帧 == _F基准 + 42) 玩家.探针_跳 = false;
                if (_帧 > _F基准 + 40) _F3最低Y = Mathf.Min(_F3最低Y, 玩家.Position.Y);
                if (_帧 > _F基准 + 50 && 玩家.IsOnFloor())
                {
                    var 升 = _F3按下Y - _F3最低Y;
                    断言(升 is > 30f and < 90f, $"F3 可变跳高：轻点=小跳（升 {升:0}px，满跳 ≈128）");
                    _F基准 = _帧;
                    _F步骤 = 6;
                }
                break;
            case 6:   // 收尾：退出（此后 D/H/E 组按相对帧推进）
                if (_帧 == _F基准 + 12)
                {
                    _退出前X = 玩家.Position.X;
                    GameHost.请求退出();
                    _退出帧 = _帧;
                    _F步骤 = 7;
                }
                break;
        }

        // ---- 退出之后：D / H / E 组按相对帧推进（间距沿用原设计：+8 / +10~14 / +20…）----
        if (_退出帧 > 0) switch (_帧 - _退出帧)
        {
            case 8:   // D 组：窗口/精灵/状态机还原 + 检查点入档
            {
                断言(ModeManager.CurrentMode == ModeManager.Mode.Office, "D1 已切回办公模式");
                断言(!GameHost.挂载中, "D2 世界已卸载");
                断言(DisplayServer.WindowGetSize() == _原尺寸, $"D3 窗口尺寸原样还原（{DisplayServer.WindowGetSize()} vs {_原尺寸}）");
                断言(DisplayServer.WindowGetPosition() == _原位置, $"D4 窗口位置原样还原（{DisplayServer.WindowGetPosition()} vs {_原位置}）");
                var 根 = GetChild(0);
                var 精灵 = 根.GetNodeOrNull<Node2D>("AnimatedSprite2D");
                断言(精灵?.GetParent() == 根, "D5 精灵已放回主场景");
                var 期望 = new Vector2(PetWindow.S / 2f, PetWindow.S / 2f);
                断言(精灵 != null && 精灵.Position.DistanceTo(期望) < 0.5f,
                    $"D6 精灵回办公居中（{精灵?.Position} vs {期望}）");
                断言(StateMachine.CurrentState == StateMachine.Idle, $"D7 状态机回 idle（实际 {StateMachine.CurrentState}）");
                try
                {
                    using var 文档 = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(GameSession.探针_存盘路径));
                    var cx = 文档.RootElement.GetProperty("checkpointX").GetSingle();
                    断言(System.Math.Abs(cx - _退出前X) < 0.01f, $"D8 退出时检查点已写进存档（{cx:0.00} vs {_退出前X:0.00}）");
                }
                catch (System.Exception e) { 断言(false, $"D8 读临时存档失败: {e.Message}"); }
                断言(!GetWindow().GetFlag(Window.Flags.MousePassthrough), "D9 退出后鼠标交还桌面（穿透关闭）");
                break;
            }

            // H 组「办公即游戏」钩子：干完一次活 → 办公星 +1（办公 idle 时做，避开入场动画）
            case 10:
                StateMachine.开始干活();
                break;
            case 12:
                断言(GameSession.办公星 == 0, $"H1 开工不记账（办公星 {GameSession.办公星}）");
                StateMachine.结束干活();
                break;
            case 14:
                断言(GameSession.办公星 == 1, $"H2 干完一次活 → 办公星 +1（实际 {GameSession.办公星}）");
                break;

            // E 组：二进宫（玩法切片一）收集 / 扣血 / 玩累了
            case 20:
                GameEntryDialog.请求进入();
                break;
            case 28:
                敲键(Key.Space, true);
                break;
            case 32:
                敲键(Key.Space, false);
                break;
            case 36:
                断言(GameHost.挂载中, "E1 再次进入游戏（二进宫）");
                断言(GameHost.单例.探针_状态文本.Contains("办公星 ×1"), $"E2 状态行带办公星计数（实际「{GameHost.单例.探针_状态文本}」）");
                断言(GameHost.单例.探针_状态文本.Contains("⭐ 0/3"), $"E3 初始收集 0/3（实际「{GameHost.单例.探针_状态文本}」）");
                {   // E0：三颗星都必须摆在可站面上方 50px（够得到）—— 防「星摆进地面」这类表/关卡不一致
                    var 空间 = GameHost.单例.探针_世界.GetWorld2D().DirectSpaceState;
                    foreach (var (id, 位) in GameHost.探针_星星表)
                    {
                        var 查 = PhysicsRayQueryParameters2D.Create(位, 位 + new Vector2(0, 500f));
                        var 果 = 空间.IntersectRay(查);
                        var 落Y = 果.Count > 0 ? 果["position"].AsVector2().Y : float.NaN;
                        断言(果.Count > 0 && Mathf.Abs(落Y - (位.Y + 50f)) < 8f,
                            $"E0 {id} 摆在可站面上方 50px（落面 y={落Y:0} vs 期望 {位.Y + 50f:0}）");
                    }
                }
                GameSession.设血量(50);
                GameHost.单例.刷新状态UI();
                玩家.GlobalPosition = new Vector2(670f, 129f);   // 站上台1 台面（star-1 正下方）
                break;
            case 42:
                断言(GameSession.有道具("star-1"), "E4 站上台1 → 收集到 star-1");
                断言(GameSession.血量 == 65, $"E5 收集回血 +15（50 → {GameSession.血量}）");
                断言(GameHost.单例.探针_星星.Count == 2, $"E6 星点从世界移除（剩 {GameHost.单例.探针_星星.Count}）");
                断言(GameHost.单例.探针_状态文本.Contains("⭐ 1/3"), $"E7 状态行更新（实际「{GameHost.单例.探针_状态文本}」）");
                break;
            case 50:
                玩家.GlobalPosition = new Vector2(0f, 950f);   // 掉出世界 → 扣血重生
                break;
            case 56:
                断言(GameSession.血量 == 45, $"E8 掉落扣 20 血（65 → {GameSession.血量}）");
                断言(玩家.GlobalPosition.Y < 500f, $"E9 已回出生点（Y={玩家.GlobalPosition.Y:0}）");
                break;
            case 64:
                GameSession.设血量(10);
                玩家.GlobalPosition = new Vector2(0f, 950f);   // 血见底 → 玩累了
                break;
            case 70:
                断言(GameHost.单例.探针_累了, "E10 血空 → 触发「玩累了」");
                断言(GameSession.血量 == 100, $"E11 玩累了回满血（实际 {GameSession.血量}）");
                断言(GameHost.单例.探针_状态文本.Contains("❤ 100"), $"E12 状态行刷新（实际「{GameHost.单例.探针_状态文本}」）");
                break;
            case 180:
                断言(ModeManager.CurrentMode == ModeManager.Mode.Game, "E13 玩累了 → **不退出游戏**（仍在游戏模式）");
                断言(GameHost.挂载中, "E14 世界仍在（死亡只回血、不卸载）");
                断言(玩家.GlobalPosition.Y < 500f, $"E15 死亡后回到出生点（Y={玩家.GlobalPosition.Y:0}）");
                break;
            case 184:
                敲键(Key.Escape, true);    // 纯键盘退出：Esc
                break;
            case 186:
                敲键(Key.Escape, false);
                break;
            case 196:
                断言(ModeManager.CurrentMode == ModeManager.Mode.Office, "E16 Esc → 退出游戏");
                断言(!GameHost.挂载中, "E17 世界已卸载");
                断言(DisplayServer.WindowGetSize() == _原尺寸, $"E18 窗口还原（{DisplayServer.WindowGetSize()} vs {_原尺寸}）");
                break;

            case 206:
                try { System.IO.File.Delete(GameSession.探针_存盘路径); } catch { /* 忽略 */ }
                GameSession.探针_覆盖存盘路径 = null;
                try { System.IO.Directory.Delete(ConfigEdit.探针_根目录, true); } catch { /* 忽略 */ }
                ConfigEdit.探针_根目录 = "";
                GD.Print($"[GP] ===== 失败数 = {_失败} =====");
                GD.Print(_失败 == 0 ? "[GP] PASS" : "[GP] FAIL");
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                break;
        }

        if (_F步骤 > 0 && _F步骤 < 7 && _帧 > 900)
        {
            GD.PrintErr($"[GP] F 组超时未完成（步骤 {_F步骤}，帧 {_帧}）");
            GetTree().Quit(3);
        }
        if (_帧 is > 130 and < 250 && 玩家 != null) _最高Y = Mathf.Min(_最高Y, 玩家.Position.Y);
        if (_帧 > 60 * 60)
        {
            GD.PrintErr("[GP] 超时 FAIL");
            GetTree().Quit(2);
        }
    }
}
