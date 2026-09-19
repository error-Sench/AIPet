using desktop.script.Agent;
using desktop.script.Soul;
using Godot;

namespace desktop.tests;

/// <summary>
/// 数值层探针（headless）：**mood = 主人情绪读数**（2026-09-20 P5 返工后）。
/// 覆盖：① 衰减（每 30s 向中性 50 回 5 点）② 夹取 ③ Agent 指令 `set_mood` 接线
/// ④ 存盘 schema（只有 mood + 时间戳，旧字段 energy/affection 已移除）⑤ 离线补算（离线越久越接近中性）。
/// 探针隔离：先取真实值 → 存盘路径切临时档 → 收尾只删临时档（绝不碰主人真实存档）。
/// 用法：Godot_..._console.exe --headless --path D:/Games/Github/AIPet res://tests/StatsProbe.tscn
/// </summary>
public partial class StatsProbe : Node
{
    private int _帧;
    private int _失败;
    private float _原心情;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        StatsTable.载入();
        _原心情 = StatsTable.当前心情;
        // 探针隔离（2026-09-20）：**先取真实值，再把存盘路径切到临时档** ——
        // 之后的存盘/载入全走临时文件；收尾只删临时档，**绝不碰主人的真实存档**。
        StatsTable.探针_覆盖存盘路径 = "user://probe_stats_tmp.json";
        GD.Print($"=== StatsProbe: 起始 {StatsTable.概述}（存盘走临时档）===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[ST] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[ST] FAIL  {描述}"); }
    }

    private static bool 近(float a, float b, float 容差 = 0.01f) => Mathf.Abs(a - b) <= 容差;

    public override void _Process(double delta)
    {
        _帧++;

        if (_帧 == 5)
        {
            GD.Print("--- A 组：衰减（每 30s 向中性 50 回 5 点）---");
            // A1 高于中性 → 往回落
            StatsTable.探针_设值(90f);
            StatsTable.探针_衰减(30f);
            断言(近(StatsTable.当前心情, 85f), $"A1 高读数 90 → 30s 后 85（实际 {StatsTable.当前心情:0.0}）");
            // A2 低于中性 → 往上回
            StatsTable.探针_设值(20f);
            StatsTable.探针_衰减(30f);
            断言(近(StatsTable.当前心情, 25f), $"A2 低读数 20 → 30s 后 25（实际 {StatsTable.当前心情:0.0}）");
            // A3 不到一个节拍不动
            StatsTable.探针_设值(80f);
            StatsTable.探针_衰减(29f);
            断言(近(StatsTable.当前心情, 80f), $"A3 29s（不到一个节拍）不动（实际 {StatsTable.当前心情:0.0}）");
            // A4 长时间 → 停在中性、不过冲
            StatsTable.探针_衰减(600f);
            断言(近(StatsTable.当前心情, 50f), $"A4 长衰减停在中性 50、不过冲（实际 {StatsTable.当前心情:0.0}）");

            GD.Print("--- B 组：夹取 ---");
            StatsTable.探针_设值(999f);
            断言(近(StatsTable.当前心情, 100f), $"B1 上限夹取（{StatsTable.当前心情:0.0}）");
            StatsTable.探针_设值(-50f);
            断言(近(StatsTable.当前心情, 0f), $"B2 下限夹取（{StatsTable.当前心情:0.0}）");

            GD.Print("--- C 组：Agent 指令 set_mood 接线（LLM 判断 → 写入）---");
            // C1 数值形式
            var (_, c1) = PetCommands.解析("```pet\n{\"cmd\":\"set_mood\",\"mood\":\"85\"}\n```");
            var n1 = PetCommands.执行(c1, out var log1);
            断言(n1 == 1 && 近(StatsTable.当前心情, 85f), $"C1 set_mood=85 生效（日志 {log1[0]}）");
            // C2 关键词形式
            var (_, c2) = PetCommands.解析("```pet\n{\"cmd\":\"set_mood\",\"mood\":\"难过\"}\n```");
            var n2 = PetCommands.执行(c2, out var log2);
            断言(n2 == 1 && 近(StatsTable.当前心情, 20f), $"C2 set_mood=难过 映射到 20（日志 {log2[0]}）");
            // C3 非法值被拒
            var (_, c3) = PetCommands.解析("```pet\n{\"cmd\":\"set_mood\",\"mood\":\"狂暴\"}\n```");
            var n3 = PetCommands.执行(c3, out var log3);
            断言(n3 == 0 && log3[0].Contains("无法识别"), $"C3 无法识别的 mood 被拒（日志 {log3[0]}）");
            // C4 soul_set 仍是「未实现」
            var (_, c4) = PetCommands.解析("```pet\n{\"cmd\":\"soul_set\",\"key\":\"x\",\"value\":\"y\"}\n```");
            var n4 = PetCommands.执行(c4, out var log4);
            断言(n4 == 0 && log4[0].Contains("未实现"), $"C4 soul_set 仍记「未实现」（P3）（日志 {log4[0]}）");

            GD.Print("--- D 组：存盘 schema（只 mood + 时间戳）---");
            StatsTable.探针_设值(80f);
            StatsTable.存盘();
            var 数值文件 = StatsTable.探针_存盘路径;
            var 数值文本 = System.IO.File.ReadAllText(数值文件);
            using (var 文档 = System.Text.Json.JsonDocument.Parse(数值文本))
            {
                var 根 = 文档.RootElement;
                断言(根.TryGetProperty("mood", out var m5) && System.Math.Abs(m5.GetSingle() - 80f) < 0.01f,
                    $"D1 数值落在 JSON 文件里、Agent 可自行读取（{数值文件}）");
                断言(!根.TryGetProperty("energy", out _) && !根.TryGetProperty("affection", out _),
                    "D2 旧字段（energy / affection）已从 schema 移除（归 mod 扩展）");
                断言(根.TryGetProperty("savedAtUnix", out _), "D3 含时间戳（供离线补算）");
            }
            断言(!数值文本.Contains("供你参考") && !数值文本.Contains("复述"),
                "D4 文件里只有数据、没有写给 Agent 的「提示句」（我们只提供信息，不代替用户说话）");

            GD.Print("--- E 组：离线补算（离线越久越接近中性）---");
            StatsTable.探针_设值(70f);
            StatsTable.存盘();
            var 路径 = StatsTable.探针_存盘路径;
            断言(System.IO.File.Exists(路径), $"E1 存盘文件已生成（{路径}）");
            var 文本 = System.IO.File.ReadAllText(路径);
            // 模拟「离线 2 小时后启动」：手改时间戳，再走载入路径
            var 旧 = System.Text.Json.JsonDocument.Parse(文本);
            var 改后 = 文本.Replace(旧.RootElement.GetProperty("savedAtUnix").GetRawText(),
                (System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7200).ToString());
            System.IO.File.WriteAllText(路径, 改后);
            StatsTable.探针_重置载入标志();
            StatsTable.载入();
            断言(近(StatsTable.当前心情, 50f), $"E2 离线 2h 后载入 → 已回中性 50（实际 {StatsTable.当前心情:0.0}）");
        }
        else if (_帧 == 20)
        {
            // 恢复数值 + 只删**临时档**（真实存档完全没被碰过；见 StatsTable.探针_覆盖存盘路径）
            try { System.IO.File.Delete(StatsTable.探针_存盘路径); } catch { /* 忽略 */ }
            StatsTable.探针_覆盖存盘路径 = null;
            StatsTable.探针_设值(_原心情);
            GD.Print($"[ST] 已恢复数值并清理临时存档（真实档未动）→ {StatsTable.概述}");
            GD.Print($"[ST] ===== 失败数 = {_失败} =====");
            GD.Print(_失败 == 0 ? "[ST] PASS" : "[ST] FAIL");
            GetTree().Quit(_失败 == 0 ? 0 : 1);
        }
        else if (_帧 > 600)
        {
            GD.PrintErr("[ST] 超时 FAIL");
            GetTree().Quit(2);
        }
    }
}
