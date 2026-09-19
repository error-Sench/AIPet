using desktop.script.State;
using desktop.script.logic;
using Godot;

namespace desktop.script.UX;

public partial class WindowDrag : Node
{
    private bool _dragging;
    private bool _isPreparing;

    /// <summary>摸身体命中区（窗口比例 x0,y0,x1,y1）；null = 用默认。由 `StateMachine.设置.加载()` 注入。</summary>
    public static float[] 命中区;

    /// <summary>
    /// 默认「身体区」：VPet `.lps` 的 `touchbody: px#166 py#206 sw#163 sh#136`（500 空间）换算成窗口比例。
    /// 换算与捏脸命中区同源（素材 500 空间 → 帧 ×0.5116 → 窗口 ×缩放）：先用实测校准过的捏脸区
    /// （px149,128,sw56,sh59 → [0.315,0.261,0.427,0.379]）反解出「窗口像素 = 素材 × 0.5125 + (4.2, 1.3)」，
    /// 再套到身体区 → 窗口 x 89~173、y 107~176（256 窗口）= 躯干位置，与脸区（y 67~97）不重叠。
    /// 点不准就改 `config/behavior.json` 的 `摸身体命中区`。
    /// </summary>
    public static readonly float[] 默认身体命中区 = [0.349f, 0.417f, 0.675f, 0.689f];

    /// <summary>身体区命中（探针可直接断言；脸区优先由调用方处理）。</summary>
    public static bool 身体区命中(Vector2 局部, Vector2I 窗口尺寸) =>
        desktop.script.Util.HitRegion.命中(命中区 ?? 默认身体命中区, 局部, 窗口尺寸);

    /// <summary>
    /// **拖拽命中区**（窗口比例 x0,y0,x1,y1）：只有从这里起手的按压才能把桌宠拖走
    /// （主人 2026-09-19：「拖拽要加判定区，只有头顶部分才能点击拖拽」）。
    /// 默认 = 窗口顶部 40% = **整个头部（含头顶）**。换算与取舍：官方 `.lps` 的 `touchhead`（摸头区）≈ 窗口 y 0.04~0.39，
    /// 向上放宽到 0 以覆盖头顶尖；官方另有 `touchraised`（拎起区，nomal = 全宽 × 窗口 y 0.105~0.506），
    /// 但它上缘反而低于头顶、下缘到腰，不符合「抓头顶拖」的手感，故取「头部」语义。
    /// <para>**脸区优先**：脸（捏脸命中区）上起手的按压**永远不会变拖拽**；捏脸已触发时移动也**不会**变拖拽。
    /// **贴边隐藏例外**：半截在屏外时不做区限（可见条上按哪儿都得能把它拖回来）。</para>
    /// 微调改 `config/behavior.json` 的 `拖拽命中区`。
    /// </summary>
    public static float[] 拖拽命中区;

    /// <summary>默认「拖拽区」：窗口顶部 40%（整个头部，含头顶）→ 256 窗口里 y ≤ 102。</summary>
    public static readonly float[] 默认拖拽命中区 = [0f, 0f, 1f, 0.40f];

    /// <summary>拖拽区命中（探针可直接断言）。</summary>
    public static bool 拖拽区命中(Vector2 局部, Vector2I 窗口尺寸) =>
        desktop.script.Util.HitRegion.命中(拖拽命中区 ?? 默认拖拽命中区, 局部, 窗口尺寸);

    private Vector2I _dragOffset;
    private Vector2I _pressOrigin;
    private bool _脸区起手;      // 本次按压起手在脸区（捏脸优先 → 永不变拖拽）
    private bool _拖拽区起手;    // 本次按压起手在拖拽区（可拖）
    private bool _移动过;        // 本次按压移动超过阈值（松手不算「摸摸」）

    private const float DragThreshold = 5.0f;

    /// <summary>探针只读：是否已进入「按下待拖拽」状态（判定拖拽能否起手）。</summary>
    public static bool 探针_准备中 { get; private set; }

    /// <summary>探针只读：是否正在拖拽。</summary>
    public static bool 探针_拖拽中 { get; private set; }

    /// <summary>探针只读：最近一次起手判定的结果。</summary>
    public static bool 探针_最近判定通过 { get; private set; } 

    /// <summary>探针只读：最近一次按压的**起手分类**（脸区 / 拖拽区）。</summary>
    public static bool 探针_起手脸区 { get; private set; }
    public static bool 探针_起手拖拽区 { get; private set; }

    /// <summary>
    /// 指针是否落在桌宠身上（窗口矩形判定；拖拽能否起手另见 `拖拽区命中`）。
    /// <para>
    /// **历史遗留约束已移除**：旧逻辑要求 mousePos.Y >= screenHeight/3（鼠标必须在屏幕下 2/3），
    /// 那是「窗口远大于角色」时代的补丁。现在窗口正好套住角色，精确的窗口矩形判定已足够；
    /// 保留那条约束会导致 **桌宠靠近屏幕上边缘时完全无法拖拽**（实测 bug，滚轮缩放同样被卡住）。
    /// </para>
    /// </summary>
    public static bool IsInValidZone() => 在桌宠内(DisplayServer.MouseGetPosition());

    /// <summary>判定任意屏幕坐标是否落在桌宠窗口内（探针可传入指定坐标，避免依赖真实光标）。</summary>
    public static bool 在桌宠内(Vector2I 屏幕坐标)
    {
        var 宠位 = DisplayServer.WindowGetPosition();
        var 宠尺 = DisplayServer.WindowGetSize();
        return 屏幕坐标.X >= 宠位.X && 屏幕坐标.X < 宠位.X + 宠尺.X &&
               屏幕坐标.Y >= 宠位.Y && 屏幕坐标.Y < 宠位.Y + 宠尺.Y;
    }

    /// <summary>独立面板在鼠标下方时，它们是最上层交互目标，桌宠必须完全让出本次指针。</summary>
    private static bool 面板接管指针() => ChatBox.正在接管指针 || ToolBar.正在接管指针 || SettingsWindow.正在接管指针 || StatsWindow.正在接管指针;

    /// <summary>清除桌宠拖拽的残留状态；若已经开始拖动，补播原有的落下动画。</summary>
    private void 取消桌宠拖拽()
    {
        if (_dragging)
        {
            CharAnim.结束拖拽();
            // dragup/dragdown 的表现在 CharAnim 内已处理，这里只同步逻辑状态，避免同一帧两次 Play
            StateMachine.标记状态(StateMachine.Idle);
        }
        _dragging = false;
        _isPreparing = false;
    }

    private void OnDragStart()
    {
        CharAnim.开始拖拽();               // 表现：dragup
        StateMachine.标记状态(StateMachine.Drag); // 逻辑态：拖拽中（禁止自主行为）
        StateMachine.NotifyInteraction("drag");
        Dialogue.关闭标题();
        ChatBox.隐藏(); // 拖动时不带着面板走
        IO.单例.stopAudio();
    }

    public override void _Process(double delta)
    {
        // 对话框/工具栏的拖动使用原生 Window.StartDrag。主桌宠绝不能并行接管同一根指针，
        // 否则会隐藏聊天框，或让桌宠窗口错误地黏在鼠标上。
        if (面板接管指针())
        {
            取消桌宠拖拽();
            FacePinch.取消();       // 面板接管 → 长按候选作废
            探针_准备中 = false;
            探针_拖拽中 = false;
            return;
        }

        // 1. 检测鼠标左键状态
        bool isLeftPressed = Input.IsMouseButtonPressed(MouseButton.Left);

        if (isLeftPressed)
        {
            Vector2I currentMousePos = DisplayServer.MouseGetPosition();

            if (!_dragging && !_isPreparing)
            {
                // 初次按下：检查指针是否落在桌宠身上（不再要求「屏幕下 2/3」）
                探针_最近判定通过 = IsInValidZone();
                if (探针_最近判定通过)
                {
                    _isPreparing = true;
                    _pressOrigin = currentMousePos;
                    _移动过 = false;
                    // 起手分类（决定这一按能不能拖；2026-09-19 主人定「只有头顶部分才能点击拖拽」）：
                    //   贴边隐藏中 → 不做区限（半截在屏外，可见条上按哪儿都得能拖回来）
                    //   脸区起手   → 捏脸优先，**永不变拖拽**
                    //   拖拽区起手 → 可拖
                    var 局部 = currentMousePos - DisplayServer.WindowGetPosition();
                    var 窗口尺寸 = DisplayServer.WindowGetSize();
                    var 贴边中 = EdgeHide.占用中;
                    _脸区起手 = !贴边中 && FacePinch.脸区命中(局部, 窗口尺寸);
                    _拖拽区起手 = 贴边中 || (!_脸区起手 && 拖拽区命中(局部, 窗口尺寸));
                    探针_起手脸区 = _脸区起手;
                    探针_起手拖拽区 = _拖拽区起手;
                    // 长按候选：命中「脸」区才成立（照 VPet 官方：长按脸 = 捏脸）
                    FacePinch.按下(局部, 窗口尺寸);
                }
            }
            else if (_isPreparing && !_dragging)
            {
                // 准备中：检查移动距离是否达标
                if (currentMousePos.DistanceTo(_pressOrigin) > DragThreshold)
                {
                    // 捏脸进行中（已触发）→ 移动**什么也不做**：不拖、不作废。
                    // 官方（MainGrid_MouseMove）在捏脸时同样无动作 —— 修主人实机报的 bug：
                    // 「长按确实触发捏脸，但再拖动时直接进入拖拽状态」。
                    var 捏脸进行中 = FacePinch.当前相 is FacePinch.相.捏脸中 or FacePinch.相.退出中;
                    if (!捏脸进行中)
                    {
                        _移动过 = true;   // 移动过了：松手不算「摸摸」
                        if (_脸区起手)
                        {
                            // 脸区起手 = 捏脸优先：动了只作废长按候选，**永不变拖拽**
                            FacePinch.取消();
                        }
                        else if (_拖拽区起手)
                        {
                            FacePinch.取消();   // 冗余（脸区起手已排除），保留防未来改动
                            // 贴边隐藏中开始拖拽 → **直接让位**（不用「复位」，因为复位会同时让窗口滑向原位，
                            // 与拖拽同一帧抢窗口位置，表现为「拖不动/往边缘吸」）。让位是瞬时回位且清阶段，交给拖拽独占窗口。
                            if (EdgeHide.占用中) EdgeHide.让位();
                            _dragging = true;
                            _isPreparing = false;
                            // 正式锁定 Offset
                            _dragOffset = currentMousePos - DisplayServer.WindowGetPosition();
                            OnDragStart();
                        }
                        // else：起手不在拖拽区（身体/脚下/…）→ **不拖**，什么都不做
                    }
                }
            }
            
            // 3. 执行拖拽：一旦进入拖拽状态，无视区域，直到松手
            if (_dragging)
            {
                DisplayServer.WindowSetPosition(currentMousePos - _dragOffset);
            }
        }
        else
        {
            // 4. 松开鼠标：**按下未越阈值即松手 = 单击（摸摸）**。
            //    判定必须早于 取消桌宠拖拽()（它会复位 _isPreparing/_dragging）。
            //    注意：本分支只在真正的松手路径执行；「面板接管指针」的早退路径不经过这里，
            //    因此不会在操作面板时产生假摸摸。
            var 捏过 = FacePinch.占用中;                    // 长按脸已经变成捏脸 → 这次松手不算「摸摸」
            var 单击 = _isPreparing && !_dragging && !捏过 && !_移动过;   // 移动超过阈值也不补「摸摸」
            var 拖过 = _dragging;   // 必须在 取消桌宠拖拽() 之前取：它会把标志复位
            // 只有「本窗口自己管理的那次按压」才负责收捏脸：探针/其它模块直接调 FacePinch 时，
            // 这里每帧的松手分支会把刚触发的捏脸立刻打断（实测：探针里刚进捏脸就被收掉）
            var 本窗口按着 = _isPreparing || _dragging;
            取消桌宠拖拽();
            if (本窗口按着) FacePinch.松手();       // 捏脸中 → 播退出段（相.按住中 → 直接清）
            if (单击)
            {
                // P10：单击分部位 —— 脸区优先（捏脸那套），再看身体区；都不中当摸头（老行为）
                var 局部 = DisplayServer.MouseGetPosition() - DisplayServer.WindowGetPosition();
                var 窗口尺寸 = DisplayServer.WindowGetSize();
                var 身体 = 身体区命中(局部, 窗口尺寸) && !FacePinch.脸区命中(局部, 窗口尺寸);
                StateMachine.摸摸(身体 ? StateMachine.TouchPart.Body : StateMachine.TouchPart.Head);
            }
            // 拖拽结束（非单击）→ 检查是否贴到屏幕边缘：贴中就缩进隐藏（P2 行为层）
            else if (拖过) EdgeHide.检查贴边();
        }

        // 探针只读状态（供 DragProbe 断言起手判定）
        探针_准备中 = _isPreparing;
        探针_拖拽中 = _dragging;
    }
}
