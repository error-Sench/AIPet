using desktop.script.Game;
using desktop.script.Mode;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 游戏模式探针（**非 headless**：要真实窗口几何、真实物理与渲染截图）：
/// ① 确认弹窗：Esc 取消（留在办公）/ 空格确认（进入游戏；主人指定空格确认）；
/// ② 挂载：窗口铺满屏幕、世界/玩家/相机就位、桌宠精灵 Reparent 进物理体、办公面板收起；
/// ③ 最小可玩：着地 / WSAD 位移（含走动画）/ 空格跳跃（起跳-落地）/ 相机跟随；
/// ④ 退出：**窗口几何原样还原**、精灵回主场景居中、世界清理、回到 idle、检查点已写进存档；
/// ⑤ 截两帧 PNG（站立 / 空中）供视觉复核。
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

    public override void _Ready()
    {
        // 隔离：游戏存档走临时档
        GameSession.探针_覆盖存盘路径 = "user://probe_game_mount_tmp.json";
        try { System.IO.File.Delete(GameSession.探针_存盘路径); } catch { /* 上轮残留 */ }

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
                断言(玩家.Position.X - _走前X > 60f, $"C3 D 键方向行走（{_走前X:0.0} → {玩家.Position.X:0.0}）");
                断言(CharAnim.当前动画名_只读 == "walk-right", $"C4 行走动画 = walk-right（实际 {CharAnim.当前动画名_只读}）");
                break;
            case 100:
                _跳前Y = 玩家.Position.Y;
                _最高Y = _跳前Y;
                玩家.探针_跳 = true;
                break;
            case 104:
                玩家.探针_跳 = false;
                break;
            case 130:
                _最高Y = Mathf.Min(_最高Y, 玩家.Position.Y);
                断言(!玩家.IsOnFloor(), "C5 上升段：已离地");
                断言(玩家.探针_影子?.Visible == true, "C5b 空中时影子仍可见（落点提示）");
                断言(玩家.探针_影子 != null && Mathf.Abs(玩家.探针_影子.GlobalPosition.Y - 360f) < 8f,
                    $"C5c 影子落在地面高度（{玩家.探针_影子?.GlobalPosition.Y:0.0} ≈ 360）");
                break;
            case 100 + 90:
                _最高Y = Mathf.Min(_最高Y, 玩家.Position.Y);
                断言(玩家.IsOnFloor(), "C6 已落回地面");
                断言(Mathf.Abs(玩家.Position.Y - _跳前Y) < 8f, $"C7 落回原高度（{_跳前Y:0.0} → {玩家.Position.Y:0.0}）");
                断言(_最高Y < _跳前Y - 80f, $"C8 真的跳起来了（最高 {_最高Y:0.0}，起跳前 {_跳前Y:0.0}）");
                断言(玩家.Position.DistanceTo(GameHost.单例.探针_相机.GlobalPosition) < 80f,
                    "C9 相机跟随（与玩家距离 < 80px）");
                截图("game_stand.png");
                break;
            case 210:
                // 空中截图：再跳一次，半程截
                玩家.探针_跳 = true;
                break;
            case 214:
                玩家.探针_跳 = false;
                break;
            case 232:
                截图("game_jump.png");
                break;

            // ===== ④ 退出：窗口还原 / 精灵回位 / 存档检查点 =====
            case 250:
                _退出前X = 玩家.Position.X;
                GameHost.请求退出();
                break;
            case 258:
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
                break;
            }

            case 270:
            {
                try { System.IO.File.Delete(GameSession.探针_存盘路径); } catch { /* 忽略 */ }
                GameSession.探针_覆盖存盘路径 = null;
                GD.Print($"[GP] ===== 失败数 = {_失败} =====");
                GD.Print(_失败 == 0 ? "[GP] PASS" : "[GP] FAIL");
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                break;
            }
        }

        if (_帧 is > 130 and < 250 && 玩家 != null) _最高Y = Mathf.Min(_最高Y, 玩家.Position.Y);
        if (_帧 > 60 * 60)
        {
            GD.PrintErr("[GP] 超时 FAIL");
            GetTree().Quit(2);
        }
    }
}
