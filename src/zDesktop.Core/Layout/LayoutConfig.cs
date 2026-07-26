namespace zDesktop.Core.Layout;

/// <summary>
/// 布局配置 — 记录桌面上所有组件的位置/大小/可见性
/// 序列化为 layout.json，重启后恢复桌面布局
/// </summary>
public class LayoutConfig
{
    /// <summary>
    /// 配置版本号。
    /// v4 起每个组件记录所属显示器（<see cref="WidgetLayoutEntry.MonitorKey"/>），
    /// 坐标语义由「主屏绝对坐标」改为「相对所属显示器工作区」。
    /// </summary>
    public const int CurrentVersion = 4;

    /// <summary>配置版本号（用于迁移）</summary>
    public int Version { get; set; } = CurrentVersion;

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

    /// <summary>
    /// 所属显示器的稳定标识（如 <c>\\.\DISPLAY1</c>）。
    /// 空表示主显示器 —— v3 及更早的配置没有此字段，迁移时留空即归主屏。
    /// </summary>
    public string MonitorKey { get; set; } = string.Empty;

    /// <summary>相对所属显示器工作区左上角的 X 坐标（DIP）</summary>
    public double X { get; set; }

    /// <summary>相对所属显示器工作区左上角的 Y 坐标（DIP）</summary>
    public double Y { get; set; }

    /// <summary>宽度</summary>
    public double Width { get; set; }

    /// <summary>高度</summary>
    public double Height { get; set; }

    /// <summary>是否可见</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>是否折叠为标题条。Height 仍记录展开时的高度。</summary>
    public bool Collapsed { get; set; }

    /// <summary>组件配置（键值对，键对应 WidgetConfigField.Key）</summary>
    public Dictionary<string, object?> Config { get; set; } = new();
}
