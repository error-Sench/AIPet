using Godot;

namespace desktop.script.Util;

/// <summary>
/// 「窗口比例矩形」命中判定：把 VPet `.lps` 的 500 空间坐标（px/py/sw/sh）换算成**窗口宽高的比例**后，
/// 判断窗口内的局部坐标是否落在矩形里。两处共用：捏脸的脸区（`FacePinch`）、单击的身体区（`WindowDrag`）。
/// <para>比例怎么来的：素材 500 空间 → 我们的帧（×0.5116 固定缩放）→ 桌宠窗口（×缩放）。
/// 换算细节与实测校准见 `StateMachine.设置.默认摸身体命中区` 的注释（同一套算法也用在捏脸命中区）。</para>
/// 约定：标识符英文，注释中文（见 AGENTS.md §8）。
/// </summary>
public static class HitRegion
{
    /// <summary>比例矩形命中：`区` = [x0, y0, x1, y1]（0~1 的窗口比例）；区为空/长度不对 → 永不命中。</summary>
    public static bool 命中(float[] 区, Vector2 局部, Vector2I 窗口尺寸)
    {
        if (区 is not { Length: 4 } || 窗口尺寸.X <= 0 || 窗口尺寸.Y <= 0) return false;
        var x0 = 区[0] * 窗口尺寸.X;
        var y0 = 区[1] * 窗口尺寸.Y;
        var x1 = 区[2] * 窗口尺寸.X;
        var y1 = 区[3] * 窗口尺寸.Y;
        return 局部.X >= x0 && 局部.X <= x1 && 局部.Y >= y0 && 局部.Y <= y1;
    }
}
