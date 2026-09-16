using Godot;

namespace desktop.script.Asset;

/// <summary>
/// 桌宠图标集（右键菜单 / 对话选项用的小图标）。
/// 原来由 `resource/icon.tres`（手装配的 .tres 资源）提供，那个目录已删除 ——
/// 现在改为**代码直接加载** `res://icon/*.png`（同名图片仍在），不再依赖 `resource/`。
/// 用法：`IconResource` 字段在 _Ready 里为 null 时会自动填 <see cref="默认"/>。
/// </summary>
[GlobalClass]
public partial class IconResource : Resource
{
	[Export] public Texture2D 关机图标;
	[Export] public Texture2D 目录图标;
	[Export] public Texture2D 复制图标;
	[Export] public Texture2D 剪切图标;
	[Export] public Texture2D 取消图标;
	[Export] public Texture2D 返回图标;

	private static IconResource _默认;

	/// <summary>默认图标集：从 `res://icon/` 直接加载（懒加载，只建一次）。</summary>
	public static IconResource 默认 => _默认 ??= new IconResource
	{
		关机图标 = GD.Load<Texture2D>("res://icon/close.png"),
		目录图标 = GD.Load<Texture2D>("res://icon/openDir.png"),
		复制图标 = GD.Load<Texture2D>("res://icon/copy.png"),
		剪切图标 = GD.Load<Texture2D>("res://icon/cut.png"),
		取消图标 = GD.Load<Texture2D>("res://icon/cancel.png"),
		返回图标 = GD.Load<Texture2D>("res://icon/return.png"),
	};
}
