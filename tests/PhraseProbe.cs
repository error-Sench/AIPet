using System;
using System.Linq;
using desktop.script.Soul;
using desktop.script.State;
using Godot;

namespace desktop.tests;

/// <summary>
/// PhraseProbe（headless）：**本地话语表**（`config/phrases.json` + `PhraseTable`）。
/// 覆盖：解析（数组类与嵌套的「问候/时段」）/ 随机不连续重复 / 占位符替换（合成表） /
/// 坏数据与缺表的**内置兜底**（不许哑）/ 与启动问候的时段联动。
/// </summary>
public partial class PhraseProbe : Node
{
    private int _帧;
    private int _失败;

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[PP] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[PP] FAIL  {描述}"); }
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 5: 组_真实表(); break;
            case 10: 组_探针文本(); break;
            case 15: 组_坏数据兜底(); break;
            case 20: 组_问候联动(); break;
            case 25:
                GD.Print($"[PP] ===== 失败数 = {_失败} =====");
                GD.Print(_失败 == 0 ? "[PP] PASS" : "[PP] FAIL");
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                break;
        }
    }

    private void 组_真实表()
    {
        GD.Print("--- A 组：真实 config/phrases.json ---");
        PhraseTable.探针_恢复();
        断言(PhraseTable.已载入, "config/phrases.json 载入成功");
        断言(PhraseTable.有("被摸") && PhraseTable.有("久坐") && PhraseTable.有("降级"),
            "程序侧会说话的分类都有话（被摸/久坐/降级）");
        断言(!PhraseTable.有("磁盘"), "Plan #16：旧「磁盘」分类已随磁盘提醒删除（表里不该再留）");
        foreach (var 段 in new[] { "早上", "中午", "下午", "晚上", "深夜" })
            断言(PhraseTable.有($"问候/{段}"), $"问候/{段} 有话");
        var 句 = PhraseTable.取("久坐");
        断言(!string.IsNullOrWhiteSpace(句), $"能抽到句子（「{句}」）");

        var 抽到 = Enumerable.Range(0, 60).Select(_ => PhraseTable.取("久坐")).ToHashSet();
        断言(抽到.Count >= 2, $"同一类多句子都会抽到（抽到 {抽到.Count} 种）");
    }

    private void 组_探针文本()
    {
        GD.Print("--- B 组：探针文本装表（解析 / 空类 / 不连续重复）---");
        PhraseTable.探针_载入文本("{\"_comment\":\"x\",\"久坐\":[\"A\",\"B\"],\"问候\":{\"早上\":[\"早呀\"]},\"测试\":[\"{甲} 与 {乙}\"],\"空\":[]}");
        断言(PhraseTable.行数("久坐") == 2, "数组类读到 2 句");
        断言(PhraseTable.行数("问候/早上") == 1, "嵌套的 问候/早上 读到 1 句");
        断言(!PhraseTable.有("空"), "空数组 = 该类不说话");
        var 替 = PhraseTable.取("测试", ("甲", "一"), ("乙", "二"));
        断言(替 == "一 与 二", $"占位符替换（「{替}」；机制保留给自定义句子，内置类别当前没人用）");

        var 上次 = "";
        var 连重 = 0;
        for (var i = 0; i < 30; i++)
        {
            var 句 = PhraseTable.取("久坐");
            if (句 == 上次) 连重++;
            上次 = 句;
        }
        断言(连重 == 0, $"随机不会连着重复（30 抽里连重 {连重} 次）");
    }

    private void 组_坏数据兜底()
    {
        GD.Print("--- C 组：坏数据 / 缺表 → 内置兜底（不许哑）---");
        PhraseTable.探针_载入文本("{ 这不是 JSON ");
        断言(!PhraseTable.已载入, "坏 JSON → 标记未载入");
        断言(PhraseTable.有("久坐") && PhraseTable.行数("久坐") > 0, "坏数据也能说话（内置兜底）");
        断言(!string.IsNullOrWhiteSpace(PhraseTable.取("降级")), "「降级」兜底句还在");
        PhraseTable.探针_恢复();
        断言(PhraseTable.已载入, "探针_恢复() 回到真实表");
    }

    private void 组_问候联动()
    {
        GD.Print("--- D 组：时段 → 问候（与 DailyRoutine 联动）---");
        PhraseTable.探针_载入文本("{\"问候\":{\"早上\":[\"早呀\"],\"中午\":[\"中午好\"],\"深夜\":[\"夜猫子\"]}}");
        断言(DailyRoutine.问候语(new DateTime(2026, 9, 19, 8, 0, 0)) == ("早上", "早呀"), "08:00 → 早上「早呀」");
        断言(DailyRoutine.问候语(new DateTime(2026, 9, 19, 12, 30, 0)).语句 == "中午好", "12:30 → 中午「中午好」");
        断言(DailyRoutine.问候语(new DateTime(2026, 9, 19, 3, 0, 0)).语句 == "夜猫子", "03:00 → 深夜「夜猫子」");
        PhraseTable.探针_恢复();
    }
}
