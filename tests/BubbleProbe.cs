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
            case 56: E_气泡窗_短文本(); break;
            case 60: E_气泡窗_长文本(); break;
            case 64: E_气泡窗_转义(); break;
            case 68: E_气泡窗_常驻与自动收(); break;
            case 74: E_气泡窗_收尾断言(); break;
            case 80: 收尾(); break;
        }
        if (_帧 > 300) { GD.PrintErr("[BP] 超时"); GetTree().Quit(2); }
    }

    private void 准备()
    {
        StateMachine.入场完成();
        DailyRoutine.探针_重置();          // 清掉入场顺带的启动问候（它会占住 Greet）
        FacePinch.探针_重置();
        StateMachine.SetState(StateMachine.Idle);
        GD.Print($"[BP] 气泡显示秒={Dialogue.气泡显示秒}（config/bubble.json 的 气泡显示秒）");
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

    // ---- E：气泡窗（独立窗 / 自适应大小 / 字号 / 鼠标穿透 / 转义 / 自动收）----
    private Vector2I _短文本尺寸;
    private bool _量得到;
    private const string 长文本 = "主人今天过得怎么样啦？这句话我特意写长一点，看看气泡会不会自己折行、自己长高——要还是像以前那样被固定在窗口里裁掉，这一句的后半截就看不见了。";

    private void E_气泡窗_短文本()
    {
        GD.Print("--- E 组：气泡窗（独立窗 / 自适应 / 字号 / 穿透 / 转义 / 自动收）---");
        BubbleWindow.显示("嗯，在的。", 0f);
    }

    private void E_气泡窗_长文本()
    {
        断言(BubbleWindow.可见中, "气泡窗显示出来了（独立窗，不在桌宠窗口里）");
        断言(BubbleWindow.探针_鼠标穿透, "鼠标穿透已开 —— 点气泡 = 点到底下的东西");
        断言(BubbleWindow.探针_字号 == BubbleWindow.探针_配置字号, $"字号 = 配置值（{BubbleWindow.探针_字号}）");
        断言(BubbleWindow.探针_解析文本 == "嗯，在的。", $"短文本原样渲染（{BubbleWindow.探针_解析文本}）");
        _短文本尺寸 = BubbleWindow.探针_当前尺寸;
        _量得到 = _短文本尺寸.X > 20;
        断言(_量得到, $"按内容自适应：量到尺寸 {_短文本尺寸}（不是写死的大框）");
        BubbleWindow.显示(长文本, 0f);
    }

    private void E_气泡窗_转义()
    {
        断言(BubbleWindow.探针_解析文本 == 长文本, "长文本完整渲染（一个字都没被裁掉）");
        if (_量得到)
        {
            var 尺寸 = BubbleWindow.探针_当前尺寸;
            断言(尺寸.X <= BubbleWindow.探针_最大窗宽, $"折行：宽 {尺寸.X} ≤ 窗口上限 {BubbleWindow.探针_最大窗宽}（本体 {BubbleWindow.探针_配置最大宽度} + 投影留白）");
            断言(尺寸.Y > _短文本尺寸.Y, $"长高：{尺寸.Y} > 短文本 {_短文本尺寸.Y}（短 {_短文本尺寸} → 长 {尺寸}）");
        }
        else GD.Print("[BP] 跳过尺寸断言：本环境量不到字体（headless 无字形）");
        BubbleWindow.显示("方括号 [b] 不该被当成标签[/b]，也不该整段消失", 0f);
    }

    private void E_气泡窗_常驻与自动收()
    {
        断言(BubbleWindow.探针_解析文本 == "方括号 [b] 不该被当成标签[/b]，也不该整段消失",
            $"气泡文本一律当纯文本（BBCode 转义）：{BubbleWindow.探针_解析文本}");
        BubbleWindow.显示("常驻测试", 0f);
        断言(BubbleWindow.可见中, "常驻气泡（时长 0）显示中");
        断言(BubbleWindow.探针_剩余秒 <= 0, "常驻气泡没有倒计时");
        BubbleWindow.显示("自动收测试", 2.0f);      // 给足余量：探针跑在真实帧上，case 之间也会走时间
    }

    private void E_气泡窗_收尾断言()
    {
        断言(BubbleWindow.可见中, "定时气泡显示中");
        BubbleWindow.探针_推进时间(0.5);
        断言(BubbleWindow.可见中, $"推进 0.5s（剩余 {BubbleWindow.探针_剩余秒:0.0}s）还在");
        BubbleWindow.探针_推进时间(2.0);
        断言(!BubbleWindow.可见中, "到点自动收藏 —— 收的是气泡，不是说话动作");
        BubbleWindow.显示("最后一条", 0f);
        断言(BubbleWindow.可见中 && BubbleWindow.探针_当前文本 == "最后一条", "后一条气泡顶掉前一条（不会叠字）");
        BubbleWindow.隐藏();
        断言(!BubbleWindow.可见中, "隐藏() 生效");
    }

    private void 收尾()
    {
        Dialogue.探针_设气泡秒(4f);
        StateMachine.SetState(StateMachine.Idle);
        GD.Print($"[BP] ===== 失败数 = {_失败} =====");
        GD.Print(_失败 == 0 ? "[BP] PASS" : "[BP] FAIL");
        GetTree().Quit(_失败 == 0 ? 0 : 1);
    }
}
