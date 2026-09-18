using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using desktop.script.Asset;
using desktop.script.Loader;
using desktop.script.logic;
using desktop.script.State;
using desktop.script.Util;
using DialogueManagerRuntime;
using Godot;

namespace desktop.script.UX;

public partial class Dialogue : Node
{
    [Export] public PopupMenu 选项菜单;
    [Export] public IconResource IconResource;
    private const float 打字速度 = 20.0f;
    private static Dialogue _单例;
    private static readonly List<可见脚本信息> 脚本列表 = new();
    private static E选项类型 _选项类型 = E选项类型.无;
    private static string _脚本选项标题;
    private static string _当前文本;
    public override void _Ready()
    {
        IconResource ??= desktop.script.Asset.IconResource.默认;   // resource/icon.tres 已删除：图标改由代码直接加载
        // 清理并添加选项
        选项菜单.Clear();
        选项菜单.AddThemeConstantOverride("icon_max_width", 24);
        选项菜单.AddThemeFontSizeOverride("font_size", 24);
        选项菜单.IdPressed += OnMenuItemPressed;
        选项菜单.PopupHide += 关闭标题;
        _单例 = this;
    }
    // ReSharper disable once MemberCanBePrivate.Global 外部调用
    public static void 延迟显示标题(string 文本)
    {
        探针_最近请求文本 = 文本 ?? "";
        Audio.Tts.说(文本);                       // 与气泡一致：延迟显示的话也念（TTS）
        if (_单例 == null) return;                 // 没有 Dialogue 节点（探针/无 UI 场景）→ 只记不发
        _单例.CallDeferred("单例显示标题", 文本);
    }

    /// <summary>显示标题（常驻，直到 `关闭标题()` 或下一条气泡顶掉）。可跨线程调用（内部 marshal 到主线程）。</summary>
    public static void 显示标题(string 文本)
    {
        if (_单例 == null) return;
        _单例.CallDeferred(nameof(单例显示标题), 文本);
    }

    /// <summary>探针：最近一次请求显示的文本（即使当前场景里没有 Dialogue 节点也能断言「它想说什么」）。</summary>
    public static string 探针_最近请求文本 { get; private set; } = "";

    /// <summary>气泡显示时长（秒）——转发 `BubbleWindow`（`config/bubble.json` 的 `气泡显示秒`，默认 4）。</summary>
    public static float 气泡显示秒 => BubbleWindow.气泡显示秒;

    /// <summary>探针：直接设时长（验证「时长可调」与到点自动收）。</summary>
    public static void 探针_设气泡秒(float 秒) => BubbleWindow.探针_设气泡秒(秒);

    /// <summary>读气泡配置（`config/bubble.json`：时长 + 外观）。`Main` 启动时调；缺省 4 秒。</summary>
    public static void 载入配置() => BubbleWindow.载入配置();

    /// <summary>显示临时气泡（到点自动收起）。可跨线程调用（内部 marshal 到主线程）。</summary>
    public static void 显示临时标题(string 文本)
    {
        探针_最近请求文本 = 文本 ?? "";
        // 桌宠"冒泡"的话就用系统语音念出来（TTS；没有可用语音时静默降级，见 script/Audio/Tts.cs）
        Audio.Tts.说(文本);
        if (string.IsNullOrEmpty(文本) || _单例 == null) return;
        _单例.CallDeferred(nameof(单例临时显示), 文本, 气泡显示秒);
    }

    /// <summary>临时气泡的主线程落点：定时由 `BubbleWindow` 管（连发时后一条自然顶掉前一条）。</summary>
    private void 单例临时显示(string 文本, float 显示秒) => 显示气泡(文本, 显示秒);

    /// <summary>常驻气泡的主线程落点：不自动收（等 `关闭标题()`）。</summary>
    private void 单例显示标题(string 原始文本) => 显示气泡(原始文本, 0f);

    /// <summary>
    /// **气泡的唯一出口**（主线程），三件事：<br/>
    /// ① `StateMachine.冒泡说话()` —— 气泡一出现就演「说话」动作（可被打断；忙态白名单不抢）；<br/>
    /// ② 交给 `BubbleWindow`：**按内容自适应尺寸** + 跟随桌宠 + `显示秒` 到点自动收（0 = 常驻）；<br/>
    /// ③ 文本一律**当纯文本**（`[` 会被转义成 `[lb]`，见 `BubbleWindow.装饰后文本`）——不再需要调用方自己防 BBCode。
    /// </summary>
    private void 显示气泡(string 原始文本, float 显示秒)
    {
        if (string.IsNullOrEmpty(原始文本)) return;
        StateMachine.冒泡说话();
        _当前文本 = 原始文本;
        BubbleWindow.显示(Tr(原始文本), 显示秒);
    }

    /// <summary>收起气泡（对话结束 / 选项菜单关闭 / 探针）。没有气泡窗口时也安全。</summary>
    public static void 关闭标题()
    {
        _当前文本 = null;
        BubbleWindow.隐藏();
    }

    /// <summary>当前气泡文本（探针/测试可读）——用于验证 Agent 指令 speak 是否真的落到气泡。</summary>
    public static string 探针_当前标题 => _当前文本 ?? "";

    #region 流式回复（Agent 对话）

    private static readonly System.Text.StringBuilder _流式缓冲 = new();
    private static bool _流式中;

    /// <summary>开始一轮流式回复：清空缓冲、显示气泡。</summary>
    private static void 开始流式()
    {
        _流式缓冲.Clear();
        _流式中 = true;
        CharAnim.PlayState("fidget");
    }

    /// <summary>追加一个流式块（实时显示，模拟打字）。可跨线程调用（内部 marshal 到主线程）。</summary>
    public static void 流式追加(string 块)
    {
        if (string.IsNullOrEmpty(块)) return;
        // 后端回调可能不在主线程；UI 操作必须 marshal 回主线程。
        if (_单例 != null) _单例.CallDeferred(nameof(单例流式追加), 块);
    }

    private void 单例流式追加(string 块)
    {
        if (string.IsNullOrEmpty(块)) return;
        if (!_流式中) 开始流式();
        _流式缓冲.Append(块);
        _当前文本 = _流式缓冲.ToString();
        BubbleWindow.追加(块);      // 不清空、不重放打字机：整段直接可见（BBCode 转义在 BubbleWindow 里统一做）
    }

    /// <summary>流式结束：完成气泡。可跨线程调用。</summary>
    public static void 结束流式()
    {
        if (_单例 == null) { _流式中 = false; return; }
        _单例.CallDeferred(nameof(单例结束流式));
    }

    private void 单例结束流式()
    {
        _流式中 = false;
        if (_流式缓冲.Length == 0) return;
        _当前文本 = _流式缓冲.ToString();
        // 停留后自动收起（沿用临时标题的序列号机制，避免竞态）
        显示临时标题(_当前文本);
        _流式缓冲.Clear();
    }

    #endregion
    public static void 关闭指定标题(string text)
    {
        if (_当前文本 == text)
        {
            关闭标题();
        }
    }
    private void 底部居中显示()
    {
        选项菜单.AddIconItem(IconResource.取消图标,Tr("cancel"),114514);//固定取消,防止退出bug
        var viewportSize = GetViewport().GetVisibleRect().Size;
        var menuSize = 选项菜单.GetContentsMinimumSize();
        var x = (viewportSize.X - menuSize.X) / 2;
        var y = viewportSize.Y * 2 / 3;
        选项菜单.Size = new Vector2I(20, 8);
        选项菜单.MaxSize = new Vector2I(选项菜单.MaxSize.X, (int)(viewportSize.Y/3));
        选项菜单.Position = new Vector2I((int)x, (int)y);
        选项菜单.Show();
    }
    public static void 文件处理完成(string tip)
    {
        _选项类型 = E选项类型.处理完成;
        var 选项菜单 = _单例.选项菜单;
        显示标题(tip);
        选项菜单.Clear();
        选项菜单.AddIconItem(_单例.IconResource.目录图标,_单例.Tr("opendir"),0);
        ShortCutUtil.BindShortCut(选项菜单,0,1);
        var cnt = 1;
        foreach (var 脚本信息 in CommandLoader.展示指令列表)
        {
            var name = _单例.Tr(脚本信息.name);
            _单例.选项菜单.AddIconItem(脚本信息.IconImg,name,cnt);
            ShortCutUtil.BindShortCut( _单例.选项菜单,cnt,cnt+1);
            cnt++;
        }
        _单例.底部居中显示();
    }
    public static void 显示脚本选项<T>(List<T> 新脚本列表,string 询问,bool 配置显示=false) where T : 可见脚本信息
    {
        if(新脚本列表.Count==0)return;
        _选项类型 = E选项类型.脚本;
        脚本列表.Clear();
        脚本列表.AddRange(新脚本列表);
        _脚本选项标题 = 询问;
        显示脚本选项(配置显示);
    }
    private static void 显示脚本选项(bool 配置显示=false)
    {
        if(脚本列表.Count==0)return;
        if(!string.IsNullOrEmpty(_脚本选项标题))显示标题(_脚本选项标题);
        _单例.选项菜单.Clear();
        var cnt = 0;
        foreach (var 脚本信息 in 脚本列表)
        {
            var name = _单例.Tr(脚本信息.name);
            _单例.选项菜单.AddIconItem(脚本信息.IconImg,name,cnt);
            ShortCutUtil.BindShortCut( _单例.选项菜单,cnt,cnt+1);
            cnt++;
        }
        if (配置显示)
        {
            foreach (var 脚本信息 in Main.配置脚本列表)
            {
                var name = _单例.Tr(脚本信息.name)+_单例.Tr("config");
                _单例.选项菜单.AddIconItem(脚本信息.IconImg,name,cnt);
                ShortCutUtil.BindShortCut( _单例.选项菜单,cnt,cnt+1);
                cnt++;
            }
        }
        _单例.底部居中显示();
    }
    private static void OnMenuItemPressed(long id)
    {
        _单例.选项菜单.Hide();
        switch (id)
        {
            case 114514:
                _选项类型 = E选项类型.无;
                脚本结束();
                return;
            case 6174:
                _选项类型 = E选项类型.脚本;
                _单例.CallDeferred(nameof(显示脚本选项));
                return;
            default:
                switch (_选项类型)
                {
                    case E选项类型.无:
                        return;
                    case E选项类型.脚本:
                        if (脚本列表!=null)
                        {
                            if (id < 脚本列表.Count)
                            {
                                Main.选择脚本(脚本列表[(int)id]);
                            }
                            else
                            {
                                Main.打开配置((int)id-脚本列表.Count);
                            }
                        }
                        break;
                    case E选项类型.处理完成:
                        文件处理完成回调((int)id);
                        break;
                    case E选项类型.对话:
                        if (_当前回复列表 != null && _当前回复列表.Count > (int)id)
                        {
                            // 获取点击选项对应的下一个 ID
                            var nextId = _当前回复列表[(int)id].NextId;
                            foreach (var tag in _当前回复列表[(int)id].Tags)
                            {
                                var list = tag.Split("=", 2);
                                IO.单例.set(list[0],list[1]);
                            }
                            进行对话(nextId);
                        }
                        else if (_当前回复列表 == null || _当前回复列表.Count == 0)
                        {
                            对话结束();
                        }
                        break;
                }
                break;
        }
    }

    #region 对话逻辑
    private static Resource _当前对话资源;
    private static Godot.Collections.Array<DialogueResponse> _当前回复列表;
    private static readonly Dictionary<string, Texture2D> IconMap = new();

    public static void 开始对话(Resource 对话资源)
    {
        _当前对话资源 = 对话资源;
        IconMap.Clear();
        进行对话("start");
    }

    private static void 对话结束()
    {
        
        关闭标题(); // 对话结束
        _选项类型 = E选项类型.无;
        Main.对话结束();
    }
    private static async void 进行对话(string 节点)
    {
        try
        {
            // 获取对话行数据
            var line = await DialogueManager.GetNextDialogueLine(_当前对话资源, 节点);
            if (line != null)
            {
                _单例.处理对话行(line);
            }
            else
            {
                对话结束();
            }
        }
        catch (Exception e)
        {
            GD.Print($"进行对话异常:{e.Message}");
        }
    }
    private void 处理对话行(DialogueLine line)
    {
        _选项类型 = E选项类型.对话;
        var 脚本 = Main.当前脚本;
        显示标题(line.Text);
        选项菜单.Clear();
        _当前回复列表 = line.Responses;
        var cnt = 1;
        if (_当前回复列表.Count > 0)
        {
            for (var i = 0; i < _当前回复列表.Count; i++)
            {
                var 选项 = _当前回复列表[i];
                if (选项.IsAllowed)
                {
                    var iconpath = 选项.GetTagValue("icon");
                    if (string.IsNullOrEmpty(iconpath)) iconpath = "icon.png";
                    if (!IconMap.ContainsKey(iconpath))
                    {
                        if (ImageUtil.Loadimage(Path.Combine(脚本.Path,iconpath),out var icon))
                        {
                            IconMap[iconpath] = icon;
                        }
                        else
                        {
                            IconMap[iconpath] = null;
                        }
                    }
                    选项菜单.AddIconItem(IconMap[iconpath],Tr(选项.Text), i);
                    ShortCutUtil.BindShortCut( _单例.选项菜单,i,cnt++);
                }
            }
        }
        else
        {
            选项菜单.AddItem("继续...", 0);
        }

        if (脚本列表.Count>0)选项菜单.AddIconItem(IconResource.返回图标,Tr("return"),6174);
        ShortCutUtil.BindShortCut( _单例.选项菜单,6174,0);
        底部居中显示();
    }
    public static void 脚本结束()
    {
        脚本列表.Clear();
    }
    #endregion
    
    #region 复制剪切

    private static void 文件处理完成回调(int id)
    {
        if (id == 0)
        {
            GD.Print((string)IO.单例.get("out"));
            OS.ShellOpen((string)IO.单例.get("out"));
            IO.单例.Info.Clear();
            return; 
        }
        IO.单例.set("in",IO.单例.get("result"));
        if (id <= CommandLoader.展示指令列表.Count)
        {
            Main.选择脚本( CommandLoader.展示指令列表[id-1]);
        }
    }
    #endregion
}

public enum E选项类型
{
    无,
    脚本,
    处理完成,
    对话
}