using desktop.script.Agent;
using desktop.script.Soul;
using Godot;

namespace desktop.tests;

/// <summary>
/// 数值层探针（headless，P5）：漂移 / 事件 / 夹取 / 存盘往返 / 离线补算 / Agent 指令接线。
/// 用法：Godot_..._console.exe --headless --path D:/Games/Github/AIPet res://tests/StatsProbe.tscn
/// </summary>
public partial class StatsProbe : Node
{
    private int _帧;
    private int _失败;
    private float _原心情, _原精力, _原亲密;

    public override void _Ready()
    {
        var ps = GD.Load<PackedScene>("res://game.tscn");
        if (ps == null) { GD.PrintErr("game.tscn 加载失败"); GetTree().Quit(1); return; }
        AddChild(ps.Instantiate());
        StatsTable.载入();
        _原心情 = StatsTable.当前心情; _原精力 = StatsTable.当前精力; _原亲密 = StatsTable.当前亲密;
        GD.Print($"=== StatsProbe: 起始 {StatsTable.概述} ===");
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
            GD.Print("--- A 组：漂移 ---");
            // A1 心情高于基线 → 随时间向基线回落
            StatsTable.探针_设值(90f, 80f, 0f);
            StatsTable.探针_漂移(1f, false);
            var a = StatsTable.当前心情;
            断言(a < 90f && a > 60f, $"A1 心情向基线回落（90 → {a:0.0}，介于基线与原值之间）");

            // A2 心情低于基线 → 慢慢好起来
            StatsTable.探针_设值(20f, 80f, 0f);
            StatsTable.探针_漂移(1f, false);
            断言(StatsTable.当前心情 > 20f && StatsTable.当前心情 < 60f,
                $"A2 心情低于基线时回升（20 → {StatsTable.当前心情:0.0}）");

            // A3 清醒耗精力 / 睡眠回充
            StatsTable.探针_设值(60f, 80f, 0f);
            StatsTable.探针_漂移(1f, false);
            var 清醒 = StatsTable.当前精力;
            StatsTable.探针_设值(60f, 80f, 0f);
            StatsTable.探针_漂移(1f, true);
            var 睡眠 = StatsTable.当前精力;
            断言(清醒 < 80f && 睡眠 > 80f, $"A3 清醒耗精力（{清醒:0.0}）/ 睡眠回充（{睡眠:0.0}）");

            GD.Print("--- B 组：事件与节流 ---");
            // B1 摸摸加心情与亲密
            StatsTable.探针_设值(50f, 80f, 0f);
            StatsTable.探针_清节流();
            StatsTable.事件_摸摸();
            var 摸后 = StatsTable.当前心情;
            var 亲后 = StatsTable.当前亲密;
            断言(摸后 > 50f && 亲后 > 0f, $"B1 摸摸加心情（50→{摸后:0.0}）与亲密（0→{亲后:0.0}）");

            // B2 节流内再摸：心情不加，只加亲密
            StatsTable.事件_摸摸();
            断言(近(StatsTable.当前心情, 摸后), $"B2 节流内二次摸摸不加心情（仍 {StatsTable.当前心情:0.0}）");
            断言(StatsTable.当前亲密 > 亲后, "B2 但亲密照加（节流只挡心情）");

            // B3 对话加心情+亲密
            var 对话前 = StatsTable.当前心情;
            StatsTable.事件_对话();
            断言(StatsTable.当前心情 > 对话前, "B3 对话加心情");

            // B4 走动耗精力
            StatsTable.探针_设值(60f, 80f, 0f);
            StatsTable.事件_走动();
            断言(StatsTable.当前精力 < 80f, "B4 走动耗精力");

            GD.Print("--- C 组：夹取与阈值 ---");
            // C1 上限夹取
            StatsTable.探针_设值(999f, 999f, 9999f);
            断言(近(StatsTable.当前心情, 100f) && 近(StatsTable.当前精力, 100f) && 近(StatsTable.当前亲密, 999f),
                $"C1 上限夹取（{StatsTable.概述}）");
            // C2 下限夹取
            StatsTable.探针_设值(-50f, -50f, -50f);
            断言(近(StatsTable.当前心情, 0f) && 近(StatsTable.当前亲密, 0f), $"C2 下限夹取（{StatsTable.概述}）");
            // C3 阈值
            StatsTable.探针_设值(29f, 19f, 0f);
            断言(StatsTable.心情低落 && StatsTable.精力不济, "C3 心情低落/精力不济 阈值判定");
            StatsTable.探针_设值(31f, 21f, 0f);
            断言(!StatsTable.心情低落 && !StatsTable.精力不济, "C3 过阈值后判定解除");

            GD.Print("--- D 组：Agent 指令 set_mood 接线 ---");
            // D1 数值形式
            var (_, c1) = PetCommands.解析("```pet\n{\"cmd\":\"set_mood\",\"mood\":\"85\"}\n```");
            var n1 = PetCommands.执行(c1, out var log1);
            断言(n1 == 1 && 近(StatsTable.当前心情, 85f), $"D1 set_mood=85 生效（日志 {log1[0]}）");
            // D2 关键词形式
            var (_, c2) = PetCommands.解析("```pet\n{\"cmd\":\"set_mood\",\"mood\":\"难过\"}\n```");
            var n2 = PetCommands.执行(c2, out var log2);
            断言(n2 == 1 && 近(StatsTable.当前心情, 20f), $"D2 set_mood=难过 映射到 20（日志 {log2[0]}）");
            // D3 非法值被拒
            var (_, c3) = PetCommands.解析("```pet\n{\"cmd\":\"set_mood\",\"mood\":\"狂暴\"}\n```");
            var n3 = PetCommands.执行(c3, out var log3);
            断言(n3 == 0 && log3[0].Contains("无法识别"), $"D3 无法识别的 mood 被拒（日志 {log3[0]}）");
            // D4 soul_set 仍是「未实现」
            var (_, c4) = PetCommands.解析("```pet\n{\"cmd\":\"soul_set\",\"key\":\"x\",\"value\":\"y\"}\n```");
            var n4 = PetCommands.执行(c4, out var log4);
            断言(n4 == 0 && log4[0].Contains("未实现"), $"D4 soul_set 仍记「未实现」（P3）（日志 {log4[0]}）");

            // D5/D6 状态摘要进 Agent 上下文（P5 第三切片）
            StatsTable.探针_设值(80f, 40f, 7f);
            AgentBridge.Options.InjectStats = true;
            var 提示 = AgentBridge.组装提示("帮我看看这个文件");
            断言(提示.Contains("帮我看看这个文件") && 提示.Contains("心情 80") &&
            提示.Contains("精力 40") && 提示.Contains("亲密 7"),
            $"D5 状态摘要注入（{提示.Replace("\n", " ")}）");
            AgentBridge.Options.InjectStats = false;
            断言(AgentBridge.组装提示("帮我看看这个文件") == "帮我看看这个文件",
                            "D6 关掉 injectStats 后原样发送（完全不注入）");
            AgentBridge.Options.InjectStats = true;

            GD.Print("--- E 组：存盘与离线补算 ---");
            StatsTable.探针_设值(70f, 50f, 12f);
            StatsTable.存盘();
            var 路径 = StatsTable.探针_存盘路径;
            断言(System.IO.File.Exists(路径), $"E1 存盘文件已生成（{路径}）");
            var 文本 = System.IO.File.ReadAllText(路径);
            断言(文本.Contains("\"mood\"") && 文本.Contains("savedAtUnix"), "E2 存盘含数值与时间戳");

            // 模拟「离线 2 小时后启动」：手改时间戳，再走载入路径
            var 旧 = System.Text.Json.JsonDocument.Parse(文本);
            var 心情存 = 旧.RootElement.GetProperty("mood").GetSingle();
            var 改后 = 文本.Replace(旧.RootElement.GetProperty("savedAtUnix").GetRawText(),
                (System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7200).ToString());
            System.IO.File.WriteAllText(路径, 改后);
            StatsTable.探针_重置载入标志();
            StatsTable.载入();
            断言(StatsTable.当前心情 < 心情存,
                $"E3 离线 2h 后载入 → 心情按离线时长回落（{心情存:0.0} → {StatsTable.当前心情:0.0}）");
            断言(StatsTable.当前精力 < 50f, $"E3 离线也耗精力（50 → {StatsTable.当前精力:0.0}）");
        }
        else if (_帧 == 20)
        {
            // 恢复探针开始时的数值并清掉测试文件（不污染主人的存档）
            StatsTable.探针_设值(_原心情, _原精力, _原亲密);
            try { System.IO.File.Delete(StatsTable.探针_存盘路径); } catch { /* 忽略 */ }
            GD.Print($"[ST] 已恢复数值并清理测试存档 → {StatsTable.概述}");
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