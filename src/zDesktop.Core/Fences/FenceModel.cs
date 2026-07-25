namespace zDesktop.Core.Fences;

/// <summary>
/// 分区排序方式
/// </summary>
public enum FenceSortMode
{
    /// <summary>手动 —— 保持用户拖拽形成的顺序</summary>
    Manual = 0,
    Name = 1,
    Type = 2,
    Modified = 3,
}

/// <summary>分区自动入区规则的条件类型</summary>
public enum FenceRuleKind
{
    /// <summary>按扩展名（值形如 ".docx"，不区分大小写）</summary>
    Extension = 0,
    /// <summary>按文件名正则</summary>
    NameRegex = 1,
    /// <summary>修改时间在最近 N 天内（值为天数）</summary>
    ModifiedWithinDays = 2,
}

/// <summary>单条自动入区规则</summary>
public sealed class FenceRule
{
    public FenceRuleKind Kind { get; set; }

    /// <summary>规则取值。Extension 可多值；NameRegex / ModifiedWithinDays 取首值。</summary>
    public List<string> Values { get; set; } = new();
}

/// <summary>
/// 分区矩形 —— **DIP，相对所属显示器工作区左上角**（设计案 v3.1 §五）。
///
/// 不存物理像素、不存全局坐标：换分辨率、换缩放、换主屏都不需要迁移数据。
/// </summary>
public sealed class FenceRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(double px, double py)
        => px >= X && px < Right && py >= Y && py < Bottom;

    public FenceRect Clone() => new() { X = X, Y = Y, Width = Width, Height = Height };

    public override string ToString() => $"({X:F0},{Y:F0} {Width:F0}x{Height:F0})";
}

/// <summary>
/// 一个桌面分区。
/// </summary>
public sealed class Fence
{
    /// <summary>稳定标识 —— 重命名不影响图标归属</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 所属显示器的稳定标识（如 <c>\\.\DISPLAY1</c>）。
    /// 显示器被移除时分区不删除，标记为孤儿并隐藏。
    /// </summary>
    public string MonitorKey { get; set; } = string.Empty;

    public string Name { get; set; } = "新分区";

    /// <summary>标题栏与边框配色，形如 <c>#6c5ce7</c></summary>
    public string Color { get; set; } = "#6c5ce7";

    public FenceRect Rect { get; set; } = new();

    /// <summary>折叠状态 —— 折叠后只显示标题条，组内图标隐藏</summary>
    public bool Collapsed { get; set; }

    public FenceSortMode SortMode { get; set; } = FenceSortMode.Manual;

    public List<FenceRule> Rules { get; set; } = new();
}

/// <summary>
/// 图标归属记录 —— 设计案 v3.1 §4.2 决策 1：
/// 持久化的是「路径 → (分区, 序号)」，**不是绝对坐标**。
/// 坐标每次由分区几何 + 序号实时解算。
/// </summary>
public sealed class FenceAssignment
{
    /// <summary>文件完整路径</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>所属分区 Id</summary>
    public string FenceId { get; set; } = string.Empty;

    /// <summary>分区内序号（决定排布位置）</summary>
    public int Order { get; set; }

    /// <summary>
    /// 用户手动放置标记（§4.2 决策 5）。
    ///
    /// 置位后自动规则不再移动该文件 —— 防止「我明明拖出来了，它又自己跑回去」
    /// 这种最招人恨的行为。
    /// </summary>
    public bool Manual { get; set; }
}

/// <summary>
/// 分区配置根对象 —— 序列化为 <c>%APPDATA%\zDesktop\fences.json</c>
/// </summary>
public sealed class FenceConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public List<Fence> Fences { get; set; } = new();

    public List<FenceAssignment> Assignments { get; set; } = new();

    public DateTime SavedAt { get; set; } = DateTime.Now;
}
