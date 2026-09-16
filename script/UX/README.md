# 界面 / 窗口 / 动画 —— 说明文档

> 本文件是 **`AGENTS.md` 的分册**。AGENTS.md 是总纲（项目定位 / 架构总览 / 目录地图 / 路线图 / 开发约定 / 已知事项），
> **本层的细节与踩坑在这里**。相关代码：`script/UX/`；改动后请跑 `tests/README.md` 里对应的探针。
> 约定：标识符英文，注释中文。

## 面板群铁律（新建/改面板前必读）

| 铁律 | 原因（全部实测） |
|---|---|
| **UI 一律代码构建** | `.tscn` 里中文属性名会**静默失效**（见坑 #1） |
| **每个面板恰好一个自绘 ×** | `Borderless = true` 没有系统标题栏，否则关不掉（见坑 #5） |
| **`embed_subwindows=false` 才有真独立窗口** | 否则是被困在主窗口里的嵌入子窗口，标题栏点击无效（见坑 #5） |
| **弹窗不要设顶** | `AlwaysOnTop` 与 `PopupCentered()` 强制出的 transient 冲突，Windows 直接报错（见坑 #5） |
| **摆位必须做** | 漏了窗口就落屏幕 `(0,0)`（实测：状态窗「跑到屏幕左上角」）→ 统一用 `PanelPlacement.摆在桌宠旁(window)`；**探针也必须断言位置** |
| **接管指针名单要加全** | `WindowDrag.面板接管指针()` 必须包含每个面板，否则在面板上点/拖会误拖桌宠 |
| **重建子节点：先 `RemoveChild` 再 `QueueFree`** | 否则新旧节点叠加（见坑 #2） |
| **跨线程一律 `CallDeferred`** | 后台线程碰节点会报「can only be accessed from main thread」（见坑 #3） |

样式统一走 `MicaTheme`（云母浅色磨砂）；新面板**照抄现有面板的范式**最省事。

## 面板清单

| 面板 | 文件 | 入口 | 备注 |
|---|---|---|---|
| 聊天 | `ChatBox.cs` | 右键桌宠 → 面板 + 下方横排命令栏 | 流式与历史回放**两条路径**都要过滤 Agent 指令块（协议见 `script/Agent/README.md`） |
| 工具栏 | `ToolBar.cs` | 命令栏「工具」 | 扫描 `mods/toolbar/` 生成工具格 |
| 配置窗 | `SettingsWindow.cs` | 命令栏「配置」 | 语言下拉 + 目录（点击打开文件夹） |
| 状态窗 | `StatsWindow.cs` | 命令栏「状态」 | 数值可视化：**mood 用文字状态显示、界面不出现任何数字** + 占位行 |

## 动画池机制（`CharAnim.cs`）

- **`CharAnim` 是动画的唯一交汇点**：状态机（`script/State/`）只下发状态/动画名，不直接碰 `AnimatedSprite2D`。
- **新增池必须改 2 处**：`内置动画组`（否则不预载、播放失败）+ `OnAnimationFinished` 里的池判断（否则播完冻结在末帧）。
- **循环池**在 `循环动画组`（`walk` / `sleep`）：走动 6 帧 0.75s 而一次位移约 1s；睡觉是持续态，循环比「播完重播」更顺滑。
- `PlayNamed(动画名)` 精确播单段（走动的左右方向用它）；`PlayState(池)` 池内随机取一项（语义池用它）。
- 状态机与动画的**唯一交汇点**在 `OnAnimationFinished`：持续态让位给状态机重播，退出动画必须放行（见坑 #14）。

## 踩坑（本层）

### 踩坑 #1

1. **`.tscn` 里不能用中文属性名**。`关闭按钮 = NodePath("...")` 这类中文属性 Godot **不认且静默失败**，`[Export]` 绑定出来的引用是 `null`，表现为「按钮点了没反应」。→ 规则：**UI 一律代码构建**（`new Button{...}` + 手工 `AddChild`），不用 `[Export]` + 场景属性绑定。已按此范式：`ChatBox` / `ToolBar` / `SettingsWindow`。
---

### 踩坑 #2

2. **`QueueFree()` 是延迟释放**。重建子节点列表时若只 `QueueFree()` 不 `RemoveChild()`，旧节点仍在树中，会与新节点叠加（表现为「按钮重复成两份」）。→ 规则：重建前先 `RemoveChild(c)` 再 `c.QueueFree()`。
---

### 踩坑 #3

3. **跨线程不能碰 UI / 节点**。Agent 回调、子进程读取线程都在后台线程，直接访问节点会报 `get_translation_domain()/set_visible() can only be accessed from main thread`。→ 规则：后台线程只改数据，一律 `CallDeferred(nameof(方法), 参数)` 回主线程再碰节点（`CharAnim.PlayState` 已内置该保护）。
---

### 踩坑 #5

5. **无边框窗口没有系统标题栏**。`Window.Borderless = true` 时**不存在**系统的最小化/关闭按钮（已用整屏截图实证），窗口会关不掉。→ 规则：**关闭按钮必须自绘 ×**（`ChatBox` / `ToolBar` / `SettingsWindow` 三处统一此范式）。
   - 附：`window/subwindows/embed_subwindows=false` 才会让子窗口变成**真正的独立 OS 窗口**（否则是被困在主窗口里的嵌入子窗口，标题栏点击无效）。
   - 附：`AlwaysOnTop` 与 transient 冲突 —— `PopupCentered()` 会强制 transient，Windows 下报 `Windows with the 'on top' can't become transient.`。**不要给弹出窗口设置顶**。
---

### 踩坑 #12

12. **「手指命中区」不要加位置类历史约束**。`WindowDrag.IsInValidZone()` 里曾有一条旧补丁：要求鼠标在屏幕**下 2/3**
    （`mousePos.Y >= screenHeight/3`）——那是「窗口远大于角色」时代的产物。窗口现在正好套住角色，精确的窗口矩形判定已足够；
    保留它会让**桌宠靠近屏幕上边缘时完全无法拖拽**（顺带也卡住滚轮缩放，而滚轮缩放本身已按主人决策**删除**：
    运行时缩放会破坏动画链——素材偏移与贴边比例都是按固定缩放导入对齐的），而右键（走 `Context._UnhandledInput`，不经过该判定）却正常，
    所以现象是「点得到但拖不动」。
    - 规则：命中判定只用**窗口矩形**；判定函数拆出「传入坐标」的重载（`在桌宠内(Vector2I)`）以便探针不依赖真实光标。
    - 附带实测：`DisplayServer.WarpMouse` 用的是**窗口相对坐标**（请求全局 960 → 实际落 960+窗口X）；探针要移光标需传 `目标全局 - 窗口位置`。
