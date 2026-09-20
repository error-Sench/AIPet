using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// TouchProbe（headless）：**P10 扩充** —— 摸身体 / 转身 / 三档状态 / 干活进出场。
/// <para>
/// 覆盖：① 命中区纯函数（脸区 / 身体区互不重叠，脸区优先）② 摸摸部位分流
/// （头 → `interact` 序列；身体 → `interact_body` 或 30% 概率 `turn`）
/// ③ 三档状态（开心 → `think-happy`、摸头序列首段 → `interact-happy-a`；不良 → `think-poor`；关 → 老行为）
/// ④ 干活进出场（`开始干活()` → `switch-up` → working；`结束干活()` → `switch-down` → idle）。
/// </para>
/// <para>
/// **时序两条坑**（都实测过）：① `摸摸` 是**排队**的 —— 要等当前动画播完才演，所以用「轮询等状态」而不是定帧断言；
/// ② `CharAnim.PlayState` 走 `CallDeferred` —— 动画**下一帧**才换，断言要隔一帧。
/// </para>
/// </summary>
public partial class TouchProbe : Node
{
    private int _帧;
    private int _失败;
    private int _阶段帧;
    private int _阶段;
    private readonly Vector2I 窗 = new(256, 256);

    public override void _Ready()
    {
        StateMachine.探针_禁用包裹 = true;   // 本探针测三档映射与部位分流（即时切换语义）：包裹段（A/C 过渡）旁路——包裹段另有 WrapProbe
        MusicSense.启用 = false;   // 组③：隔离音乐反应（系统有声就跳舞会顶状态）
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== TouchProbe: 场景已实例化（数值已与择档解耦，无需钉值） ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[TP] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[TP] FAIL  {描述}"); }
    }

    private void 下一阶段() { _阶段++; _阶段帧 = 0; }

    public override void _Process(double delta)
    {
        _帧++;
        _阶段帧++;
        if (_帧 > 2000) { GD.PrintErr("[TP] 超时"); GetTree().Quit(2); return; }

        switch (_阶段)
        {
            case 0:   // 准备
                StateMachine.入场完成();
                DailyRoutine.探针_重置();          // 清掉启动问候（它会占住 Greet）
                FacePinch.探针_重置();
                StateMachine.SetState(StateMachine.Idle);
                GD.Print($"[TP] 三档开关={StateMachine.设置.三档状态启用} 档位={StateMachine.设置.状态档位}（默认关/普通）");
                下一阶段();
                break;

            case 1:   // A：命中区纯函数
                A组_命中区();
                下一阶段();
                break;

            case 2:   // B：发摸摸（头）
                GD.Print("--- B 组：摸摸（头）→ interact 三段序列 ---");
                StateMachine.SetState(StateMachine.Idle);
                StateMachine.摸摸(StateMachine.TouchPart.Head);
                下一阶段();
                break;

            case 3:   // B：排队等反应生效
                if (StateMachine.CurrentState == StateMachine.Interact)
                {
                    var 名 = CharAnim.当前动画名_只读;
                    GD.Print($"[TP] 摸头反应开始（第 {_阶段帧} 帧）：动画={名}");
                    断言(名.StartsWith("interact-"), $"摸头播 interact 序列：{名}");
                    下一阶段();
                }
                else if (_阶段帧 > 500)
                {
                    断言(false, $"摸头没在 8 秒内进入 interact（当前 {StateMachine.CurrentState}）");
                    下一阶段();
                }
                break;

            case 4:   // C：发摸摸（身体）
                GD.Print("--- C 组：摸摸（身体）→ interact_body / turn ---");
                StateMachine.SetState(StateMachine.Idle);
                StateMachine.摸摸(StateMachine.TouchPart.Body);
                下一阶段();
                break;

            case 5:   // C：排队等反应生效
                if (StateMachine.CurrentState is StateMachine.InteractBody or StateMachine.Turn)
                {
                    var 名 = CharAnim.当前动画名_只读;
                    GD.Print($"[TP] 摸身体反应开始（第 {_阶段帧} 帧）：状态={StateMachine.CurrentState} 动画={名}");
                    断言(名.StartsWith("interact_body-") || 名.StartsWith("turn-"), $"摸身体播身体/转身素材：{名}");
                    下一阶段();
                }
                else if (_阶段帧 > 500)
                {
                    断言(false, $"摸身体没在 8 秒内进入身体反应（当前 {StateMachine.CurrentState}）");
                    下一阶段();
                }
                break;

            case 6:   // D1：三档=开心
                GD.Print("--- D 组：三档状态（开心 / 不良 / 摸头高兴档 / 关）---");
                StateMachine.设置.三档状态启用 = true;
                StateMachine.设置.状态档位 = "开心";
                StateMachine.SetState(StateMachine.Think);
                下一阶段();
                break;

            case 7:
                断言(CharAnim.当前动画名_只读 == "think-happy", $"档位=开心 → think-happy（实际 {CharAnim.当前动画名_只读}）");
                StateMachine.设置.状态档位 = "不良";
                StateMachine.SetState(StateMachine.Idle);
                StateMachine.SetState(StateMachine.Think);
                下一阶段();
                break;

            case 8:   // D2：三档=不良
                断言(CharAnim.当前动画名_只读 == "think-poor", $"档位=不良 → think-poor（实际 {CharAnim.当前动画名_只读}）");
                StateMachine.设置.状态档位 = "开心";
                StateMachine.SetState(StateMachine.Idle);
                StateMachine.SetState(StateMachine.Interact);   // 直接进序列：看首段是否换档
                下一阶段();
                break;

            case 9:   // D3：摸头高兴档
                断言(CharAnim.当前动画名_只读 == "interact-happy-a",
                    $"开心档下摸头序列首段换成 interact-happy-a（实际 {CharAnim.当前动画名_只读}）");
                StateMachine.设置.三档状态启用 = false;
                StateMachine.设置.状态档位 = "普通";
                StateMachine.SetState(StateMachine.Idle);
                StateMachine.SetState(StateMachine.Think);
                下一阶段();
                break;

            case 10:  // D4：三档关 → 回老行为
                断言(CharAnim.当前动画名_只读.StartsWith("think-"), $"关掉三档 → 回池内随机（数值已不参与择档；实际 {CharAnim.当前动画名_只读}）");
                下一阶段();
                break;

            case 11:  // E1：开工
                GD.Print("--- E 组：干活进出场（VPet Switch_Up / Switch_Down）---");
                StateMachine.SetState(StateMachine.Idle);
                StateMachine.开始干活();
                下一阶段();
                break;

            case 12:
                断言(StateMachine.CurrentState == StateMachine.WorkIn, $"开工先走过渡态（{StateMachine.CurrentState}）");
                断言(CharAnim.当前动画名_只读 == "switch-up", $"过渡播 switch-up（实际 {CharAnim.当前动画名_只读}）");
                StateMachine.探针_推进时间(2.0f);      // 过渡到点 → 自动落到 working
                下一阶段();
                break;

            case 13:
                断言(StateMachine.CurrentState == StateMachine.Working, $"过渡到点自动进 working（{StateMachine.CurrentState}）");
                // PlayState 是 CallDeferred → 动画下一帧才换，这里等到它换过来（最多 3 帧）
                if (CharAnim.当前动画名_只读.StartsWith("work-"))
                {
                    断言(true, $"working 播 work 池（{CharAnim.当前动画名_只读}）");
                    StateMachine.结束干活();
                    下一阶段();
                }
                else if (_阶段帧 > 3)
                {
                    断言(false, $"working 没播 work 池（实际 {CharAnim.当前动画名_只读}）");
                    StateMachine.结束干活();
                    下一阶段();
                }
                break;

            case 14:
                断言(StateMachine.CurrentState == StateMachine.WorkOut, $"收工先走过渡态（{StateMachine.CurrentState}）");
                断言(CharAnim.当前动画名_只读 == "switch-down", $"过渡播 switch-down（实际 {CharAnim.当前动画名_只读}）");
                StateMachine.探针_推进时间(2.0f);
                下一阶段();
                break;

            case 15:
                断言(StateMachine.CurrentState == StateMachine.Idle, $"过渡到点回 idle（{StateMachine.CurrentState}）");
                收尾();
                break;
        }
    }

    // ---- A：命中区（纯函数；换算见 config/behavior.json 注释）----
    private void A组_命中区()
    {
        GD.Print("--- A 组：命中区纯函数（脸区 / 身体区）---");
        var 脸心 = new Vector2(0.371f * 窗.X, 0.32f * 窗.Y);     // 脸区中心
        var 身中 = new Vector2(0.512f * 窗.X, 0.553f * 窗.Y);     // 身体区中心
        var 脚部 = new Vector2(0.5f * 窗.X, 0.95f * 窗.Y);        // 脚下（两个区都不该命中）

        断言(FacePinch.脸区命中(脸心, 窗), "脸区中心命中（捏脸命中区）");
        断言(WindowDrag.身体区命中(身中, 窗), "身体区中心命中（VPet touchbody 换算）");
        断言(!WindowDrag.身体区命中(脸心, 窗), "脸区中心**不在**身体区（两区不重叠 → 单击分流不会打架）");
        断言(!FacePinch.脸区命中(身中, 窗), "身体区中心不在脸区");
        断言(!WindowDrag.身体区命中(脚部, 窗) && !FacePinch.脸区命中(脚部, 窗), "脚下两个区都不命中");
    }

    private void 收尾()
    {
        StateMachine.设置.三档状态启用 = false;
        StateMachine.设置.状态档位 = "普通";
        StateMachine.SetState(StateMachine.Idle);
        GD.Print($"[TP] ===== 失败数 = {_失败} =====");
        GD.Print(_失败 == 0 ? "[TP] PASS" : "[TP] FAIL");
        GetTree().Quit(_失败 == 0 ? 0 : 1);
    }
}
