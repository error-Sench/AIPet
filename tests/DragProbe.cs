using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 拖拽探针（**必须非 headless**：要真实窗口几何与光标）。覆盖三件事：
/// <list type="number">
/// <item>**上边缘可拖**：把桌宠挪到屏幕上边缘，光标落在**头顶**（拖拽区）→ 能起手、越阈值真拖、窗口跟随
/// （旧逻辑会因「必须在屏幕下 2/3」而失败）。</item>
/// <item>**拖拽命中区**（主人 2026-09-19「拖拽要加判定区，只有头顶部分才能点击拖拽」）：
/// 身体处 / 脸区起的按压，移动后**不拖拽**（脸区起手永不变拖拽 = 捏脸优先）。</item>
/// <item>**捏脸优先**：长按脸触发捏脸后，移动**不变拖拽、捏脸不受影响**（主人实机报的 bug 的回归）。</item>
/// </list>
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
    private Vector2I _身体组窗口位;
    private int _捏前次数;
    private readonly Vector2I 窗 = new(256, 256);

    // 窗口内测试点（窗口相对坐标，窗口 256）：
    private static readonly Vector2I 头部点 = new(128, 46);   // 拖拽区内（顶部 18%）
    private static readonly Vector2I 身体点 = new(128, 160);  // 拖拽区外（躯干）
    private static readonly Vector2I 脸点 = new(95, 82);      // 脸区（捏脸命中区中心）

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

    /// <summary>把（全局）目标点换算成窗口相对坐标后 WarpMouse（坑 #12：WarpMouse 用窗口相对坐标）。</summary>
    private static void 移光标(Vector2I 全局目标) =>
        DisplayServer.WarpMouse(全局目标 - DisplayServer.WindowGetPosition());

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
        else if (_帧 == 45)
        {
            GD.Print("--- A 组：拖拽命中区（纯函数）---");
            断言(WindowDrag.拖拽区命中(new Vector2(128, 46), 窗), "头部点（顶部 18%）在拖拽区内（默认顶部 40%）");
            断言(!WindowDrag.拖拽区命中(new Vector2(128, 128), 窗), "窗口正中不在拖拽区（y=50% > 40%）");
            断言(!WindowDrag.拖拽区命中(new Vector2(128, 160), 窗), "身体点（躯干）不在拖拽区");
            断言(!WindowDrag.拖拽区命中(new Vector2(128, 200), 窗), "下半身不在拖拽区");
            // 2026-10-01 抓握点纯函数（抄官方 raisepoint (290,128)@500 → 窗口比例）
            var 抓 = WindowDrag.抓握点像素(窗);
            GD.Print($"[DR] 抓握点(256 窗口) = {抓}");
            断言(抓 == new Vector2I(153, 67), $"抓握点 = 官方 raisepoint 换算 (153,67)（实际 {抓}；= draghold 内容顶边，拎起点）");
            断言(WindowDrag.拖拽窗口位(new Vector2I(500, 300), 窗) == new Vector2I(500 - 抓.X, 300 - 抓.Y),
                "拖拽窗口位 = 鼠标 − 抓握点（纯函数）");
            断言(WindowDrag.抓握点像素(new Vector2I(512, 512)) == new Vector2I(306, 134), "抓握点随窗口尺寸等比（512 → (306,134)）");
        }
        else if (_帧 == 60)
        {
            GD.Print("--- B 组：头顶（拖拽区）起手 → 能拖（上边缘处）---");
            DisplayServer.WarpMouse(头部点);
            注入左键(true);
        }
        else if (_帧 == 62)
        {
            GD.Print($"[DR] 起手分类: 脸区={WindowDrag.探针_起手脸区} 拖拽区={WindowDrag.探针_起手拖拽区} 准备中={WindowDrag.探针_准备中}");
            断言(WindowDrag.探针_最近判定通过, "上边缘处：指针落在桌宠身上被判定为有效区");
            断言(WindowDrag.探针_准备中 || WindowDrag.探针_拖拽中, "上边缘处：按压被接管（准备中或已开拖；旧逻辑在这里会失败）");
            断言(WindowDrag.探针_起手拖拽区 && !WindowDrag.探针_起手脸区, "头部起手 → 归类为「拖拽区起手」");
        }
        else if (_帧 is >= 64 and <= 80 && _帧 % 8 == 0)
        {
            // 起手后轨迹（诊断用）：真实光标在两次 warp 之间可能被物理挪动 → 越过 5px 会提前开拖，属正常
            GD.Print($"[DR] 起手后轨迹 t{_帧}: 鼠标={DisplayServer.MouseGetPosition()} 准备中={WindowDrag.探针_准备中} 拖拽中={WindowDrag.探针_拖拽中}");
        }
        else if (_帧 == 82)
        {
            var 现在 = DisplayServer.MouseGetPosition();
            var 目标 = new Vector2I(现在.X + 40, 现在.Y + 10); // 越过 5px 阈值
            移光标(目标);
            GD.Print($"[DR] 拖动光标: 期望 {目标}（实际 {DisplayServer.MouseGetPosition()}）");
        }
        else if (_帧 == 100)
        {
            断言(WindowDrag.探针_拖拽中, "越过阈值后真的进入拖拽状态");
            _拖前窗口位 = DisplayServer.WindowGetPosition();
            var 现在 = DisplayServer.MouseGetPosition();
            移光标(现在 + new Vector2I(30, 20));
            GD.Print($"[DR] 拖拽中把光标再挪 (+30,+20)（期望 {现在 + new Vector2I(30, 20)}，实际 {DisplayServer.MouseGetPosition()}）");
        }
        else if (_帧 == 125)
        {
            var 拖后 = DisplayServer.WindowGetPosition();
            var 位移 = 拖后 - _拖前窗口位;
            GD.Print($"[DR] 拖拽中窗口位移 = {位移}（期望 ≈(30,20)）");
            断言(位移.X >= 25 && 位移.Y >= 15, "拖拽时窗口真的跟随光标移动");
            // 2026-10-01 抓握点口径（抄官方 raisepoint）：窗口位 = 鼠标 − 抓握点（头顶恒对鼠标）
            var 鼠标 = DisplayServer.MouseGetPosition();
            var 期望位 = WindowDrag.拖拽窗口位(鼠标, DisplayServer.WindowGetSize());
            GD.Print($"[DR] 鼠标={鼠标} 窗口={拖后} 期望={期望位} 抓握点={WindowDrag.抓握点像素(DisplayServer.WindowGetSize())}");
            断言((拖后 - 期望位).Length() <= 2, "抓握点对齐：窗口位 = 鼠标−抓握点（±2px，拎起点和鼠标重合）");
            注入左键(false); // 松手
        }
        else if (_帧 == 145)
        {
            GD.Print($"[DR] 松手后：准备中={WindowDrag.探针_准备中} 拖拽中={WindowDrag.探针_拖拽中}");
            断言(!WindowDrag.探针_拖拽中, "松手后拖拽状态复位");

            GD.Print("--- C 组：身体起手 → 不拖拽 ---");
            _身体组窗口位 = DisplayServer.WindowGetPosition();
            DisplayServer.WarpMouse(身体点);
            注入左键(true);
        }
        else if (_帧 == 165)
        {
            GD.Print($"[DR] 身体起手分类: 脸区={WindowDrag.探针_起手脸区} 拖拽区={WindowDrag.探针_起手拖拽区}");
            断言(WindowDrag.探针_准备中, "身体处能起手（按压被登记）");
            断言(!WindowDrag.探针_起手拖拽区 && !WindowDrag.探针_起手脸区, "身体起手 → 既非拖拽区也非脸区");
            var 现在 = DisplayServer.MouseGetPosition();
            移光标(现在 + new Vector2I(40, 0));
        }
        else if (_帧 == 185)
        {
            断言(!WindowDrag.探针_拖拽中, "身体起手移动后**不拖拽**（拖拽区限制生效）");
            断言(DisplayServer.WindowGetPosition() == _身体组窗口位, $"窗口没有移动（{DisplayServer.WindowGetPosition()} == {_身体组窗口位}）");
            注入左键(false);
        }
        else if (_帧 == 205)
        {
            GD.Print("--- D 组：脸区起手 → 不拖拽（捏脸优先，早移动作废候选）---");
            DisplayServer.WarpMouse(脸点);
            注入左键(true);
        }
        else if (_帧 == 215)
        {
            GD.Print($"[DR] 脸区起手分类: 脸区={WindowDrag.探针_起手脸区} 拖拽区={WindowDrag.探针_起手拖拽区} 相={FacePinch.当前相}");
            断言(WindowDrag.探针_起手脸区 && !WindowDrag.探针_起手拖拽区, "脸区起手 → 归脸区（捏脸候选已登记）");
            var 现在 = DisplayServer.MouseGetPosition();
            移光标(现在 + new Vector2I(40, 0));   // 早于 300ms：作废候选
        }
        else if (_帧 == 230)
        {
            断言(!WindowDrag.探针_拖拽中, "脸区起手移动后**不拖拽**（捏脸优先）");
            断言(FacePinch.当前相 == FacePinch.相.无, $"早移动 → 捏脸候选作废（相={FacePinch.当前相}）");
            注入左键(false);
        }
        else if (_帧 == 250)
        {
            GD.Print("--- E 组：捏脸触发后移动 → 不拖拽、不作废（主人报的 bug 回归）---");
            _捏前次数 = FacePinch.探针_捏脸次数;
            DisplayServer.WarpMouse(脸点);
            注入左键(true);
        }
        else if (_帧 == 300)
        {
            GD.Print($"[DR] 长按结果: 次数 {_捏前次数}→{FacePinch.探针_捏脸次数} 相={FacePinch.当前相} 拖拽中={WindowDrag.探针_拖拽中}");
            断言(FacePinch.探针_捏脸次数 == _捏前次数 + 1 && FacePinch.当前相 == FacePinch.相.捏脸中,
                "按住不动 ≥300ms → 捏脸触发（长按确实触发捏脸）");
            var 现在 = DisplayServer.MouseGetPosition();
            移光标(现在 + new Vector2I(40, 0));   // 捏脸中移动
        }
        else if (_帧 == 330)
        {
            GD.Print($"[DR] 捏脸中移动后: 相={FacePinch.当前相} 拖拽中={WindowDrag.探针_拖拽中}");
            断言(FacePinch.当前相 == FacePinch.相.捏脸中, "捏脸期间移动 → 捏脸不受影响（官方 MainGrid_MouseMove 同款）");
            断言(!WindowDrag.探针_拖拽中, "★ 捏脸期间移动**不进入拖拽**（主人实机报的 bug 回归）");
            注入左键(false);
        }
        else if (_帧 == 350)
        {
            断言(FacePinch.当前相 == FacePinch.相.退出中, $"松手 → 播退出段（相={FacePinch.当前相}）");
            FacePinch.探针_重置();
            StateMachine.SetState(StateMachine.Idle);
            DisplayServer.WindowSetPosition(_原窗口位置);
            DisplayServer.WarpMouse(_原光标位置 - DisplayServer.WindowGetPosition());
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
