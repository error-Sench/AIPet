using System.Collections.Generic;
using desktop.script.Logic;

namespace desktop.script.Soul;

/// <summary>
/// 名字表：桌宠的**显示名**（聊天窗标题 / 说话人标签 / 首启问候都用它）。
/// <para>
/// 设计（2026-09-20 主人定稿）：**名字就是一个配置值** —— 读 `config/config.json` 的「名字」键，
/// 缺省「萝莉丝」。没有独立数据文件、没有开局问名字流程、没有 set_name；要改就改配置。
/// 人格不落程序：人格是 Agent 经 skill 一次性内化的材料，名字不属于人格。
/// </para>
/// 约定：标识符英文，注释中文（见 AIPet-Agent.md §8）。
/// </summary>
public static class NameTable
{
    /// <summary>官方默认名（VPet 主角「萝莉丝」）。</summary>
    public const string 默认名字 = "萝莉丝";

    /// <summary>当前显示名 = 配置值（config/config.json 的「名字」；缺省「萝莉丝」）。</summary>
    public static string 当前名字 => Main.配置信息字典.GetValueOrDefault("名字", 默认名字);
}
