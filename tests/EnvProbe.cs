using System;
using desktop.script.State;
using Godot;

namespace desktop.tests;

/// <summary>
/// 环境感知探针（headless，P6）：**默认关闭**、真实 Win32 读取可用、启用后闸门生效、边沿与节流正确。
/// 用法：Godot_..._console.exe --headless --path D:/Games/Github/AIPet res://tests/EnvProbe.tscn
/// </summary>
public partial class EnvProbe : Node
{
    private int _帧;
    private int _失败;
    private bool _原启用;
    private float _原阈值;
    private bool _原静默;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        _原启用 = EnvironmentSense.启用;
        _原阈值 = EnvironmentSense.离开阈值秒;
        _原静默 = EnvironmentSense.全屏静默;
        GD.Print("=== EnvProbe: 场景已实例化 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[EV] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[EV] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        if (_帧 == 5) { A组(); return; }
        if (_帧 == 20) { B组(); return; }
        if (_帧 == 25) { C组(); D组(); E组(); F组(); 收尾(); return; }
        if (_帧 > 900) { GD.PrintErr("[EV] 超时"); GetTree().Quit(2); }
    }

    private float _空闲1 = -1f;

    private void A组()
    {
        GD.Print("--- A 组：默认关闭（主人的开关必须真的关得住）---");
        EnvironmentSense.探针_重置();
        EnvironmentSense.启用 = false; // 模拟配置默认值
        断言(!EnvironmentSense.启用, "A1 默认不启用");
        断言(EnvironmentSense.空闲秒 == 0f, "A2 未启用时**不查询**空闲（恒 0）");
        断言(!EnvironmentSense.全屏, "A3 未启用时不查询全屏（恒 false）");
        断言(!EnvironmentSense.应当静默 && !EnvironmentSense.主人不在, "A4 未启用时既不静默也不判「不在」");
        _空闲1 = EnvironmentSense.探针_真实读取().空闲秒;
    }

    private void B组()
    {
        GD.Print("--- B 组：真实 Win32 读取（不受开关影响，验证调用本身可用）---");
        var (真实空闲, 真实全屏) = EnvironmentSense.探针_真实读取();
        GD.Print($"[EV] 真实读数：第一次 {_空闲1:0.0}s → 第二次 {真实空闲:0.0}s / 全屏 {真实全屏}");
        断言(真实空闲 >= 0f && 真实空闲 < 86400f, $"B1 空闲秒数读得到且在合理范围（{真实空闲:0.0}s）");
        // 它是活的时钟：间隔 0.25s 再读，要么前进（无人操作），要么归零（刚好有输入）。
        // 注意别假设「刚跑过探针就该很小」——探针在后台跑**不算**用户输入（首版断言就是这么错的）。
        断言(真实空闲 >= _空闲1 || 真实空闲 < 1f,
            $"B2 两次读数符合活的时钟（{_空闲1:0.0}s → {真实空闲:0.0}s：前进=无人操作，归零=刚有输入）");
    }

    private void C组()
        {
            GD.Print("--- C 组：启用后闸门与静默 ---");
            EnvironmentSense.启用 = true;
            EnvironmentSense.离开阈值秒 = 300f;
            EnvironmentSense.全屏静默 = true;
            EnvironmentSense.探针_注入(10f, true); // 主人在，且在全屏
            断言(EnvironmentSense.应当静默, $"C1 全屏（静默开）→ 应当静默（{EnvironmentSense.概述}）");
            断言(!StateMachine.探针_允许主动(), "C2 全屏时主动行为闸门被拦下");
            EnvironmentSense.探针_注入(10f, false);
            断言(!EnvironmentSense.应当静默, "C3 非全屏 → 不静默");

            EnvironmentSense.全屏静默 = false;
            EnvironmentSense.探针_注入(10f, true);
            断言(!EnvironmentSense.应当静默, "C4 把「全屏静默」关掉后，全屏也不再静默（开关有效）");
            EnvironmentSense.全屏静默 = true;
        }

    private void D组()
    {
        GD.Print("--- D 组：离开/回来边沿 + 节流 ---");
        EnvironmentSense.探针_重置();
        EnvironmentSense.探针_注入(10f, false);
        EnvironmentSense.心跳(1f);
        断言(!EnvironmentSense.主人不在 && !EnvironmentSense.刚回来, "D1 主人在 → 不判离开");
        EnvironmentSense.探针_注入(9999f, false);
        EnvironmentSense.心跳(1f);
        断言(EnvironmentSense.主人不在 && !EnvironmentSense.刚回来, "D2 空闲超阈值 → 主人不在（但这不是「刚回来」）");
        EnvironmentSense.探针_注入(1f, false);
        EnvironmentSense.心跳(1f);
        断言(EnvironmentSense.刚回来, "D3 从「不在」变「在」→ 刚回来（可用于打招呼）");
        EnvironmentSense.心跳(1f);
        断言(!EnvironmentSense.刚回来, "D4 边沿只报一次（下一拍不再报）");

        EnvironmentSense.探针_注入(9999f, false); EnvironmentSense.心跳(1f);
        EnvironmentSense.探针_注入(1f, false); EnvironmentSense.心跳(1f);
        断言(!EnvironmentSense.刚回来, "D5 60s 冷却内不重复报「刚回来」（不烦人）");

    }

    private void E组()
    {
        GD.Print("--- E 组：不允许的无用监听 ---");
        EnvironmentSense.启用 = false;
        EnvironmentSense.探针_注入(9999f, true);
        EnvironmentSense.心跳(1f);
        断言(!EnvironmentSense.应当静默 && !EnvironmentSense.主人不在, "E1 关掉开关后注入值也不生效（彻底不动）");

        // 恢复
        EnvironmentSense.探针_重置();
        EnvironmentSense.启用 = _原启用;
        EnvironmentSense.离开阈值秒 = _原阈值;
        EnvironmentSense.全屏静默 = _原静默;
        GD.Print($"[EV] 已恢复 → {EnvironmentSense.概述}");
    }

    private void F组()
    {
        GD.Print("--- F 组：配置接线（behavior.json → 感知层 / 事件池；Plan #19 阈值 + Plan #20 开关）---");
        StateMachine.设置.加载();   // 重读真实 behavior.json（只读不写）
        断言(EnvironmentSense.启用 == StateMachine.设置.环境感知启用,
            $"「环境感知启用」交给感知层（{EnvironmentSense.启用}）");
        断言(Math.Abs(EnvironmentSense.离开阈值秒 - Math.Max(30f, StateMachine.设置.离开阈值秒)) < 0.01f,
            $"「离开阈值秒」（键鼠空闲判据）交给感知层（{EnvironmentSense.离开阈值秒:0}s；下限 30）");
        断言(EventPool.记录启用 == StateMachine.设置.事件记录启用,
            $"「事件记录启用」交给事件池（{EventPool.记录启用}）");
    }

    private void 收尾()
    {
        GD.Print($"[EV] ===== 失败数 = {_失败} =====");
        GD.Print(_失败 == 0 ? "[EV] PASS" : "[EV] FAIL");
        GetTree().Quit(_失败 == 0 ? 0 : 1);
    }
}