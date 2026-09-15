using System;
using Godot;

namespace desktop.script.UX;

/// <summary>
/// 桌宠窗口几何：窗口**恒定**正好套住角色（不再随面板撑开/收起，避免角色漂移）。
/// 面板与工具栏都是独立窗口，不需要主窗口变形。
/// 约定：标识符英文，注释中文（见 AGENTS.md §8）。
/// </summary>
public static class PetWindow
{
    /// <summary>角色像素尺寸（正方形边长 = 素材尺寸 × 缩放）。</summary>
    public static int S { get; private set; } = 282;

    /// <summary>由 CharAnim 在动画加载/缩放变化后调用。</summary>
    public static void 初始化(int 角色像素)
    {
        S = Math.Clamp(角色像素, 96, 2048);
        DisplayServer.WindowSetSize(new Vector2I(S, S));
        同步角色();
    }

    /// <summary>角色在窗口内：横向居中、纵向贴顶。</summary>
    public static void 同步角色()
    {
        CharAnim.设置位置(new Vector2(S / 2f, S / 2f));
    }
}