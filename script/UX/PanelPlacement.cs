using System;
using Godot;

namespace desktop.script.UX;

/// <summary>
/// 独立面板的摆位（抽出来共用，且**计算是纯函数** → 探针可以直接断言，不必依赖真实屏幕几何）。
/// <para>
/// 规则：优先摆在桌宠**下方**（水平以桌宠为基准居中）；下方放不下就翻到**上方**；最后夹进可用屏幕区。
/// </para>
/// 教训：状态窗首版漏了摆位 → 窗口落在屏幕 (0,0)（主人实机发现）。**新建独立窗口时，摆位是必做项，
/// 探针也必须断言位置**（首版探针只断言了尺寸，白打印了 `位置=(0,0)` 却没拦住）。
/// 约定：标识符英文，注释中文（见 AGENTS.md §8）。
/// </summary>
public static class PanelPlacement
{
    public const int 贴边间距 = 8;

    /// <summary>纯函数：算出面板应放的屏幕坐标。</summary>
    public static Vector2I 计算(Vector2I 宠位, Vector2I 宠尺, Vector2I 面板尺寸, Rect2I 屏)
    {
        var x = 宠位.X + (宠尺.X - 面板尺寸.X) / 2;
        var y = 宠位.Y + 宠尺.Y + 贴边间距;
        if (y + 面板尺寸.Y > 屏.End.Y) y = 宠位.Y - 面板尺寸.Y - 贴边间距; // 下方放不下 → 上方

        x = Math.Clamp(x, 屏.Position.X + 贴边间距,
            Math.Max(屏.Position.X + 贴边间距, 屏.End.X - 面板尺寸.X - 贴边间距));
        y = Math.Clamp(y, 屏.Position.Y + 贴边间距,
            Math.Max(屏.Position.Y + 贴边间距, 屏.End.Y - 面板尺寸.Y - 贴边间距));
        return new Vector2I(x, y);
    }

    /// <summary>按当前真实几何，把面板摆到桌宠旁边（用窗口自己的 Size）。</summary>
    public static void 摆在桌宠旁(Window 面板)
    {
        if (面板 == null) return;
        面板.Position = 计算(
            DisplayServer.WindowGetPosition(),
            DisplayServer.WindowGetSize(),
            面板.Size,
            DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen()));
    }
}