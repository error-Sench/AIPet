using desktop.script.State;
using desktop.script.Soul;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// PinchProbe（headless）：**捏脸**（P7，照 VPet 官方实现抄的「长按脸」）。
/// 覆盖：① 命中区纯函数（官方换算出的默认区 + 可配置）② 长按阈值（不到点不触发、脸外不成立）
/// ③ 三段流转（A 进入 → B 循环 → 松手 C → 回 idle）④ 拖拽/面板抢走时作废 ⑤ 贴边隐藏时不捏
/// ⑥ **数值不动**（官方那套体力-2/心情+1 我们不抄 —— 我们的心情定义与官方不同）。
/// </summary>
public partial class PinchProbe : Node
{
    private int _帧;
    private int _失败;
    private readonly Vector2I 窗 = new(256, 256);
    private float _原长按秒;
    private float[] _原命中区;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        _原长按秒 = FacePinch.长按秒;
        _原命中区 = (float[])FacePinch.命中区.Clone();
        GD.Print("=== PinchProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[PN] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[PN] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 10: 准备(); break;
            case 15: A组_命中区(); break;
            case 20: B组_按下(); break;
            case 25: C组_长按触发(); break;
            case 30: C组_B循环(); break;
            case 35: C组_松手(); break;
            case 40: C组_退出收尾(); break;
            case 45: D组_被抢与贴边(); break;
            case 50: 收尾(); break;
        }
        if (_帧 > 300) { GD.PrintErr("[PN] 超时"); GetTree().Quit(2); }
    }

    private void 准备()
    {
        StateMachine.入场完成();          // 解入场门（否则状态机还在 enter）
        DailyRoutine.探针_重置();         // 清掉入场顺带的「启动问候」，别让它抢状态
        FacePinch.探针_重置();
        StateMachine.SetState(StateMachine.Idle);
        GD.Print($"[PN] 默认命中区={string.Join(",", FacePinch.命中区)} 长按秒={FacePinch.长按秒}");
    }

    private void A组_命中区()
    {
        GD.Print("--- A 组：命中区（纯函数）---");
        FacePinch.命中区 = new[] { 0.315f, 0.261f, 0.427f, 0.379f };
        断言(FacePinch.脸区命中(new Vector2(95, 82), 窗), "官方换算出的脸区中心命中（窗口 256：x≈81~109, y≈67~97）");
        断言(!FacePinch.脸区命中(new Vector2(10, 10), 窗), "左上角不命中");
        断言(!FacePinch.脸区命中(new Vector2(200, 200), 窗), "右下角不命中");
        断言(!FacePinch.脸区命中(new Vector2(95, 150), 窗), "脸下方的身体不命中");

        FacePinch.命中区 = new[] { 0f, 0f, 1f, 1f };
        断言(FacePinch.脸区命中(new Vector2(200, 200), 窗), "命中区可配置（config/behavior.json 的 捏脸命中区）");
        FacePinch.命中区 = new[] { 0.315f, 0.261f, 0.427f, 0.379f };
    }

    private void B组_按下()
    {
        GD.Print("--- B 组：按下（只有脸区才算长按候选）---");
        FacePinch.探针_重置();
        FacePinch.长按秒 = 0.2f;
        断言(FacePinch.按下(new Vector2(95, 82), 窗), "脸区按下 → 命中");
        断言(FacePinch.当前相 == FacePinch.相.按住中, "相=按住中（还没到阈值）");
        断言(FacePinch.探针_捏脸次数 == 0, "还没触发");
        断言(!FacePinch.按下(new Vector2(95, 82), 窗), "候选中重复按下被忽略");

        FacePinch.探针_重置();
        断言(!FacePinch.按下(new Vector2(10, 10), 窗), "脸外按下 → 不命中（这一按归拖拽/其它）");
        断言(FacePinch.当前相 == FacePinch.相.无, "脸外按下不留状态");
    }

    private void C组_长按触发()
    {
        GD.Print("--- C 组：长按到阈值 → 捏脸 ---");
        FacePinch.探针_重置();
        FacePinch.长按秒 = 0.2f;
        StateMachine.SetState(StateMachine.Idle);
        var 前心情 = StatsTable.当前心情;
        var 前精力 = StatsTable.当前精力;

        FacePinch.按下(new Vector2(95, 82), 窗);
        FacePinch.每帧(0.1f);
        断言(FacePinch.当前相 == FacePinch.相.按住中, "0.1s（< 阈值 0.2s）还没触发");
        FacePinch.每帧(0.15f);
        断言(FacePinch.当前相 == FacePinch.相.捏脸中, "到阈值 → 捏脸中");
        断言(FacePinch.探针_捏脸次数 == 1, $"捏脸次数=1（{FacePinch.探针_捏脸次数}）");
        断言(StateMachine.CurrentState == StateMachine.PinchState, "状态机进了 pinch 状态");
        断言(FacePinch.本次已捏, "本次按压标记为「已捏」（拖拽松手时不会再补一个摸摸）");
        断言(StatsTable.当前心情 == 前心情 && StatsTable.当前精力 == 前精力,
            $"**数值不动**（官方体力-2/心情+1 我们不抄；心情 {StatsTable.当前心情}、精力 {StatsTable.当前精力}）");
    }

    private void C组_B循环()
    {
        GD.Print("--- C 组：按住时 B 段连续循环 ---");
        StateMachine.重播当前状态();     // 模拟「A 播完 / B 播完」
        断言(FacePinch.当前相 == FacePinch.相.捏脸中, "播完一段仍在捏脸中（官方 DisplayPinch_loop：播完 B 再播 B）");
        断言(CharAnim.当前动画名_只读.StartsWith("pinch-"), $"播的还是 pinch 池（{CharAnim.当前动画名_只读}）");
    }

    private void C组_松手()
    {
        GD.Print("--- C 组：松手 → 播退出段 ---");
        FacePinch.松手();
        断言(FacePinch.当前相 == FacePinch.相.退出中, "松手 → 退出中（播 C）");
    }

    private void C组_退出收尾()
    {
        GD.Print("--- C 组：退出段播完 → 回 idle ---");
        StateMachine.重播当前状态();
        断言(FacePinch.当前相 == FacePinch.相.无, "C 播完 → 相归位");
        断言(StateMachine.CurrentState == StateMachine.Idle, $"回到 idle（{StateMachine.CurrentState}）");
    }

    private void D组_被抢与贴边()
    {
        GD.Print("--- D 组：被拖拽/面板抢走 → 作废；贴边隐藏时不捏 ---");
        FacePinch.探针_重置();
        FacePinch.按下(new Vector2(95, 82), 窗);
        FacePinch.取消();                // WindowDrag：移动超 5px 时调（本次按的是拖拽）
        断言(FacePinch.当前相 == FacePinch.相.无 && !FacePinch.本次已捏, "被抢走 → 候选作废、不捏");
        FacePinch.每帧(1f);
        断言(FacePinch.探针_捏脸次数 == 0, "作废后推进时间也不会补触发");

        EdgeHide.探针_强制阶段(EdgeHide.侧.左, EdgeHide.相.隐藏);
        断言(!FacePinch.按下(new Vector2(95, 82), 窗), "贴边隐藏时不捏（半截在屏外，别抢状态）");
        EdgeHide.探针_重置();
    }

    private void 收尾()
    {
        FacePinch.长按秒 = _原长按秒;
        FacePinch.命中区 = _原命中区;
        FacePinch.探针_重置();
        StateMachine.SetState(StateMachine.Idle);
        GD.Print($"[PN] ===== 失败数 = {_失败} =====");
        GD.Print(_失败 == 0 ? "[PN] PASS" : "[PN] FAIL");
        GetTree().Quit(_失败 == 0 ? 0 : 1);
    }
}
