namespace zDesktop.Core.Widgets;

/// <summary>
/// 组件缩放模式
/// </summary>
public enum WidgetResizeMode
{
    /// <summary>不可缩放（固定尺寸）</summary>
    None,

    /// <summary>仅可调整高度（宽度锁定）</summary>
    HeightOnly,

    /// <summary>宽高均可调整</summary>
    Both,
}

/// <summary>
/// 组件元数据描述符 — 描述一个组件类型的静态信息
/// </summary>
public sealed class WidgetDescriptor
{
    /// <summary>组件唯一标识（如 "clock"）</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>用户可见名称</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>组件功能描述</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>默认宽度（WPF 逻辑像素）</summary>
    public double DefaultWidth { get; init; } = 240;

    /// <summary>默认高度（WPF 逻辑像素）</summary>
    public double DefaultHeight { get; init; } = 180;

    /// <summary>缩放模式（默认不可缩放 — 桌面组件保持固定尺寸更整洁）</summary>
    public WidgetResizeMode ResizeMode { get; init; } = WidgetResizeMode.None;

    /// <summary>是否允许调整大小（向后兼容 — 等价于 ResizeMode != None）</summary>
    public bool AllowResize
    {
        get => ResizeMode != WidgetResizeMode.None;
        init => ResizeMode = value ? WidgetResizeMode.Both : WidgetResizeMode.None;
    }

    /// <summary>配置字段定义列表（空列表表示该组件无可配置项）</summary>
    public List<WidgetConfigField> ConfigSchema { get; init; } = new();
}
