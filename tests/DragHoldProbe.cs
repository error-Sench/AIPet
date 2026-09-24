using System;
using desktop.script.Logic;
using desktop.script.State;
using desktop.script.UX;
using Godot;

namespace desktop.tests;

/// <summary>
/// 拖拽挂起探针（headless，动画组H）：DragProbe 需要真实窗口/光标（非 headless），
/// 这里把「拖满 N 秒 → 静态挂起」的计时逻辑做成可测路径 —— WindowDrag.探针_开始拖拽/推进/松手
/// 与生产**同一内核函数**（推进拖拽计时 / 取消桌宠拖拽），headless 断言：
/// ① 阈值纯函数（够进静态）+ 配置默认 4 秒；
/// ② 满 4s 切静态：挂起按当前档锁定（draghold-nomal）→ A 拎定过渡 → B 循环挂起；
/// ③ 挂起松手 → 播 {档}-c 放下落地（happy 另有 c2 随机）→ 播完回 idle；
/// ④ 未满 4s 松手 → 走旧 dragdown 路径；
/// ⑤ 档位：开心档挂起走 draghold-happy-*；挂起中改档**不重挑**（会话锁定）；
/// ⑥ 探针_拖拽秒数覆盖生效（阈值可注入）。
/// ⚠ 每个场景必须在**一帧内**跑完（下一帧 _Process 的松手分支会兜底取消模拟拖拽）。
/// 用法：Godot_v4.7.2-stable_mono_win64_console.exe --headless --path D:/Games/Github/AIPet res://tests/DragHoldProbe.tscn
/// </summary>
public partial class DragHoldProbe : Node
{
    private int _帧;
    private int _失败;
    private int _步;

    public override void _Ready()
    {
        Main.探针_禁首启提示 = true;
        // 隔离「时间驱动」（问候/音乐）——与其它探针同规矩（共享存档下问候气泡会抢状态）
        StateMachine.设置.探针_冻结时间驱动开关 = true;
        MusicSense.启用 = false;
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        GD.Print("=== DragHoldProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[DH] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[DH] FAIL  {描述}"); }
    }

    private void 结束()
    {
        GD.Print($"[DH] ===== 失败数 = {_失败} =====（步 {_步}/帧 {_帧}）");
        GD.Print(_失败 == 0 ? "[DH] PASS" : "[DH] FAIL");
        GetTree().Quit(_失败 == 0 ? 0 : 1);
    }

    public override void _Process(double delta)
    {
        _帧++;
        if (_帧 > 600) { 断言(false, $"超时（步 {_步} 卡住）"); 结束(); return; }

        if (_步 == 0 && _帧 >= 6)
        {
            StateMachine.入场完成();   // 解除入场门（同其它 headless 探针）
            _步 = 1;
            return;
        }

        if (_步 == 1 && _帧 >= 8)
        {
            // ── ① 阈值纯函数 + 配置默认 ──
            断言(WindowDrag.探针_实例已就绪, "WindowDrag 实例就绪（探针钩子可用）");
            断言(!WindowDrag.够进静态(3.99, 4f) && WindowDrag.够进静态(4f, 4f) && WindowDrag.够进静态(4.01, 4f),
                "阈值纯函数：3.99 未满 / 4.00、4.01 满（≥ 判定，边界含等号）");
            var 覆盖档 = ProjectSettings.GlobalizePath("user://behavior.json");
            if (!System.IO.File.Exists(覆盖档))
                断言(Mathf.Abs(StateMachine.设置.拖拽静止秒 - 4f) < 0.001f, $"「拖拽静止秒」默认 4（实际 {StateMachine.设置.拖拽静止秒}）");
            else GD.Print("[DH] SKIP 默认值断言（存在 user:// 覆盖档，见 tests/README 坑#27）");
            _步 = 2;
            return;
        }

        if (_步 == 2 && _帧 >= 10)
        {
            // ── ② 满 4 秒切静态（真链路，一帧内跑完）──
            WindowDrag.探针_开始拖拽();
            断言(WindowDrag.探针_拖拽中_即时 && CharAnim.当前动画名_只读.StartsWith("dragup", StringComparison.Ordinal),
                $"起拖：拖拽态 + 先播 dragup 起手（实际 {CharAnim.当前动画名_只读}）");
            CharAnim.探针_模拟播完();   // dragup 播完 → 动态段
            var 动态 = CharAnim.当前动画名_只读;
            断言(动态.StartsWith("drag-nomal", StringComparison.Ordinal), $"动态段按当前档（普通）→ drag-nomal-*（实际 {动态}）");
            断言(CharAnim.拖拽锁_只读 == 动态, "动态段锁定（拖拽中档位变化不重挑）");
            WindowDrag.探针_推进拖拽计时(3.9);
            断言(!WindowDrag.探针_曾进静态, "3.9s < 4s：未进静态");
            WindowDrag.探针_推进拖拽计时(0.2);
            断言(WindowDrag.探针_曾进静态, "4.1s ≥ 4s：进入静态挂起");
            断言(CharAnim.挂起会话_只读 == "draghold-nomal", $"挂起按当前档锁定 draghold-nomal（实际 {CharAnim.挂起会话_只读}）");
            断言(CharAnim.当前动画名_只读 == "draghold-nomal-a", $"先播拎定过渡 -a（实际 {CharAnim.当前动画名_只读}）");
            CharAnim.探针_模拟播完();   // A 播完 → B
            断言(CharAnim.当前动画名_只读 == "draghold-nomal-b", $"A 播完 → B 循环挂起（实际 {CharAnim.当前动画名_只读}）");
            断言(CharAnim.动画循环_只读("draghold-nomal-b"), "B 按循环加载（循环动画组）");
            断言(!CharAnim.动画循环_只读("draghold-nomal-a") && !CharAnim.动画循环_只读("draghold-nomal-c")
                 && !CharAnim.动画循环_只读("draghold-happy-c2"), "A/C/c2 段按非循环加载（段名排除，含编号后缀）");
            WindowDrag.探针_松手();
            var 落地 = CharAnim.当前动画名_只读;
            断言(落地.StartsWith("draghold-nomal-c", StringComparison.Ordinal), $"挂起松手 → 播 c 放下落地（实际 {落地}）");
            断言(!WindowDrag.探针_拖拽中 && !WindowDrag.探针_曾进静态 && Mathf.IsZeroApprox((float)WindowDrag.探针_拖拽秒),
                "松手后拖拽/挂起标记与计时复位");
            CharAnim.探针_模拟播完();   // c 播完 → idle
            断言(CharAnim.当前动画名_只读.StartsWith("idle", StringComparison.Ordinal), $"c 播完 → idle（实际 {CharAnim.当前动画名_只读}）");
            断言(CharAnim.挂起会话_只读 == null, "挂起会话清空");
            _步 = 3;
            return;
        }

        if (_步 == 3 && _帧 >= 12)
        {
            // ── ③ 未满 4s 松手 → 旧 dragdown 路径 ──
            WindowDrag.探针_开始拖拽();
            CharAnim.探针_模拟播完();   // dragup → 动态
            WindowDrag.探针_推进拖拽计时(1.0);
            断言(!WindowDrag.探针_曾进静态, "1s：未进静态");
            WindowDrag.探针_松手();
            断言(CharAnim.当前动画名_只读.StartsWith("dragdown", StringComparison.Ordinal),
                $"未满 4s 松手 → 旧 dragdown 路径（实际 {CharAnim.当前动画名_只读}）");
            CharAnim.探针_模拟播完();   // dragdown 播完 → idle
            断言(CharAnim.当前动画名_只读.StartsWith("idle", StringComparison.Ordinal), $"dragdown 播完 → idle（实际 {CharAnim.当前动画名_只读}）");
            _步 = 4;
            return;
        }

        if (_步 == 4 && _帧 >= 14)
        {
            // ── ④ 开心档：挂起走 draghold-happy-*；挂起中改档不重挑 ──
            StateMachine.设置.三档状态启用 = true;
            StateMachine.设置.状态档位 = "开心";
            WindowDrag.探针_开始拖拽();
            CharAnim.探针_模拟播完();   // dragup → 动态
            断言(CharAnim.拖拽锁_只读 == "drag-1",
                $"开心档动态 = 无档基名 drag-1（happy 无 drag-happy-* 变体，走兜底；实际 {CharAnim.拖拽锁_只读}）");
            WindowDrag.探针_推进拖拽计时(4.2);
            断言(WindowDrag.探针_曾进静态 && CharAnim.挂起会话_只读 == "draghold-happy",
                $"开心档挂起 = draghold-happy（实际 {CharAnim.挂起会话_只读}）");
            断言(CharAnim.当前动画名_只读 == "draghold-happy-a", $"拎定过渡 draghold-happy-a（实际 {CharAnim.当前动画名_只读}）");
            StateMachine.设置.状态档位 = "不良";   // 挂起中改档 → 不重挑
            断言(CharAnim.挂起会话_只读 == "draghold-happy", "挂起中改档不重挑（会话仍 draghold-happy）");
            WindowDrag.探针_松手();
            断言(CharAnim.当前动画名_只读.StartsWith("draghold-happy-c", StringComparison.Ordinal),
                $"松手播 draghold-happy-c*（c/c2 随机；实际 {CharAnim.当前动画名_只读}）");
            CharAnim.探针_模拟播完();   // 落地播完 → idle
            断言(CharAnim.当前动画名_只读.StartsWith("idle", StringComparison.Ordinal), $"落地播完 → idle（实际 {CharAnim.当前动画名_只读}）");
            StateMachine.设置.三档状态启用 = false;   // 还原（默认普通）
            StateMachine.设置.状态档位 = "普通";
            _步 = 5;
            return;
        }

        if (_步 == 5 && _帧 >= 16)
        {
            // ── ⑤ 探针_拖拽秒数覆盖（阈值注入）──
            WindowDrag.探针_拖拽秒数覆盖 = 0.5f;
            WindowDrag.探针_开始拖拽();
            CharAnim.探针_模拟播完();   // dragup → 动态
            WindowDrag.探针_推进拖拽计时(0.4);
            断言(!WindowDrag.探针_曾进静态, "覆盖 0.5s：0.4s 未进静态");
            WindowDrag.探针_推进拖拽计时(0.2);
            断言(WindowDrag.探针_曾进静态, "覆盖 0.5s：0.6s 进静态（覆盖生效）");
            WindowDrag.探针_松手();
            WindowDrag.探针_拖拽秒数覆盖 = null;
            CharAnim.探针_模拟播完();   // c 播完 → idle
            _步 = 6;
            return;
        }

        if (_步 == 6 && _帧 >= 18)
        {
            断言(CharAnim.当前动画名_只读.StartsWith("idle", StringComparison.Ordinal), $"收尾回 idle（实际 {CharAnim.当前动画名_只读}）");
            断言(StateMachine.CurrentState == StateMachine.Idle, $"逻辑态 idle（实际 {StateMachine.CurrentState}）");
            结束();
        }
    }
}
