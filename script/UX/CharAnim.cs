using System;
using System.Linq;
using System.Collections.Generic;
using desktop.script.logic;
using desktop.script.State;
using Godot;

namespace desktop.script.UX;

public partial class CharAnim : AnimatedSprite2D
{
    // 不需要定义额外的 _animatedSprite 变量，因为当前类本身就是 AnimatedSprite2D
    private static CharAnim _单例;
    private static string _退出动画名;
    private static 人物数据 显示人物 => Main.显示人物;
    private const int Idle循环下限 = 8;
    private const int Idle循环上限 = 16;
    private static int _fidget触发次数;
    private static int _idle循环次数;
    private static readonly List<string> 内置动画组 =
        ["idle", "celerate", "drag", "dragup", "dragdown", "fidget",
         // 语义池（P2 导入资产后即自动生效；池目录不存在时加载管线自动跳过，无副作用）
         "think", "say", "work", "listen", "sleep", "walk", "greet", "interact", "move",
         // 贴边隐藏（素材已导入；行为接线见 script/State/README.md）
         "edge_hide",
         // 捏脸（照 VPet 官方：长按脸触发；素材 Pinch/*；表现由 FacePinch 三段自管）
         "pinch",
         "interact_body", "turn", "switch",
         // 生日彩蛋（2026-09-20 组①：VPet BDay 三段；触发在 Main）
         "bday",
         // 爬边（2026-09-20 组②：VPet MOVE/*；表现由 Climb 自管）——crawl 是走动的慢速变体
         "climb", "climb_top", "crawl", "fall",
         // 起跳（2026-09-20 主人点名：素材未到、逻辑先接）——GamePlayer.上升期播 `jump-left/right`，
         // 缺素材自动回退（`jump` → 起跳前姿态）；素材导入后随池自动生效
         "jump",
         // 音乐反应（2026-09-20 组③：VPet Music；包裹段 + MusicSense）
         "music"];

    /// <summary>以「循环模式」加载的池：走动 6 帧（0.75s）而一次位移约 1s；睡觉是持续态，循环比「播完重播」更顺滑。
    /// fall 同理：`-b`（横着下落）在爬边与游戏模式的空中段都当持续姿态用（`-a`/`-c` 段仍被排除，见加载处）。</summary>
    private static readonly List<string> 循环动画组 = ["walk", "sleep", "fall"];
    public override void _Ready()
    {
        // 直接给自己的 AnimationFinished 信号绑定方法
        AnimationFinished += OnAnimationFinished;
        _单例 = this;
        _fidget触发次数 = Random.Shared.Next(Idle循环下限, Idle循环上限 + 1);
        应用外观配置();
    }

    /// <summary>从 config/pet.json 应用桌宠默认大小（窗口尺寸随后由 PetWindow 套住角色）。
    /// **大小只在启动时定**：滚轮缩放已删除（主人决策：缩放会破坏动画链 —— 素材偏移/贴边比例都是按固定缩放导入调好的）。</summary>
    private static void 应用外观配置()
    {
        _缩放 = 0.5f;   // **对齐官方内部 ZoomRatio = 0.5**（官方设置界面显示 1.0）；后续动画都按这个显示标准做
        foreach (var 路径 in Util.ConfigFile.候选("pet.json").Concat(Util.ConfigFile.候选("config/pet.json")))
        {
            try
            {
                if (!System.IO.File.Exists(路径)) continue;
                var txt = System.IO.File.ReadAllText(路径);
                if (string.IsNullOrWhiteSpace(txt)) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(txt);
                if (doc.RootElement.TryGetProperty("缩放", out var s) && s.TryGetSingle(out var sv)) _缩放 = sv;
                break;
            }
            catch (Exception e) { GD.PrintErr($"[CharAnim] 读外观配置失败: {e.Message}"); }
        }
        _缩放 = Math.Clamp(_缩放, 0.25f, 1.5f);
        if (_单例 != null) _单例.Scale = new Vector2(_缩放, _缩放);
        GD.Print($"[CharAnim] 外观: 缩放={_缩放}");
    }

    private static float _缩放 = 0.43f;
    // 注：滚轮调缩放（调整缩放 API）已按主人决策**删除** —— 运行时缩放会破坏动画链（导入时按固定缩放对齐的偏移/贴边比例全部失效）。

    /// <summary>按当前缩放与素材尺寸，把窗口收缩到正好套住角色。</summary>
    private static void 初始化窗口尺寸()
    {
        if (_单例?.SpriteFrames == null) return;
        var 尺寸 = _单例.SpriteFrames.GetFrameTexture(_单例.Animation, 0)?.GetSize()
                   ?? new Vector2(512, 512);
        var s = Mathf.RoundToInt(Math.Max(尺寸.X, 尺寸.Y) * _缩放);
        PetWindow.初始化(s);
        GD.Print($"[CharAnim] 窗口尺寸: 角色 {s}px");
    }

    /// <summary>供 PetWindow 摆放角色位置。</summary>
    public static void 设置位置(Vector2 位置)
    {
        if (_单例 != null) _单例.Position = 位置;
    }
    private void OnAnimationFinished()
    {
        // 游戏模式：办公播完逻辑全关（动画由游戏侧接管；见 script/Game/GamePlayer）
        if (Mode.ModeManager.CurrentMode == Mode.ModeManager.Mode.Game) return;

        // 状态机接管中（think/speak/working/listen/sleep 等持续态）：不回 idle，
        // 由状态机决定是否重播当前状态。这是新旧两套状态逻辑的唯一交汇点（见 AIPet-Agent.md §3）。
        // 「包裹中」：非锁定态的包裹会话（气泡说话）段推进也归状态机管（见 StateMachine 包裹段）。
        // 例外：退出动画必须放行，否则 case "exit" 永不触发、程序关不掉。
        if ((StateMachine.接管中 || StateMachine.包裹中) && Animation.ToString() != _退出动画名)
        {
            StateMachine.重播当前状态();
            return;
        }

        // 排队的交互反应（如摸摸）在此生效：等这次动画播完再切，避免观感割裂。
        // 必须放在下面 switch 之前，否则会先被 CharAnim 自己的逻辑切回 idle，产生闪跳。
        if (StateMachine.尝试应用排队状态()) return;

        var 人物数据 = 显示人物;
        var 动画数据 = 人物数据.动画信息映射[Animation];
        var 动画类型 = 动画数据.Type;
        switch (动画类型)
        {
            case "enter":
                SpriteFrames.RemoveAnimation(动画数据.name);//入场动画无用了
                _idle循环次数 = 0;
                进入状态("idle");
                StateMachine.入场完成(); // 入场门解除 → 打一次启动招呼
                break;
            case "drag":
            case "dragup":
                进入状态("drag");
                break;
            case "celerate":
            case "dragdown":
            case "fidget":
                _idle循环次数 = 0;
                进入状态("idle");
                break;
            case "idle":
                _idle循环次数++;
                if (_idle循环次数>=_fidget触发次数)
                {
                    _fidget触发次数 = Random.Shared.Next(Idle循环下限, Idle循环上限 + 1);
                    进入状态("fidget");
                }
                else
                {
                    进入状态("idle");
                }
                break;
            case "exit":
                Main.游戏结束();
                GetTree().Quit();
                break;
        }
    }
    public static void 播放退出动画()
    {
        if (_单例 == null) return;
        StateMachine.准备退出(); // 解除状态锁，否则退出动画的播完回调被接管逻辑吞掉 → 关不掉
        _单例.Play(_退出动画名);
    }

    /// <summary>该池是否已登记为可播放（见 内置动画组）。未登记的池不会被预载，播放会失败。</summary>
    public static bool 池已注册(string id) => 内置动画组.Contains(id);

    /// <summary>该动画名是否已载入（可播放）。</summary>
    public static bool 有动画(string 动画名) =>
        !string.IsNullOrEmpty(动画名) && _单例?.SpriteFrames?.HasAnimation(动画名) == true;

    /// <summary>当前正在播的动画名（只读，供状态机避免重复重播导致相位重置）。</summary>
    public static string 当前动画名_只读 => _单例?.Animation.ToString() ?? "";

    /// <summary>探针用：查询某动画是否按循环模式加载（包裹段 A/C 必须非循环——循环动画不回「播完」信号）。</summary>
    public static bool 动画循环_只读(string 名)
        => _单例 != null && _单例.SpriteFrames.HasAnimation(名) && _单例.SpriteFrames.GetAnimationLoop(名);

    /// <summary>按动画名精确播放（区别于 PlayState 的「按池随机取一项」）。可跨线程调用。</summary>
    public static void PlayNamed(string 动画名)
    {
        if (_单例 == null || string.IsNullOrEmpty(动画名)) return;
        _单例.CallDeferred(nameof(单例播放指定动画), 动画名);
    }

    private void 单例播放指定动画(string 动画名)
    {
        if (SpriteFrames?.HasAnimation(动画名) == true) Play(动画名);
        else GD.PrintErr($"[CharAnim] 动画不存在: {动画名}");
    }
    public static void 开始庆祝() => 进入状态("celerate");
    public static void 开始拖拽()=>进入状态("dragup");
    public static void 结束拖拽()=>进入状态("dragdown");

    /// <summary>按状态名播放动画（身体层状态机统一入口）。未知状态自动回退 idle。可跨线程调用。</summary>
    public static void PlayState(string state)
    {
        if (_单例 == null) return;
        _单例.CallDeferred(nameof(单例播放状态), state);
    }

    private void 单例播放状态(string state) => 进入状态(state);

    private static void 进入状态(string id)
    {
        if (显示人物.动画池字典.TryGetValue(id,out var list) && list.Count>0)
        {

            _单例.Play(list.列表随机项().name);
        }
        else
        {
            if (id!="idle")
            {
                // ReSharper disable once TailRecursiveCall
                进入状态("idle");
            }
        }   
    }
    public static void 载入人物动画()
    {
        var 人物 = 显示人物;
        var 状态机 = _单例.SpriteFrames;
        var 进入动画 = 人物.动画池字典["enter"].列表随机项();
        var 退出动画 = 人物.动画池字典["exit"].列表随机项();
        加载动画(状态机,进入动画);
        _单例.Play(进入动画.name);//先显示,再加载后面动画
        foreach (var 动画组 in 内置动画组)
        {
            加载动画组(人物,动画组);
        }
        加载动画(状态机,退出动画);
        _退出动画名 = 退出动画.name;
        初始化窗口尺寸(); // 动画就绪 -> 窗口收缩到正好套住角色
    }
    private static void 加载动画组(人物数据 人物,string id)
    {
        if (人物.动画池字典.TryGetValue(id,out var list))
        {
            foreach (var 动画 in list)
            {
                加载动画(_单例.SpriteFrames,动画);
            }
        }
    }
    private static void 加载动画(SpriteFrames 状态机, 动画信息 动画信息) => 加载动画(状态机,动画信息.name,动画信息.Path,动画信息.rate,动画信息.Type);
    private static void 加载动画(SpriteFrames 状态机, string 动画名, string 目录, int 帧率, string 池 = null)
    {
        // 1. 检查目录是否存在 (使用绝对路径)
        if (!DirAccess.DirExistsAbsolute(目录))
        {
            GD.PrintErr($"[错误] 外部目录不存在: {目录}");
            return;
        }
        // 2. 初始化动画轨道
        if (状态机.HasAnimation(动画名))
        {
            状态机.RemoveAnimation(动画名);
        }
        状态机.AddAnimation(动画名);
        状态机.SetAnimationSpeed(动画名, 帧率);
        // 包裹段（组①）的 A/C 段**不能**按循环加载：循环动画不回「播完」信号，进入/退出段会永远卡住。
        // 段名约定 = `-a` / `-c` 结尾（sleep-a / sleep-happy-c / think-nomal-a …）——见 StateMachine 包裹段。
        var 是段 = 动画名.EndsWith("-a", StringComparison.Ordinal) || 动画名.EndsWith("-c", StringComparison.Ordinal);
        状态机.SetAnimationLoop(动画名, 池 != null && 循环动画组.Contains(池) && !是段);

        // 3. 获取所有 PNG 文件
        using var dir = DirAccess.Open(目录);
        dir.ListDirBegin();
    
        var filePaths = new List<string>();
        var fileName = dir.GetNext();

        while (fileName != "")
        {
            if (!dir.CurrentIsDir() && fileName.ToLower().EndsWith(".png"))
            {
                // 注意：这里需要存储完整的绝对路径
                filePaths.Add(目录.PathJoin(fileName));
            }
            fileName = dir.GetNext();
        }

        // 4. 自然排序（防止 frame10 排在 frame2 前面）
        filePaths.Sort();

        // 5. 循环加载外部文件并转为 Texture
        foreach (var path in filePaths)
        {
            // 从磁盘读取字节数据
            var buffer = FileAccess.GetFileAsBytes(path);
            if (buffer == null || buffer.Length == 0) continue;

            // 创建 Image 并加载数据
            var img = new Image();
            var err = img.LoadPngFromBuffer(buffer);
        
            if (err == Error.Ok)
            {
                // 将 Image 转为 Godot 渲染可用的 ImageTexture
                var texture = ImageTexture.CreateFromImage(img);
                状态机.AddFrame(动画名, texture);
            }
            else
            {
                GD.PrintErr($"[解析失败] 无法加载图片: {path}, 错误代码: {err}");
            }
        }
    }
}

public static class 工具库
{    
// 定义一个静态随机数实例，以确保随机性并避免重复实例化
    private static readonly Random DefaultRand = new Random();
    /// <summary>
    /// 从列表中随机获取一项
    /// </summary>
    public static T 列表随机项<T>(this List<T> list, Random rand = null)
    {
        // 如果未传入参数，则使用默认的静态随机数实例
        rand ??= DefaultRand;
        if (list == null || list.Count == 0)
        {
            return default(T);
        }

        return list[rand.Next(list.Count)];
    }
}