using System;
using System.IO;
using System.Text.Json.Nodes;
using Godot;
using desktop.script.Util;

namespace desktop.tests;

/// <summary>
/// ConfigEditProbe（headless）：配置写回的三个约定 —— **只改目标键**、**保留 `_comment` 与未知键**、
/// **中文不转义**（否则用户没法手改配置）。全程写临时目录（`ConfigEdit.探针_根目录` 重定向），
/// 不碰真实配置文件。
/// <para>场景：`tests/ConfigEditProbe.tscn`。</para>
/// </summary>
public partial class ConfigEditProbe : Node
{
    private int _帧;
    private int _失败;
    private readonly string _根 = Path.Combine(Path.GetTempPath(), "aipet_cfgedit_probe");

    private void 断言(bool 条件, string 描述)
    {
        if (条件) GD.Print($"[CE] PASS  {描述}");
        else { _失败++; GD.PrintErr($"[CE] FAIL  {描述}"); }
    }

    public override void _Ready()
    {
        try { if (Directory.Exists(_根)) Directory.Delete(_根, true); } catch { /* 忽略 */ }
        Directory.CreateDirectory(_根);
        ConfigEdit.探针_根目录 = _根;
        GD.Print("=== ConfigEditProbe: 场景已实例化 ===");
    }

    public override void _Process(double delta)
    {
        _帧++;
        switch (_帧)
        {
            case 5: A组_写回三约定(); break;
            case 9: B组_新建文件(); break;
            case 13: 收尾(); break;
        }
    }

    private void A组_写回三约定()
    {
        GD.Print("--- A 组：只改目标键 / 保留 _comment / 中文不转义 ---");
        const string 相对 = "config/behavior.json";
        var 全 = Path.Combine(_根, "config", "behavior.json");
        Directory.CreateDirectory(Path.GetDirectoryName(全)!);
        File.WriteAllText(全, "{\n  \"启用\": true,\n  \"问候启用\": true,\n  \"测试整数\": 10,\n  \"_comment\": \"中文注释要能保留\"\n}\n");

        ConfigEdit.写(相对, "问候启用", false);
        ConfigEdit.写(相对, "测试整数", 25);
        var 文本 = File.ReadAllText(全);

        断言(文本.Contains("\"问候启用\": false"), "bool 写入生效");
        断言(文本.Contains("\"测试整数\": 25"), "int 写入生效");
        断言(文本.Contains("_comment") && 文本.Contains("中文注释要能保留"), "`_comment` 与中文都保留、未被转义");
        断言(!文本.Contains("\\u"), "没有 \\uXXXX 转义（文件仍可手改）");
        断言(文本.Contains("\"启用\": true"), "没碰的键保持原值（不是整文件重写）");

        var 回读 = JsonNode.Parse(文本) as JsonObject;
        断言(回读?["问候启用"]?.GetValue<bool>() == false, "回读值与写入一致");
        断言(ConfigEdit.读文本(相对, "测试整数") == "25", $"读文本可用（{ConfigEdit.读文本(相对, "测试整数")}）");
        断言(ConfigEdit.读文本(相对, "不存在的键", "兜底") == "兜底", "读不到给兜底");
    }

    private void B组_新建文件()
    {
        GD.Print("--- B 组：目标文件不存在时的兜底路径 ---");
        const string 相对 = "config/tts.json";
        var 全 = Path.Combine(_根, "config", "tts.json");
        断言(!File.Exists(全), "前置：文件还不存在");
        ConfigEdit.写(相对, "启用", true);
        断言(File.Exists(全), "写会创建文件");
        断言(File.ReadAllText(全).Contains("\"启用\": true"), "新文件内容正确");
        断言(ConfigEdit.写路径(相对).StartsWith(_根, StringComparison.OrdinalIgnoreCase), "写路径受探针根目录重定向");

        // double 走一遍（缩放这种小数值）
        ConfigEdit.写("config/pet.json", "缩放", 0.45);
        断言(ConfigEdit.读文本("config/pet.json", "缩放") == "0.45", "double 写入后可读回 0.45");
    }

    private void 收尾()
    {
        ConfigEdit.探针_根目录 = "";
        try { if (Directory.Exists(_根)) Directory.Delete(_根, true); } catch { /* 忽略 */ }
        GD.Print($"[CE] ===== 失败数 = {_失败} =====");
        GetTree().Quit(_失败 == 0 ? 0 : 1);
    }
}
