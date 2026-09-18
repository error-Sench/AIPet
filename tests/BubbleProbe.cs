using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// BubbleProbe（headless）：**气泡说话动作 + 固定时长**（P8）。
/// 覆盖：① 气泡出现 → 状态 `bubble_talk` + 播 `say` 池 ② 动作**可被打断**（交互立刻接管）
/// ③ **非 idle 不偷**（think / sleep / 贴边 / 流式 Speak / 问候 / 摸摸 —— 后两个是「先冒泡、后置状态」）
/// ④ 时长 = `气泡显示秒`（可调）→ 到点回 idle ⑤ 文本照旧落探针。
/// <para>
/// 节奏：气泡走 `CallDeferred`，`冒泡说话` 在**下一帧**才跑 —— 所以每段都拆成「请求 → 隔几帧断言」；
/// 为了少写 case，多数段落是「先断言上一段，再发下一段的请求，隔三帧再断言」。
/// </para>
/// </summary>
public partial class BubbleProbe : Node
{
    private int _帧;
    private int _失败;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== BubbleProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[BP] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[BP] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 10: 准备(); break;
            case 15: A_请求(); break;
            case 20: A_断言(); break;
            case 25: B_可被打断(); break;
            case 30: C1_忙态_请求(); break;
            case 33: C2_贴边_请求(); break;
            case 36: C3_流式_请求(); break;
            case 39: C4_问候_请求(); break;
            case 42: C5_摸摸_请求(); break;
            case 45: C6_摸摸_断言(); break;
            case 48: D_时长_请求(); break;
            case 52: D_时长_断言(); break;
            case 58: 收尾(); break;
        }
        if (_帧 > 300) { GD.PrintErr("[BP] 超时"); GetTree().Quit(2); }
    }

    private void 准备()
    {
        StateMachine.入场完成();
        DailyRoutine.探针_重置();          // 清掉入场顺带的启动问候（它会占住 Greet）
        FacePinch.探针_重置();
        StateMachine.SetState(StateMachine.Idle);
        GD.Print($"[BP] 气泡显示秒={Dialogue.气泡显示秒}（config/behavior.json 的 气泡显示秒）");
    }

    // ---- A：气泡 → 说话动作（隔一帧看状态）----
    private void A_请求()
    {
        GD.Print("--- A 组：气泡 → 说话动作 ---");
        StateMachine.SetState(StateMachine.Idle);
        Dialogue.显示临时标题("这是测试用的一句话");
        断言(Dialogue.探针_最近请求文本 == "这是测试用的一句话", "气泡文本照旧落到探针");
    }

    private void A_断言()
    {
        断言(StateMachine.CurrentState == StateMachine.BubbleTalk,
            $"气泡出现 → 状态 = bubble_talk（{StateMachine.CurrentState}）");
        断言(CharAnim.当前动画名_只读.StartsWith("say-"),
            $"播的是 say 池（{CharAnim.当前动画名_只读}）");
    }

    // ---- B：动作可被打断 ----
    private void B_可被打断()
    {
        GD.Print("--- B 组：动作可被打断 ---");
        StateMachine.SetState(StateMachine.Interact);       // 模拟主人摸了一下
        断言(StateMachine.CurrentState == StateMachine.Interact,
            $"交互立刻接管（{StateMachine.CurrentState}）—— 说话动作不锁定");
    }

    // ---- C：非 idle 不偷（每段：断言上一段 + 发下一段请求）----
    private void C1_忙态_请求()
    {
        GD.Print("--- C 组：非 idle / 忙态不偷说话动作 ---");
        StateMachine.SetState(StateMachine.Think);
        Dialogue.显示临时标题("忙着呢，别切我动作");
    }

    private void C2_贴边_请求()
    {
        断言(StateMachine.CurrentState == StateMachine.Think, $"think 中不被偷（{StateMachine.CurrentState}）");
        StateMachine.SetState(StateMachine.Sleep);
        Dialogue.显示临时标题("睡着了也别切");                 // 隔三帧后在这里连断言
    }

    private void C3_流式_请求()
    {
        断言(StateMachine.CurrentState == StateMachine.Sleep, $"sleep 中不被偷（{StateMachine.CurrentState}）");
        StateMachine.SetState(StateMachine.EdgeHideState);   // 贴边隐藏（状态机 + 相机位都按真实路径摆）
        EdgeHide.探针_强制阶段(EdgeHide.侧.左, EdgeHide.相.隐藏);
        Dialogue.显示临时标题("贴边的时候别切");
    }

    private void C4_问候_请求()
    {
        断言(StateMachine.CurrentState != StateMachine.BubbleTalk, $"贴边隐藏中不被偷（{StateMachine.CurrentState}）");
        EdgeHide.探针_重置();
        StateMachine.SetState(StateMachine.Speak);           // Agent 流式回复
        Dialogue.显示临时标题("流式回复中别切");
    }

    private void C5_摸摸_请求()
    {
        断言(StateMachine.CurrentState == StateMachine.Speak, $"Speak 中不被偷（{StateMachine.CurrentState}）");
        StateMachine.SetState(StateMachine.Greet);           // 问候：**先冒泡、后置状态**（真实顺序）
        Dialogue.显示临时标题("打完招呼再说");
    }

    private void C6_摸摸_断言()
    {
        断言(StateMachine.CurrentState == StateMachine.Greet, $"问候姿态不被偷（{StateMachine.CurrentState}）");
        StateMachine.SetState(StateMachine.Interact);        // 摸摸：同样先冒泡、后置状态
        Dialogue.显示临时标题("摸完再说");
    }

    // ---- D：时长 = 气泡显示秒（可调）→ 到点回 idle ----
    private void D_时长_请求()
    {
        GD.Print("--- D 组：时长 = 气泡显示秒（可调）→ 到点回 idle ---");
        断言(StateMachine.CurrentState != StateMachine.BubbleTalk, $"摸摸反应也没被偷（{StateMachine.CurrentState}）");
        StateMachine.SetState(StateMachine.Idle);
        Dialogue.探针_设气泡秒(0.4f);                        // 缩短到 0.4s 便于探针
        Dialogue.显示临时标题("时长测试");
    }

    private void D_时长_断言()
    {
        断言(StateMachine.CurrentState == StateMachine.BubbleTalk, $"切进说话动作（{StateMachine.CurrentState}）");
        推进(0.2f);
        断言(StateMachine.CurrentState == StateMachine.BubbleTalk, "0.2s（< 0.4s）还在说");
        推进(0.3f);
        断言(StateMachine.CurrentState == StateMachine.Idle,
            $"到点回 idle（{StateMachine.CurrentState}）—— 时长与动画无关");
        Dialogue.探针_设气泡秒(4f);
    }

    /// <summary>推进状态机时间（直接喂 _Process 太慢：探针手动喂 delta）。</summary>
    private void 推进(float 秒) => StateMachine.探针_推进时间(秒);

    private void 收尾()
    {
        Dialogue.探针_设气泡秒(4f);
        StateMachine.SetState(StateMachine.Idle);
        GD.Print($"[BP] ===== 失败数 = {_失败} =====");
        GD.Print(_失败 == 0 ? "[BP] PASS" : "[BP] FAIL");
        GetTree().Quit(_失败 == 0 ? 0 : 1);
    }
}
