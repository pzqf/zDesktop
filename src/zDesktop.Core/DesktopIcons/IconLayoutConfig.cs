namespace zDesktop.Core.DesktopIcons;

/// <summary>
/// 桌面图标布局配置 — 记录每个图标的位置
/// 序列化为 icons-layout.json，重启后恢复图标排列
/// </summary>
public class IconLayoutConfig
{
    /// <summary>配置版本号</summary>
    public int Version { get; set; } = 2;

    /// <summary>图标布局条目列表</summary>
    public List<IconLayoutEntry> Icons { get; set; } = new();

    /// <summary>最后保存时间</summary>
    public DateTime SavedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 单个图标的布局条目 — 以 SourcePath 为键记录坐标
/// </summary>
public class IconLayoutEntry
{
    /// <summary>桌面项原始路径（唯一键）</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>桌面坐标 X（逻辑像素）</summary>
    public double X { get; set; }

    /// <summary>桌面坐标 Y（逻辑像素）</summary>
    public double Y { get; set; }
}
