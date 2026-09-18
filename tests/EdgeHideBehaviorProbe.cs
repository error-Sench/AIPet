using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 贴边隐藏**行为层**探针（**非 headless**：要真实窗口几何与光标）：
/// ① 纯函数（贴边判定 / 隐藏位置 / 可见条命中）② 端到端（缩进→静止→悬停探出→缩回→复位）
/// ③ 让位防护（别人抢状态时窗口必须回原位，不能把宠留在屏外）④ 开关关得住。
/// 用法：Godot_..._console.exe --path D:/Games/Github/AIPet res://tests/EdgeHideBehaviorProbe.tscn
/// </summary>
public partial class EdgeHideBehaviorProbe : Node
{
    private int _帧;
    private int _失败;
    private Vector2I _原窗口位;
    private Vector2I _原光标位;
    private float _原缩回延迟;
    private bool _原启用;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        _原窗口位 = DisplayServer.WindowGetPosition();
        _原光标位 = DisplayServer.MouseGetPosition();
        _原缩回延迟 = EdgeHide.缩回延迟秒;
        _原启用 = EdgeHide.启用;
        EdgeHide.缩回延迟秒 = 0.2f; // 探针里别等 1 秒
        GD.Print("=== EdgeHideBehaviorProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[EB] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[EB] FAIL  {描述}"); }
    }

    private static Rect2I 屏 => DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
    private static Vector2I 宠尺 => DisplayServer.WindowGetSize();
    private static int 宠X => DisplayServer.WindowGetPosition().X;

    /// <summary>把桌宠窗口的内容存成 PNG（供视觉复核：隐藏/探出时露了多少）。</summary>
    private void 存窗口图(string 文件名)
    {
        var 图 = GetWindow()?.GetTexture()?.GetImage();
        if (图 == null) { GD.PrintErr($"[EB] 截图失败：{文件名}"); return; }
        var 路径 = ProjectSettings.GlobalizePath($"user://{文件名}");
        图.SavePng(路径);
        GD.Print($"[EB] 截图: {路径} ({图.GetWidth()}x{图.GetHeight()})");
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 10: 纯函数组(); break;
            case 20:
                DisplayServer.WindowSetPosition(new Vector2I(屏.Position.X - 30, 400)); // 贴左边缘
                GD.Print($"[EB] 把宠挪到左边缘: X={宠X}（屏 {屏.Position.X}）");
                EdgeHide.检查贴边();
                GD.Print($"[EB] 检查贴边 → 相={EdgeHide.探针_相} 目标X={EdgeHide.探针_目标位置.X}");
                断言(EdgeHide.探针_相 != EdgeHide.相.无, "贴边被识别（进入缩进流程）");
                断言(StateMachine.CurrentState == StateMachine.EdgeHideState, "状态机进了 edge_hide 状态");
                break;
            case 95:
                GD.Print($"[EB] 缩进后: X={宠X} 目标={EdgeHide.探针_目标位置.X} 相={EdgeHide.探针_相}");
                断言(宠X <= EdgeHide.探针_目标位置.X + 2, $"窗口真的滑到屏外（X={宠X} ≤ {EdgeHide.探针_目标位置.X}）");
                断言(宠X < 屏.Position.X, $"大部分移出屏外（X={宠X} < 屏左 {屏.Position.X}）");
                StateMachine.重播当前状态(); // 模拟「缩进动画播完」
                break;
            case 100:
                GD.Print($"[EB] 推进后 相={EdgeHide.探针_相} 动画={CharAnim.当前动画名_只读}");
                断言(EdgeHide.探针_相 == EdgeHide.相.隐藏, "缩进动画播完 → 进入「隐藏」静止相");
                存窗口图("edgehide_hidden.png");
                // ===== 回归 ①：隐藏态不该被「持续态兜底」踢回 idle（实测 bug：自动变待机并挪回屏内）=====
                StateMachine.探针_推进时间(130f);   // 模拟经过 130 秒（兜底默认 120s）
                GD.Print($"[EB] 模拟 130s 后: 状态={StateMachine.CurrentState} 相={EdgeHide.探针_相} X={宠X}");
                断言(StateMachine.CurrentState == StateMachine.EdgeHideState && EdgeHide.探针_相 != EdgeHide.相.无,
                    "回归①：贴边 130 秒后仍保持隐藏（免兜底生效，不会自己变待机挪回屏内）");
                断言(宠X < 屏.Position.X, $"回归①：窗口仍在屏外（X={宠X} < {屏.Position.X}）");                break;
            case 130:
                GD.Print($"[EB] 隐藏后 0.5s: 循环播次数={EdgeHide.探针_循环播次数} 剩余={EdgeHide.探针_重播计时:0.00}s 动画={CharAnim.当前动画名_只读}");
                断言(CharAnim.当前动画名_只读.EndsWith("-keep"), "隐藏保持用的是 -keep 循环动画（不是静止单帧）");
                break;
            case 145:
                // 节拍：「每隔两秒眨两次眼」= 周期 2s 内循环 2 次（内间隔 0.5s）→ 0.75s 时应该已经播了 2 次
                GD.Print($"[EB] 隐藏后 0.75s: 循环播次数={EdgeHide.探针_循环播次数} 剩余={EdgeHide.探针_重播计时:0.00}s");
                断言(EdgeHide.探针_循环播次数 == 2, $"**每两秒循环两次贴边动画**：0.75s 时已循环 2 次（已播 {EdgeHide.探针_循环播次数} 次）");
                break;
            case 270:
                GD.Print($"[EB] 隐藏后 2.8s: 循环播次数={EdgeHide.探针_循环播次数}");
                断言(EdgeHide.探针_循环播次数 == 4, $"2.8s 时应播满两轮 = 4 次（已播 {EdgeHide.探针_循环播次数} 次）");
                break;

            case 280:
// 把光标移到「屏内可见条」上（等节拍断言做完再悬停）
                var 位 = DisplayServer.WindowGetPosition();
                var 可见条中心 = new Vector2I(屏.Position.X + 10, 位.Y + 宠尺.Y / 2);
                DisplayServer.WarpMouse(可见条中心 - 位); // WarpMouse 是窗口相对坐标（见坑 #12）
                GD.Print($"[EB] 光标移到可见条: 期望 {可见条中心} 实际 {DisplayServer.MouseGetPosition()}");
                break;
            case 340:
                GD.Print($"[EB] 悬停后 相={EdgeHide.探针_相} 目标X={EdgeHide.探针_目标位置.X} 动画={CharAnim.当前动画名_只读}");
                断言(EdgeHide.探针_相 is EdgeHide.相.探出中 or EdgeHide.相.已探出, "鼠标靠到可见条 → 探出");
                断言(EdgeHide.探针_目标位置.X > 屏.Position.X - 宠尺.X * 0.5f, "探出时窗口往屏内回收");
                if (EdgeHide.探针_相 == EdgeHide.相.探出中)
                    断言(CharAnim.当前动画名_只读.EndsWith("-peek"), $"探出的进入段用 -peek（Rise/A）：{CharAnim.当前动画名_只读}");
                StateMachine.重播当前状态(); // 模拟「探出动画播完」
                break;
            case 350:
                断言(EdgeHide.探针_相 == EdgeHide.相.已探出, $"探出动画播完 → 停在「已探出」（相={EdgeHide.探针_相}）");
                断言(CharAnim.当前动画名_只读.EndsWith("-rise"), $"**探出后的活状态用 -rise 微动循环**（Rise/B，不是重复弹出的 -peek）：{CharAnim.当前动画名_只读}");
                存窗口图("edgehide_peek.png");
                // 光标移走（挪到屏中间偏上，远离可见条）
                DisplayServer.WarpMouse(new Vector2I(300, 200) - DisplayServer.WindowGetPosition());
                break;
            case 430:
                GD.Print($"[EB] 移开后 相={EdgeHide.探针_相} 动画={CharAnim.当前动画名_只读}");
                断言(EdgeHide.探针_相 is EdgeHide.相.缩回中 or EdgeHide.相.隐藏, "鼠标移开 → 缩回（延迟后）");
                EdgeHide.复位("探针");
                break;
            case 440:
                断言(EdgeHide.探针_相 == EdgeHide.相.退出中, "复位 → 进入退出相");
                break;
            case 520:
                GD.Print($"[EB] 复位后: X={宠X} 原位={EdgeHide.探针_原位.X} 相={EdgeHide.探针_相}");
                断言(宠X == System.Math.Max(屏.Position.X, EdgeHide.探针_原位.X), $"窗口滑回原位并夹进屏幕（{宠X} == max(屏左 {屏.Position.X}, 原位 {EdgeHide.探针_原位.X})）");
                GD.Print("--- 让位防护：别人抢状态时窗口必须回原位 ---");
                DisplayServer.WindowSetPosition(new Vector2I(屏.Position.X - 30, 400));
                EdgeHide.检查贴边();
                break;
            case 600:
                断言(EdgeHide.探针_相 != EdgeHide.相.无 && 宠X < 屏.Position.X, $"先进入隐藏（X={宠X}）");
                StateMachine.SetState(StateMachine.Idle); // 别人抢状态
                break;
            case 605:
                GD.Print($"[EB] 抢状态后: X={宠X} 相={EdgeHide.探针_相}");
                断言(EdgeHide.探针_相 == EdgeHide.相.无, "让位后阶段清空");
                断言(宠X >= 屏.Position.X, $"**窗口被拉回屏内**（X={宠X} ≥ {屏.Position.X}）—— 不会烂在屏外");
                GD.Print("--- 回归②：拖到屏幕中间松手，不该被拽回原位（实测 bug：持续吸附回边缘）---");
                EdgeHide.探针_重置();
                DisplayServer.WindowSetPosition(new Vector2I(屏.Position.X - 30, 400));
                EdgeHide.探针_强制阶段(EdgeHide.侧.左, EdgeHide.相.隐藏);   // _原位 = 边缘位置
                DisplayServer.WindowSetPosition(new Vector2I(900, 400));    // 主人把它拖到屏幕中间
                EdgeHide.探针_设相(EdgeHide.相.退出中);                      // 复位已完成 / 主人已接管
                StateMachine.SetState(StateMachine.Idle);                   // 松手会走这条（取消拖拽 → 标记状态）
                break;
            case 608:
                // 回归③：贴边时「点击不拖动」→ 窗口被拉回屏内后 **绝不能再被判定贴边**（否则又播一遍缩进动画）
                GD.Print($"[EB] 点击回屏后: X={宠X} 相={EdgeHide.探针_相}（屏左 {屏.Position.X}）");
                断言(宠X >= 屏.Position.X, $"窗口已回到屏内（X={宠X} ≥ {屏.Position.X}）");
                EdgeHide.检查贴边();   // 模拟松手路径：若手抖被当成拖动，拖拽结束就会调它
                GD.Print($"[EB] 松手复查: 相={EdgeHide.探针_相}");
                断言(EdgeHide.探针_相 == EdgeHide.相.无, "回归③：点击回屏后不会被立刻重新吸附（不再重播缩进动画）");
                break;

            case 610:
                GD.Print($"[EB] 松手后: X={宠X}（原位 X={EdgeHide.探针_原位.X}）相={EdgeHide.探针_相}");
                断言(宠X == 900, $"回归②：窗口留在屏幕中间（X={宠X} == 900）—— 没被拽回原位 {EdgeHide.探针_原位.X}、没吸附回边缘");
                GD.Print("--- 开关：关掉后贴边不生效 ---");
                EdgeHide.启用 = false;
                DisplayServer.WindowSetPosition(new Vector2I(屏.Position.X - 30, 400));
                EdgeHide.检查贴边();
                break;
            case 615:
                GD.Print($"[EB] 关掉开关后: X={宠X} 相={EdgeHide.探针_相}");
                断言(EdgeHide.探针_相 == EdgeHide.相.无, "启用=false 时贴边检查不生效（开关关得住）");

                // ===== 右缘：与左缘对称走一遍（主人报的「右边贴边动画有问题」就是这段）=====
                GD.Print("--- 右缘：与左缘对称走一遍 ---");
                EdgeHide.启用 = true;
                EdgeHide.探针_重置();
                DisplayServer.WindowSetPosition(new Vector2I(屏.End.X - 宠尺.X + 30, 400));
                GD.Print($"[EB] 把宠挪到右边缘: X={宠X}（屏右 {屏.End.X}）");
                EdgeHide.检查贴边();
                GD.Print($"[EB] 右缘检查 → 侧={EdgeHide.当前侧} 相={EdgeHide.探针_相} 目标X={EdgeHide.探针_目标位置.X}");
                断言(EdgeHide.探针_相 != EdgeHide.相.无, "右缘被识别（进入缩进流程）");
                断言(EdgeHide.当前侧 == EdgeHide.侧.右, "识别为右侧");
                break;
            case 690:
                GD.Print($"[EB] 右缘缩进后: X={宠X} 右缘={宠X + 宠尺.X} 目标={EdgeHide.探针_目标位置.X}");
                断言(宠X + 宠尺.X >= 屏.End.X + 30, $"窗口真的滑到右缘外（右缘 {宠X + 宠尺.X} ≥ {屏.End.X + 30}）");
                StateMachine.重播当前状态();   // 模拟「缩进动画播完」
                break;
            case 700:
                GD.Print($"[EB] 右缘隐藏: 相={EdgeHide.探针_相} 动画={CharAnim.当前动画名_只读}");
                断言(EdgeHide.探针_相 == EdgeHide.相.隐藏, "右缘缩进播完 → 隐藏");
                断言(CharAnim.当前动画名_只读.EndsWith("-keep"), $"右缘隐藏用 -keep 循环：{CharAnim.当前动画名_只读}");
                存窗口图("edgehide_right_hidden.png");
                var 位右 = DisplayServer.WindowGetPosition();
                DisplayServer.WarpMouse(new Vector2I(屏.End.X - 8, 位右.Y + 宠尺.Y / 2) - 位右);
                break;
            case 760:
                GD.Print($"[EB] 右缘悬停后: 相={EdgeHide.探针_相} 动画={CharAnim.当前动画名_只读}");
                断言(EdgeHide.探针_相 is EdgeHide.相.探出中 or EdgeHide.相.已探出, "右缘鼠标靠上可见条 → 探出");
                StateMachine.重播当前状态();
                break;
            case 780:
                GD.Print($"[EB] 右缘探出: 相={EdgeHide.探针_相} 动画={CharAnim.当前动画名_只读}");
                断言(EdgeHide.探针_相 == EdgeHide.相.已探出, "右缘探出 → 已探出");
                断言(CharAnim.当前动画名_只读.EndsWith("-rise"), $"右缘探出后循环用 -rise：{CharAnim.当前动画名_只读}");
                存窗口图("edgehide_right_peek.png");
                EdgeHide.复位("探针·右缘");
                break;
            case 860:
                GD.Print($"[EB] 右缘复位后: X={宠X} 相={EdgeHide.探针_相}");
                断言(EdgeHide.探针_相 == EdgeHide.相.无, "右缘复位完成（滑回原位）");

                // 收尾：恢复
                EdgeHide.探针_重置();
                EdgeHide.启用 = _原启用;
                EdgeHide.缩回延迟秒 = _原缩回延迟;
                DisplayServer.WindowSetPosition(_原窗口位);
                DisplayServer.WarpMouse(_原光标位 - DisplayServer.WindowGetPosition());
                StateMachine.SetState(StateMachine.Idle);
                GD.Print($"[EB] 已恢复窗口 {_原窗口位} / 光标 {_原光标位}");
                GD.Print($"[EB] ===== 失败数 = {_失败} =====");
                GD.Print(_失败 == 0 ? "[EB] PASS" : "[EB] FAIL");
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                break;
        }
        if (_帧 > 1400) { GD.PrintErr("[EB] 超时"); GetTree().Quit(2); }
    }

    private void 纯函数组()
    {
        GD.Print("--- 纯函数 ---");
        var 屏测 = new Rect2I(0, 0, 1920, 1040);
        const int 宽 = 282;

        断言(EdgeHide.判断贴边侧(-25, 屏测, 20, 宽) == EdgeHide.侧.左, "窗口推出屏外 25px → 贴左边缘");
        断言(EdgeHide.判断贴边侧(1920 + 25 - 宽, 屏测, 20, 宽) == EdgeHide.侧.右, "窗口推出右缘 25px → 贴右边缘");
        断言(EdgeHide.判断贴边侧(800, 屏测, 20, 宽) == null, "屏中间 → 不贴边");
        断言(EdgeHide.判断贴边侧(5, 屏测, 20, 宽) == null, "屏内地贴边 5px → **不再吸附**（官方判据：必须推出屏外）");

        var 左隐 = EdgeHide.隐藏位置X(EdgeHide.侧.左, 屏测, 宽, 0.30f);
        var 右隐 = EdgeHide.隐藏位置X(EdgeHide.侧.右, 屏测, 宽, 0.30f);
        断言(左隐 == -(宽 - (int)(宽 * 0.30f)), $"左隐藏位置 X={左隐}（屏外 + 只留 30%）");
        断言(右隐 == 1920 - (int)(宽 * 0.30f), $"右隐藏位置 X={右隐}");

        var 窗口位 = new Vector2I(左隐, 400);
        var 尺 = new Vector2I(宽, 282);
        断言(EdgeHide.鼠标在可见条(new Vector2I(10, 500), 窗口位, 尺, 屏测), "隐藏时：屏内那一条算命中");
        断言(!EdgeHide.鼠标在可见条(new Vector2I(500, 500), 窗口位, 尺, 屏测), "隐藏时：屏内其余位置不算命中");
    }
}