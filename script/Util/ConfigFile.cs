using System.Collections.Generic;
using System.IO;
using Godot;

namespace desktop.script.Util;

/// <summary>
/// 配置文件查找（打包分发关键件）。
/// <para>
/// **为什么需要**：导出后 `res://` 打在 pck 里，**`System.IO` 读不到**（`File.Exists` 直接 false）→
/// 分发包会「配置文件全部读不到」= 没配置的空壳。所以查找顺序要带上**可执行文件同目录**
/// （分发包把 `config/` 放在 exe 旁边即可被读到），并允许用户用 `user://` 覆盖（覆盖优先）。
/// </para>
/// <para>顺序：`user://相对路径` → `<exe 同目录>/相对路径` → `res://相对路径`（最后一个覆盖开发期/编辑器运行）。</para>
/// </summary>
public static class ConfigFile
{
    /// <summary>返回按优先级排列的候选绝对路径（调用方按序试读，能读到就用）。</summary>
    public static IEnumerable<string> 候选(string 相对路径)
    {
        var 规范 = 相对路径.Replace('\\', '/').TrimStart('/');
        // 1. 用户覆盖（user:// 下同名文件，最高优先级：用户改配置不必动安装目录）
        yield return ProjectSettings.GlobalizePath($"user://{规范}");
        // 2. 可执行文件同目录（分发包形态：AIPet.exe 旁边放 mods/ config/）
        var exe目录 = Path.GetDirectoryName(OS.GetExecutablePath());
        if (!string.IsNullOrEmpty(exe目录))
            yield return Path.Combine(exe目录, 规范.Replace('/', Path.DirectorySeparatorChar));
        // 3. res://（开发期；导出后读不到，属预期）
        yield return ProjectSettings.GlobalizePath($"res://{规范}");
    }

    /// <summary>找到第一个真实存在的文件（找不到返回 null）。</summary>
    public static string 找(string 相对路径)
    {
        foreach (var 路径 in 候选(相对路径))
            if (!string.IsNullOrEmpty(路径) && File.Exists(路径)) return 路径;
        return null;
    }
}
