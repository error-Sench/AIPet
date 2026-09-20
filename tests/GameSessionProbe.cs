using desktop.script.Game;
using desktop.script.Mode;
using Godot;

namespace desktop.tests;

/// <summary>
/// 游戏模式探针（headless）：`GameSession` 数据容器 + `ModeManager` 进度存取接线。
/// 覆盖：① 缺档 → 默认进度（且只读不写）② 存盘 schema ③ 重启往返（弄脏内存 → 重载）
/// ④ 坏档 / 高版本档容忍 ⑤ 切换模式自动存盘（ModeManager 两个桩已填）。
/// 隔离：全程走临时档（GameSession.探针_覆盖存盘路径），收尾只删临时档 —— 绝不碰主人的真实游戏存档。
/// 用法：Godot_..._console.exe --headless --path &lt;项目&gt; res://tests/GameSessionProbe.tscn
/// </summary>
public partial class GameSessionProbe : Node
{
    private int _帧;
    private int _失败;

    public override void _Ready()
    {
        // 隔离：先切临时档，再碰 GameSession（不实例化 game.tscn —— 数据容器是纯静态的）
        GameSession.探针_覆盖存盘路径 = "user://probe_game_session_tmp.json";
        try { System.IO.File.Delete(GameSession.探针_存盘路径); } catch { /* 上轮残留，忽略 */ }
        GD.Print("=== GameSessionProbe: 存档走临时档 ===");
    }

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[GS] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[GS] FAIL  {描述}"); }
    }

    private static string 读档文本() => System.IO.File.ReadAllText(GameSession.探针_存盘路径);

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 5: 组_缺档默认(); break;
            case 10: 组_存盘schema(); break;
            case 15: 组_往返(); break;
            case 20: 组_坏档容忍(); break;
            case 25: 组_模式接线(); break;
            case 30: 收尾(); break;
        }
        if (_帧 > 600)
        {
            GD.PrintErr("[GS] 超时 FAIL");
            GetTree().Quit(2);
        }
    }

    // ================= 各组 =================

    private void 组_缺档默认()
    {
        GD.Print("--- A 组：缺档 → 默认进度 ---");
        GameSession.探针_重置载入标志();
        GameSession.载入();
        断言(GameSession.关卡 == GameSession.默认关卡, $"A1 关卡=默认「{GameSession.默认关卡}」（实际 {GameSession.关卡}）");
        断言(GameSession.血量 == GameSession.血量上限, $"A2 血量=满血 {GameSession.血量上限}（实际 {GameSession.血量}）");
        断言(GameSession.道具.Count == 0, $"A3 道具为空（实际 {GameSession.道具.Count}）");
        断言(!System.IO.File.Exists(GameSession.探针_存盘路径), "A4 只读不写：缺档载入不产生文件");
    }

    private void 组_存盘schema()
    {
        GD.Print("--- B 组：存盘 schema ---");
        GameSession.设关卡("level_2");
        GameSession.设血量(30);
        GameSession.加道具("coin");
        GameSession.加道具("coin"); // 去重：重复 id 只记一次
        GameSession.设检查点(123.5f, 45f);
        GameSession.存盘();
        断言(System.IO.File.Exists(GameSession.探针_存盘路径), $"B1 存盘文件已生成（{GameSession.探针_存盘路径}）");
        using var 文档 = System.Text.Json.JsonDocument.Parse(读档文本());
        var r = 文档.RootElement;
        断言(r.TryGetProperty("version", out var v) && v.GetInt32() == GameSession.当前版本, $"B2 version = {GameSession.当前版本}");
        断言(r.TryGetProperty("level", out var lv) && lv.GetString() == "level_2", "B3 level = level_2");
        断言(r.TryGetProperty("hp", out var hp) && hp.GetInt32() == 30, "B4 hp = 30");
        断言(r.TryGetProperty("items", out var it) && it.ValueKind == System.Text.Json.JsonValueKind.Array && it.GetArrayLength() == 1,
            "B5 items 是数组且重复道具只记一次");
        断言(r.TryGetProperty("checkpointX", out var cx) && System.Math.Abs(cx.GetSingle() - 123.5f) < 0.01f, "B6 checkpointX = 123.5");
        断言(r.TryGetProperty("_comment", out _), "B7 含 _comment（文件自说明）");
    }

    private void 组_往返()
    {
        GD.Print("--- C 组：重启往返（弄脏内存 → 重载）---");
        GameSession.设关卡("__脏值__");
        GameSession.设血量(7);
        GameSession.加道具("junk");
        GameSession.探针_重置载入标志();
        GameSession.载入();
        断言(GameSession.关卡 == "level_2" && GameSession.血量 == 30, $"C1 关卡/血量从档里还原（{GameSession.关卡} / {GameSession.血量}）");
        断言(GameSession.道具.Count == 1 && GameSession.有道具("coin") && !GameSession.有道具("junk"), "C2 道具还原且脏道具被清掉");
        断言(System.Math.Abs(GameSession.检查点X - 123.5f) < 0.01f && System.Math.Abs(GameSession.检查点Y - 45f) < 0.01f,
            $"C3 检查点还原（{GameSession.检查点X} / {GameSession.检查点Y}）");
    }

    private void 组_坏档容忍()
    {
        GD.Print("--- D 组：坏档 / 高版本档容忍 ---");
        System.IO.File.WriteAllText(GameSession.探针_存盘路径, "{ 这不是 JSON ");
        GameSession.探针_重置载入标志();
        GameSession.载入();
        断言(GameSession.关卡 == GameSession.默认关卡 && GameSession.血量 == GameSession.血量上限,
            $"D1 坏 JSON → 退回默认进度、不炸（{GameSession.关卡} / {GameSession.血量}）");
        System.IO.File.WriteAllText(GameSession.探针_存盘路径, "{\"version\":999,\"level\":\"future\",\"hp\":1}");
        GameSession.探针_重置载入标志();
        GameSession.载入();
        断言(GameSession.关卡 == GameSession.默认关卡 && GameSession.血量 == GameSession.血量上限,
            $"D2 高版本档不半读 → 退回默认进度（{GameSession.关卡} / {GameSession.血量}）");
    }

    private void 组_模式接线()
    {
        GD.Print("--- E 组：ModeManager 接线（切换模式自动存盘）---");
        GameSession.重置();
        GameSession.设血量(42);
        try { System.IO.File.Delete(GameSession.探针_存盘路径); } catch { /* 确保 E2 的「文件存在」是被这次切换写出来的 */ }
        ModeManager.SwitchMode(ModeManager.Mode.Game);
        断言(ModeManager.CurrentMode == ModeManager.Mode.Game, "E1 切到游戏模式");
        断言(System.IO.File.Exists(GameSession.探针_存盘路径), "E2 切换时自动存盘（ModeManager.SaveProgress → GameSession.存盘）");
        using (var 文档2 = System.Text.Json.JsonDocument.Parse(读档文本()))
            断言(文档2.RootElement.TryGetProperty("hp", out var hp2) && hp2.GetInt32() == 42,
                "E3 存的是当前进度（hp = 42）");
        ModeManager.SwitchMode(ModeManager.Mode.Office);
        断言(ModeManager.CurrentMode == ModeManager.Mode.Office, "E4 切回办公模式（收尾恢复默认态）");
    }

    private void 收尾()
    {
        try { System.IO.File.Delete(GameSession.探针_存盘路径); } catch { /* 忽略 */ }
        GameSession.探针_覆盖存盘路径 = null;
        GameSession.探针_重置载入标志();
        GD.Print($"[GS] ===== 失败数 = {_失败} =====");
        GD.Print(_失败 == 0 ? "[GS] PASS" : "[GS] FAIL");
        GetTree().Quit(_失败 == 0 ? 0 : 1);
    }
}
