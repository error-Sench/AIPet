using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using desktop.script.Loader;
using desktop.script.UX;
using Godot;

namespace desktop.script.Logic;

/// <summary>
/// 文件/网址输入管线（2026-09-24 解构合并：原 `FileDrop` + `ClipboardRead` + `IndexMatch` 三文件，
/// 它们本来就是同一条管线拆成三块互调）。职责链：
/// 拖文件到桌宠（`Root.FilesDropped`）/ 窗口内 Ctrl+V 粘贴 → 解析（.lnk 快捷方式 / 目录递归枚举 /
/// 网址拆分）→ 结果写入 `IO.单例.Info`（in/dir/inR/ext/single/host/path）→ 按 mod 索引表匹配可用脚本
/// （单个 / 同扩展名批处理 / 混合扩展名批处理+multi）→ `Dialogue.显示脚本选项` 弹菜单。
/// game.tscn 只挂一个 `FileInput` 节点（原 `File` 下的 FileDrop + ClipboardRead 两节点合一）。
/// 隐私边界见 `document/隐私与权限模型.md`：剪贴板只在**桌宠窗口聚焦时按 Ctrl+V** 才读一次，不自动发 Agent。
/// </summary>
public partial class FileInput : Node
{
    private static FileInput _单例;
    private static int _纯目录;

    public override void _Ready()
    {
        GetTree().Root.FilesDropped += OnFilesDropped;
        _单例 = this;
    }

    // ================= 入口：拖放 / 粘贴 =================

    public static void DropFiles(string[] files) => _单例.OnFilesDropped(files);

    private void OnFilesDropped(string[] files)
    {
        // 关键点 1: 立即让窗口请求焦点
        DisplayServer.WindowMoveToForeground();

        // 关键点 2: 异步执行逻辑，不要阻塞当前的信号回调
        // 尤其是包含 OS.Execute 这种耗时操作时
        _ = ProcessDroppedFilesAsync(files);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // 检查是否按下了 Ctrl (Command)
        var isCtrlPressed = @event is InputEventWithModifiers { CtrlPressed: true };

        // 如果是 macOS，通常使用 Command 键，可以用 CommandOrControlAutoremap
        // bool isCtrlPressed = Input.IsKeyPressed(Key.Ctrl) || Input.IsKeyPressed(Key.Meta);

        if (isCtrlPressed && @event is InputEventKey { Pressed: true, Keycode: Key.V })
        {
            var clipboardContent = DisplayServer.ClipboardGet();
            if (!string.IsNullOrEmpty(clipboardContent))
            {
                ProcessClip(clipboardContent);
                return;
            }
            var flies = GetClipboardFilesWin32();
            if (flies != null)
            {
                DropFiles(flies);
            }
        }
    }

    // ================= 文件解析（拖放路径） =================

    private async Task ProcessDroppedFilesAsync(string[] files)
    {
        var paths = new List<string>();
        var relativePaths = new List<string>();
        var dir = new List<string>();
        // 在后台线程或异步任务中处理 IO 耗时逻辑
        await Task.Run(() =>
        {
            _纯目录 = 1;
            foreach (var path in files)
            {
                var finalPath = path.ToLower();
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    finalPath = GetShortcutTarget(path);
                }

                if (Directory.Exists(finalPath))
                {
                    dir.Add(finalPath);
                    // 获取 finalPath 的上一级目录路径
                    var parentPath = Directory.GetParent(finalPath)?.FullName;
                    if (parentPath != null)
                    {
                        var subFiles = Directory.GetFiles(finalPath, "*.*", SearchOption.AllDirectories);

                        foreach (var file in subFiles)
                        {
                            var attributes = File.GetAttributes(file);
                            if ((attributes & FileAttributes.Hidden) == 0 && (attributes & FileAttributes.System) == 0)
                            {
                                paths.Add(file);

                                // 计算相对于父目录的路径
                                // 例如：如果 parentPath 是 C:\Projects，file 是 C:\Projects\MyApp\data.txt
                                // 结果将是 "MyApp\data.txt"
                                relativePaths.Add(Path.GetRelativePath(parentPath, file));
                            }
                        }
                    }
                }
                else if (File.Exists(finalPath))
                {
                    _纯目录 = 0;
                    paths.Add(finalPath);
                    relativePaths.Add(finalPath.GetFile());
                    GD.Print(finalPath.GetFile());
                }
            }
        });

        var extension = Path.GetExtension(paths[0]);
        if (_纯目录 > 0)
        {
            _纯目录 = dir.Count;
        }
        IO.单例.set("in", paths.ToArray());
        IO.单例.set("dir", dir.ToArray());
        IO.单例.set("inR", relativePaths.ToArray());
        IO.单例.set("ext", extension);
        IO.单例.set("single", paths.Count == 1);
        // 关键点 3: 使用 CallDeferred 或等待一帧后再触发 UI
        // 这确保了操作系统的“拖拽释放”事件已经完全结束
        CallDeferred(nameof(FinalizeProcess), Variant.From(paths.ToArray()));
    }

    private static void FinalizeProcess(string[] paths)
    {
        if (_纯目录 > 0)
        {
            var 额外脚本列表 = new List<可见脚本信息>();
            额外脚本列表.AddRange(_纯目录 == 1
                ? CommandLoader.目录指令列表
                : CommandLoader.目录指令列表.Where(目录脚本 => 目录脚本.multi));
            设置额外脚本列表(额外脚本列表);
            _纯目录 = 0;
        }
        处理路径(paths, IndexLoader.文件索引映射, IndexLoader.文件脚本映射, IndexLoader.文件通用脚本列表, "file-ask");
    }

    [SuppressMessage("Interoperability", "CA1416:验证平台兼容性")]
    private static string GetShortcutTarget(string lnkPath)
    {
        try
        {
            // 创建 COM 类型
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            // ReSharper disable once AssignNullToNotNullAttribute
            dynamic shell = Activator.CreateInstance(shellType);

            // 创建快捷方式对象
            // ReSharper disable once PossibleNullReferenceException
            var shortcut = shell.CreateShortcut(lnkPath);
            string targetPath = shortcut.TargetPath;

            // 释放 COM 资源
            Marshal.ReleaseComObject(shortcut);
            Marshal.ReleaseComObject(shell);

            return string.IsNullOrEmpty(targetPath) ? lnkPath : targetPath;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"解析快捷方式失败: {ex.Message}");
            return lnkPath;
        }
    }

    // ================= 剪贴板解析（粘贴路径） =================

    private void ProcessClip(string content)
    {
        var urls = content.Split("\n");
        var input = new List<string>();
        var hosts = new List<string>();
        var pathAndQuerys = new List<string>();
        foreach (var url in urls)
        {
            if (GetUrl(url, out var host, out var path))
            {
                input.Add(url);
                hosts.Add(host);
                pathAndQuerys.Add(path);
            }
        }

        if (input.Count == 0)
        {
            IO.单例.set("in", new[] { content });
            Dialogue.显示脚本选项(CommandLoader.文本指令列表, Tr("txt-ask"));
            return;
        }
        IO.单例.set("in", input.ToArray());
        IO.单例.set("host", hosts.ToArray());
        IO.单例.set("path", pathAndQuerys.ToArray());
        IO.单例.set("ext", hosts[0]);
        IO.单例.set("single", input.Count == 1);
        处理路径(hosts.ToArray(), IndexLoader.网址索引映射, IndexLoader.网址脚本映射, IndexLoader.网址通用脚本列表, "web-ask");
    }

    private static bool GetUrl(string url, out string host, out string path)
    {
        // 初始化输出变量
        host = string.Empty;
        path = string.Empty;

        // 1. 尝试解析字符串为绝对 URI
        // UriKind.Absolute 确保字符串包含协议头（如 http:// 或 https://）
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri uriResult))
        {
            // 2. 验证协议是否为常见的 Web 协议（可选，根据需求增加）
            if (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps)
            {
                host = uriResult.Host;

                // 3. 提取主机名之后的完整路径（包含 Query 参数）
                // PathAndQuery 会返回类似 "/index.html?id=1" 的内容
                path = uriResult.PathAndQuery;

                return true;
            }
        }
        return false;
    }

    #region Win32 剪贴板文件读取
    [DllImport("user32.dll")]
    static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")]
    static extern bool CloseClipboard();
    [DllImport("user32.dll")]
    static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("shell32.dll")]
    static extern uint DragQueryFile(IntPtr hDrop, uint iFile, System.Text.StringBuilder lpszFile, uint cch);
    private const uint CfHdrop = 15;
    private string[] GetClipboardFilesWin32()
    {
        var files = new List<string>();
        if (!OpenClipboard(IntPtr.Zero)) return files.ToArray();

        try
        {
            var hDrop = GetClipboardData(CfHdrop);
            if (hDrop != IntPtr.Zero)
            {
                var count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < count; i++)
                {
                    var sb = new System.Text.StringBuilder(260);
                    DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                    files.Add(sb.ToString());
                }
            }
        }
        finally
        {
            CloseClipboard();
        }
        return files.ToArray();
    }
    #endregion

    // ================= 索引匹配（原 IndexMatch） =================

    private static List<可见脚本信息> _额外脚本列表;

    private static void 设置额外脚本列表(List<可见脚本信息> 额外脚本)
    {
        _额外脚本列表 = 额外脚本;
    }

    // ReSharper disable once InconsistentNaming
    private static void 处理路径(string[] keys, Dictionary<string, 抬头信息> 索引映射, Dictionary<string, List<索引脚本信息>> 脚本映射, List<索引脚本信息> 通用脚本列表, string ask)
    {
        var extension = Path.GetExtension(keys[0]);
        var 脚本执行 = false;
        if (keys.Length == 1)
        {
            // ReSharper disable once AssignNullToNotNullAttribute
            var 询问 = (IndexLoader.文件索引映射.TryGetValue(extension, out var 抬头信息)) ? 抬头信息.name : ask;
            显示脚本选择(IndexLoader.文件脚本映射.TryGetValue(extension, out var list) ? list : [], 通用脚本列表, 询问);
            脚本执行 = true;
        }
        if (keys.Length > 1)
        {
            // 1. 获取所有不重复的扩展名 (建议统一转小写以防大小写不一致)
            // 使用 HashSet 或 Distinct 过滤重复项
            var distinctExtensions = keys
                .Select(Path.GetExtension)
                .Where(e => !string.IsNullOrEmpty(e))
                .Distinct()
                .ToList();

            // 这里的 extension 变量取的是 distinctExtensions[0] 用于做主要的 Key 查找
            // 因为一个合法的脚本必然挂载在所有涉及的扩展名下，所以查第一个即可
            var keyExtension = distinctExtensions.FirstOrDefault();

            if (keyExtension == null) return; // 防御性编程

            // 2. 判断扩展名是否一致
            if (distinctExtensions.Count == 1)
            {
                // === 场景 A: 扩展名都一样 (例如全是 .jpg) ===
                // 逻辑: 只需要检测 Batch 属性

                if (脚本映射.TryGetValue(keyExtension, out var list))
                {
                    // 筛选出支持 Batch 的脚本
                    var batchScripts = list.Where(s => s.batch).ToList();
                    var 对话 = ((索引映射.TryGetValue(keyExtension, out var headerInfo)) && headerInfo.batch)
                        ? headerInfo.name
                        : ask;
                    显示脚本选择(batchScripts, 通用脚本列表, 对话);
                    脚本执行 = true;
                }
            }
            else
            {
                // === 场景 B: 扩展名不一样 (例如 .jpg 和 .png 混杂) ===
                // 逻辑: 检测 Batch + Multi + Extensions 列表包含关系

                // 优化思路: 我们不需要遍历所有脚本。
                // 如果一个脚本支持处理这些文件，它一定在 keyExtension (第一个扩展名) 的映射列表中。
                if (脚本映射.TryGetValue(keyExtension, out var list))
                {
                    var validScripts = list.Where(s =>
                        s.batch &&      // 必须支持批处理
                        s.multi &&      // 必须支持混用
                        s.extensions != null && // 安全检查
                        // 关键: 脚本支持的 Extensions 列表必须包含当前拖入的所有扩展名
                        // 即: distinctExtensions 是 s.Extensions 的子集
                        distinctExtensions.All(reqExt => s.extensions.Contains(reqExt))
                    ).ToList();
                    var 对话 = ((索引映射.TryGetValue(keyExtension, out var headerInfo)) && headerInfo.batch && headerInfo.multi)
                        ? headerInfo.name
                        : ask;
                    显示脚本选择(validScripts, 通用脚本列表, 对话);
                    脚本执行 = true;
                }
            }
        }

        if (!脚本执行)
        {
            显示脚本选择([], 通用脚本列表, ask);
        }

        Input.FlushBufferedEvents();
    }

    private static void 显示脚本选择(List<索引脚本信息> 脚本列表, List<索引脚本信息> 通用脚本列表, string 对话)
    {
        脚本列表.AddRange(通用脚本列表);
        if (_额外脚本列表 != null)
        {
            _额外脚本列表.AddRange(脚本列表);
            Dialogue.显示脚本选项(_额外脚本列表, 对话);
            _额外脚本列表 = null;
        }
        else
        {
            Dialogue.显示脚本选项(脚本列表, 对话);
        }
    }
}
