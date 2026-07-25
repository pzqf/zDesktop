namespace zDesktop.Core.Widgets;

/// <summary>
/// 组件实例的运行时设置 — 记录位置、大小、可见性
/// 后续可序列化为 JSON 持久化到本地
/// </summary>
public class WidgetSettings
{
    /// <summary>关联的组件类型 Id</summary>
    public string WidgetId { get; set; } = string.Empty;

    /// <summary>桌面坐标 X（逻辑像素）</summary>
    public double X { get; set; } = 100;

    /// <summary>桌面坐标 Y（逻辑像素）</summary>
    public double Y { get; set; } = 100;

    /// <summary>宽度</summary>
    public double Width { get; set; } = 240;

    /// <summary>高度</summary>
    public double Height { get; set; } = 180;

    /// <summary>是否可见</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>组件配置（键值对，键对应 WidgetConfigField.Key，值为 JSON 原始类型）</summary>
    public Dictionary<string, object?> Config { get; set; } = new();
}
