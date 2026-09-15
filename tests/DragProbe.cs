using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 上边缘拖拽实证探针（**必须非 headless**：要真实窗口几何与光标）：
/// ① 把桌宠挪到屏幕上边缘（鼠标落点必然在屏幕「上 1/3」）
/// ② 把真实光标移到桌宠身上，注入左键按下 → 断言能起手（旧逻辑会因「必须在屏幕下 2/3」而失败）
/// ③ 再把光标移过阈值 → 断言真的进入拖拽
/// 结束时恢复窗口位置与光标。
/// 用法：Godot_..._console.exe --path D:/Games/Github/AIPet res://tests/DragProbe.tscn
/// </summary>
public partial class DragProbe : Node
{
    private int _帧;
    private int _失败;
    private Vector2I _原窗口位置;
    private Vector2I _原光标位置;
    private Vector2I _宠位;
    private Vector2I _拖前窗口位;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== DragProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[DR] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[DR] FAIL  {描述}"); }
    }

    private static void 注入左键(bool 按下)
    {
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = 按下 });
    }

    public override void _Process(double delta)
    {
        _帧++;
        if (_帧 == 30)
        {
            _原窗口位置 = DisplayServer.WindowGetPosition();
            _原光标位置 = DisplayServer.MouseGetPosition();
            var 屏 = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
            var 犬 = PetWindow.S;
            _宠位 = new Vector2I(屏.Position.X + (屏.Size.X - 犬) / 2, 屏.Position.Y + 2); // 贴上边缘
            DisplayServer.WindowSetPosition(_宠位);
            var 屏高 = 屏.Size.Y;
            GD.Print($"[DR] 桌宠挪到上边缘: {_宠位}（屏幕可用区高 {屏高}）；旧规则的「下 2/3 门槛」y≥{屏高 / 3}，本用例 y={_宠位.Y + 犬 / 2} 必然不满足");
        }
        else if (_帧 == 50)
        {
            var 中心 = new Vector2I(_宠位.X + PetWindow.S / 2, _宠位.Y + PetWindow.S / 2);
            // DisplayServer.WarpMouse 用的是**窗口相对坐标**（实测：全局 960 被当成窗口内 960 → 实际落 1779 = 960+窗口X）
            DisplayServer.WarpMouse(中心 - DisplayServer.WindowGetPosition());
            GD.Print($"[DR] 光标移到桌宠中心: 期望 {中心}（实际 {DisplayServer.MouseGetPosition()}）");
            注入左键(true);
        }
        else if (_帧 == 70)
        {
            GD.Print($"[DR] 起手判定={WindowDrag.探针_最近判定通过} 准备中={WindowDrag.探针_准备中} 拖拽中={WindowDrag.探针_拖拽中}");
            断言(WindowDrag.探针_最近判定通过, "上边缘处：指针落在桌宠身上被判定为有效区");
            断言(WindowDrag.探针_准备中, "上边缘处：能起手（旧逻辑在这里会失败）");
            var 现在 = DisplayServer.MouseGetPosition();
            var 目标 = new Vector2I(现在.X + 40, 现在.Y + 10); // 越过 5px 阈值
            DisplayServer.WarpMouse(目标 - DisplayServer.WindowGetPosition());
            GD.Print($"[DR] 拖动光标: 期望 {目标}（实际 {DisplayServer.MouseGetPosition()}）");
        }
        else if (_帧 == 90)
        {
            断言(WindowDrag.探针_拖拽中, "越过阈值后真的进入拖拽状态");
            _拖前窗口位 = DisplayServer.WindowGetPosition();
            var 现在 = DisplayServer.MouseGetPosition();
            // 窗口相对坐标：要「在当前位置再挪 (+30,+20)」= (当前全局 + 增量) - 窗口位
            DisplayServer.WarpMouse(现在 - DisplayServer.WindowGetPosition() + new Vector2I(30, 20));
            GD.Print($"[DR] 拖拽中把光标再挪 (+30,+20)（期望 {现在 + new Vector2I(30, 20)}，实际 {DisplayServer.MouseGetPosition()}）");
        }
        else if (_帧 == 115)
        {
            var 拖后 = DisplayServer.WindowGetPosition();
            var 位移 = 拖后 - _拖前窗口位;
            GD.Print($"[DR] 拖拽中窗口位移 = {位移}（期望 ≈(30,20)）");
            断言(位移.X >= 25 && 位移.Y >= 15, "拖拽时窗口真的跟随光标移动");
            注入左键(false); // 松手
        }
        else if (_帧 == 135)
        {
            GD.Print($"[DR] 松手后：准备中={WindowDrag.探针_准备中} 拖拽中={WindowDrag.探针_拖拽中}");
            断言(!WindowDrag.探针_拖拽中, "松手后拖拽状态复位");
            DisplayServer.WindowSetPosition(_原窗口位置);
            DisplayServer.WarpMouse(_原光标位置);
            GD.Print($"[DR] 已恢复窗口 {_原窗口位置} 与光标 {_原光标位置}");
            GD.Print($"[DR] ===== 失败数 = {_失败} =====");
            GD.Print(_失败 == 0 ? "[DR] PASS" : "[DR] FAIL");
            GetTree().Quit(_失败 == 0 ? 0 : 1);
        }
        else if (_帧 > 900)
        {
            GD.PrintErr("[DR] 超时 FAIL");
            GetTree().Quit(2);
        }
    }
}