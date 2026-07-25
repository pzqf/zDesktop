namespace zDesktop.Core.Widgets;

/// <summary>
/// 组件实例的运行时设置 — 记录位置、大小、可见性
/// 后续可序列化为 JSON 持久化到本地
/// </summary>
public class WidgetSettings
{
    /// <summary>关联的组件类型 Id</summary>
    public string WidgetId { get; set; } = string.Empty;

    /// <summary>
    /// 所属显示器的稳定标识（如 <c>\\.\DISPLAY1</c>）。空表示主显示器。
    /// 设计案 v3.1 §五：不可用索引，插拔顺序变化会让组件错屏。
    /// </summary>
    public string MonitorKey { get; set; } = string.Empty;

    /// <summary>相对所属显示器工作区左上角的 X 坐标（DIP）</summary>
    public double X { get; set; } = 100;

    /// <summary>相对所属显示器工作区左上角的 Y 坐标（DIP）</summary>
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
