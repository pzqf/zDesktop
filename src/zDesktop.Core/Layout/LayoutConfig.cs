namespace zDesktop.Core.Layout;

/// <summary>
/// 布局配置 — 记录桌面上所有组件的位置/大小/可见性
/// 序列化为 layout.json，重启后恢复桌面布局
/// </summary>
public class LayoutConfig
{
    /// <summary>配置版本号（用于未来迁移）</summary>
    public int Version { get; set; } = 3;

    /// <summary>组件布局条目列表</summary>
    public List<WidgetLayoutEntry> Widgets { get; set; } = new();

    /// <summary>最后保存时间</summary>
    public DateTime SavedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 单个组件的布局条目
/// </summary>
public class WidgetLayoutEntry
{
    /// <summary>组件类型 Id（对应 WidgetDescriptor.Id）</summary>
    public string WidgetId { get; set; } = string.Empty;

    /// <summary>桌面坐标 X（逻辑像素）</summary>
    public double X { get; set; }

    /// <summary>桌面坐标 Y（逻辑像素）</summary>
    public double Y { get; set; }

    /// <summary>宽度</summary>
    public double Width { get; set; }

    /// <summary>高度</summary>
    public double Height { get; set; }

    /// <summary>是否可见</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>组件配置（键值对，键对应 WidgetConfigField.Key）</summary>
    public Dictionary<string, object?> Config { get; set; } = new();
}
