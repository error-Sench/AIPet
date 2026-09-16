using System;
using System.IO;
using System.Text;
using Godot;
using desktop.script.Soul;
using desktop.script.Agent;

namespace desktop.tests;

/// <summary>
/// ContextProbe（headless）：验证「上下文接口」—— 组装内容、数据文件脚手架、只读不推送。
/// <para>
/// 场景：`tests/ContextProbe.tscn`。跑法见 `tests/README.md`。
/// </para>
/// </summary>
public partial class ContextProbe : Node
{
    private int _帧;
    private int _失败;
    private const string 哨兵 = "SENTINEL-不要覆盖我";

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[CTX] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[CTX] FAIL  {描述}"); }
    }

    private readonly string _临时目录 = Path.Combine(Path.GetTempPath(), "aipet_ctx_probe");

    public override void _Ready()
    {
        // B6（打包审计）：探针**绝不碰真实用户数据** —— 全部走临时覆写路径
        Directory.CreateDirectory(_临时目录);
        ContextTable.探针_上下文覆写 = Path.Combine(_临时目录, "context.md");
        ContextTable.探针_画像覆写 = Path.Combine(_临时目录, "profile.md");
        ContextTable.探针_记忆覆写 = Path.Combine(_临时目录, "memory.jsonl");
        GD.Print($"=== ContextProbe: 场景已实例化（临时目录 {_临时目录}）===");
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 10: A组_组装内容(); break;
            case 20: B组_脚手架不覆盖(); break;
            case 30: C组_生成与只读不推送(); break;
            case 40:
                ContextTable.探针_上下文覆写 = ""; ContextTable.探针_画像覆写 = ""; ContextTable.探针_记忆覆写 = "";
                try { Directory.Delete(_临时目录, true); } catch { /* 忽略 */ }
                GD.Print($"[CTX] ===== 失败数 = {_失败} =====");
                GetTree().Quit(_失败 == 0 ? 0 : 1);
                break;
        }
    }

    private void A组_组装内容()
    {
        GD.Print("--- A 组：组装内容（Agent 读这一个文件就够）---");
        ContextTable.确保数据文件();
        var 文本 = ContextTable.组装();

        断言(文本.Contains("桌宠上下文（给 Agent 读取）"), "A1 有标题（一眼看出这是给 Agent 的接口文件）");
        断言(文本.Contains("本文件只读"), "A2 标明只读（下次生成会覆盖）");
        断言(文本.Contains("soul.md") && 文本.Contains("stats.json") &&
            文本.Contains("profile.md") && 文本.Contains("memory.jsonl"),
            "A3 四个源文件路径都在（想深挖顺着路径去读，不用到处翻）");
        断言(文本.Contains($"心情 mood {StatsTable.心情整}/100") && 文本.Contains(StatsTable.当前心情文字),
            $"A4 数值摘要 + 文字状态（{StatsTable.当前心情文字}）");
        断言(文本.Contains(SoulTable.RawText.Trim()) || SoulTable.RawText.Trim().Length == 0,
            "A5 人格全文（soul.md）已带进上下文");
        断言(文本.Contains("## 最近记忆"), "A6 记忆段落存在");
        断言(文本.Contains("\"anim\"") && !文本.Contains("play_anim\",\"name"),
            "A7a 指令示例用对了键名（play_anim 的键是 anim，写 name 会被丢弃 —— 子 Agent 核对时抓到的）");
        断言(文本.Contains("```pet") && 文本.Contains("set_state") && 文本.Contains("set_mood"),
            "A7 指令通道说明（它能指挥桌宠做什么）");
        断言(!文本.Contains("供你参考") && !文本.Contains("它此刻的感受") && !文本.Contains("不必复述"),
            "A8 不写「它此刻的感受，供你参考」那类提示句（只提供信息，不代替用户说话）");
    }

    private void B组_脚手架不覆盖()
    {
        GD.Print("--- B 组：数据文件脚手架（只创建、绝不覆盖）---");
        // 哨兵：往两个数据文件里塞内容，再跑一次脚手架 → 内容必须原样在
        File.WriteAllText(ContextTable.画像路径, 哨兵, new UTF8Encoding(false));
        File.WriteAllText(ContextTable.记忆路径, 哨兵 + "\n", new UTF8Encoding(false));
        ContextTable.确保数据文件();
        断言(File.ReadAllText(ContextTable.画像路径) == 哨兵, "B1 已存在的 profile.md 不被覆盖（Agent 写的东西是安全的）");
        断言(File.ReadAllText(ContextTable.记忆路径).Contains(哨兵), "B2 已存在的 memory.jsonl 不被覆盖");
        断言(ContextTable.画像路径.StartsWith(_临时目录), $"探针全程走临时目录，不碰真实用户数据（{ContextTable.画像路径}）");
        // 生产路径形状：临时清空覆写检查完再恢复
        var 覆写 = ContextTable.探针_画像覆写;
        ContextTable.探针_画像覆写 = "";
        断言(ContextTable.画像路径.Replace('\\', '/').EndsWith("soul/profile.md"),
            $"真实画像路径在 soul/ 下（{ContextTable.画像路径}）");
        ContextTable.探针_画像覆写 = 覆写;

        // 记忆只带末尾若干条
        var 行 = new StringBuilder();
        for (var i = 1; i <= 30; i++) 行.AppendLine($"{{\"t\":\"2026-09-16\",\"text\":\"第{i}条记忆\"}}");
        File.WriteAllText(ContextTable.记忆路径, 行.ToString(), new UTF8Encoding(false));
        var 文本 = ContextTable.组装();
        断言(文本.Contains("第30条记忆") && !文本.Contains("第1条记忆"),
            $"B4 只带最近 {ContextTable.记忆条数上限} 条记忆（流水账不整包塞）");
    }

    private void C组_生成与只读不推送()
    {
        GD.Print("--- C 组：生成文件 + 确认「只读不推送」---");
        // 恢复干净模板
        File.Delete(ContextTable.画像路径);
        File.Delete(ContextTable.记忆路径);
        ContextTable.确保数据文件();
        ContextTable.生成();

        断言(File.Exists(ContextTable.上下文路径), $"C1 上下文文件已生成（{ContextTable.上下文路径}）");
        断言(File.ReadAllText(ContextTable.上下文路径).Contains("桌宠上下文"),
            "C2 内容是组装好的上下文");

        // 硬规则：发给 Agent 的文本 = 主人原话，什么都不附
        断言(AgentBridge.组装提示("帮我看看这个文件") == "帮我看看这个文件",
            "C3 发给 Agent 的就是主人原话（**不做主动注入**，上下文由 Agent 自己读）");
        断言(StatsTable.当前心情文字 is "超开心" or "心情不错" or "平平静静" or "有点蔫" or "不太开心" or "很低落",
            $"C4 心情文字映射可用（{StatsTable.当前心情文字}）");
    }
}
