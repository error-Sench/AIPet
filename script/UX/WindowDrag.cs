using desktop.script.State;
using desktop.script.logic;
using Godot;

namespace desktop.script.UX;

public partial class WindowDrag : Node
{
    private bool _dragging;
    private bool _isPreparing;
    private Vector2I _dragOffset;
    private Vector2I _pressOrigin;
    
    private const float DragThreshold = 5.0f;

    /// <summary>探针只读：是否已进入「按下待拖拽」状态（判定拖拽能否起手）。</summary>
    public static bool 探针_准备中 { get; private set; }

    /// <summary>探针只读：是否正在拖拽。</summary>
    public static bool 探针_拖拽中 { get; private set; }

    /// <summary>探针只读：最近一次起手判定的结果。</summary>
    public static bool 探针_最近判定通过 { get; private set; } 

    /// <summary>
    /// 指针是否落在桌宠身上（可拖拽 / 可滚轮缩放）。
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
                    // 长按候选：命中「脸」区才成立（照 VPet 官方：长按脸 = 捏脸；一按就跑 = 拖窗口）
                    FacePinch.按下(currentMousePos - DisplayServer.WindowGetPosition(), DisplayServer.WindowGetSize());
                }
            }
            else if (_isPreparing && !_dragging)
            {
                // 准备中：检查移动距离是否达标
                if (currentMousePos.DistanceTo(_pressOrigin) > DragThreshold)
                {
                    FacePinch.取消();   // 动了 → 捏脸候选作废（本次按的是拖拽）
                    // 贴边隐藏中开始拖拽 → **直接让位**（不用「复位」，因为复位会同时让窗口滑向原位，
// 与拖拽同一帧抢窗口位置，表现为「拖不动/往边缘吸」）。让位是瞬时回位且清阶段，交给拖拽独占窗口。
                    if (EdgeHide.占用中) EdgeHide.让位();
                    _dragging = true;
                    _isPreparing = false;
                    // 正式锁定 Offset
                    _dragOffset = currentMousePos - DisplayServer.WindowGetPosition();
                    OnDragStart();
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
            var 单击 = _isPreparing && !_dragging && !捏过;
            var 拖过 = _dragging;   // 必须在 取消桌宠拖拽() 之前取：它会把标志复位
            // 只有「本窗口自己管理的那次按压」才负责收捏脸：探针/其它模块直接调 FacePinch 时，
            // 这里每帧的松手分支会把刚触发的捏脸立刻打断（实测：探针里刚进捏脸就被收掉）
            var 本窗口按着 = _isPreparing || _dragging;
            取消桌宠拖拽();
            if (本窗口按着) FacePinch.松手();       // 捏脸中 → 播退出段（相.按住中 → 直接清）
            if (单击) StateMachine.摸摸();
            // 拖拽结束（非单击）→ 检查是否贴到屏幕边缘：贴中就缩进隐藏（P2 行为层）
            else if (拖过) EdgeHide.检查贴边();
        }

        // 探针只读状态（供 DragProbe 断言起手判定）
        探针_准备中 = _isPreparing;
        探针_拖拽中 = _dragging;
    }
}
