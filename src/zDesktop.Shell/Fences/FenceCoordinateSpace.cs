using zDesktop.Core.Fences;
using zDesktop.Shell.Desktop;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Fences;

/// <summary>
/// 三套坐标系之间的换算（设计案 v3.1 §四）。
///
/// <para>分区功能同时踩在三个坐标系上，混用任意两个都会错位：</para>
/// <list type="number">
/// <item><b>ListView 客户区（物理像素）</b> —— Explorer 的图标坐标</item>
/// <item><b>屏幕/虚拟屏（物理像素）</b> —— Win32 显示器矩形</item>
/// <item><b>显示器工作区相对 DIP</b> —— 分区矩形的持久化格式</item>
/// </list>
///
/// <para><b>M3-B 探针实测结论</b>（双屏，副屏位于主屏左侧）：
/// 桌面只有**一个** SysListView32，其客户区覆盖**整个虚拟屏**
/// （实测窗口矩形 (-1920,0)-(1920,1085)，客户区 3840x1085），
/// 客户区原点在屏幕上的位置恰等于虚拟屏原点。因此：</para>
/// <code>
/// 屏幕坐标 = 客户区坐标 + 虚拟屏原点
/// 工作区 DIP = (屏幕坐标 - 该屏工作区原点) × 96 / 该屏 DPI
/// </code>
///
/// <para>跨屏分区因此不需要多个 ListView，只需按显示器矩形切分同一套坐标。</para>
/// </summary>
public sealed class FenceCoordinateSpace
{
    private readonly List<MonitorInfo> _monitors;

    /// <summary>虚拟屏原点（物理像素）。副屏在主屏左/上时为负值。</summary>
    public int VirtualOriginX { get; }
    public int VirtualOriginY { get; }

    public IReadOnlyList<MonitorInfo> Monitors => _monitors;

    public FenceCoordinateSpace(IReadOnlyList<MonitorInfo> monitors, int virtualOriginX, int virtualOriginY)
    {
        _monitors = monitors.ToList();
        VirtualOriginX = virtualOriginX;
        VirtualOriginY = virtualOriginY;
    }

    /// <summary>按当前系统状态构建</summary>
    public static FenceCoordinateSpace Current()
        => new(MonitorSet.Enumerate(),
               Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN),
               Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN));

    // ===== 客户区 ↔ 屏幕 =====

    /// <summary>ListView 客户区坐标 → 屏幕物理坐标</summary>
    public (int X, int Y) ClientToScreen(IconPoint p)
        => (p.X + VirtualOriginX, p.Y + VirtualOriginY);

    /// <summary>屏幕物理坐标 → ListView 客户区坐标</summary>
    public IconPoint ScreenToClient(int screenX, int screenY)
        => new(screenX - VirtualOriginX, screenY - VirtualOriginY);

    // ===== 显示器归属 =====

    /// <summary>判断屏幕坐标落在哪个显示器上；不在任何显示器内返回 null</summary>
    public MonitorInfo? MonitorAtScreen(int screenX, int screenY)
        => _monitors.FirstOrDefault(m =>
            screenX >= m.Bounds.Left && screenX < m.Bounds.Right &&
            screenY >= m.Bounds.Top && screenY < m.Bounds.Bottom);

    /// <summary>按稳定标识取显示器</summary>
    public MonitorInfo? MonitorByKey(string key)
        => _monitors.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));

    // ===== 分区矩形 ↔ 图标空间 =====

    /// <summary>
    /// 分区矩形（相对显示器工作区的 DIP）→ ListView 客户区物理矩形。
    /// 显示器不存在（已拔掉）时返回 null，调用方应把该分区视为孤儿并隐藏。
    /// </summary>
    public IconRect? FenceToIconSpace(Fence fence)
    {
        var monitor = MonitorByKey(fence.MonitorKey);
        if (monitor == null) return null;

        var dpi = monitor.Dpi;

        // DIP → 该屏物理像素 → 屏幕绝对 → 客户区
        var physX = DpiHelper.ToPhysical(fence.Rect.X, dpi);
        var physY = DpiHelper.ToPhysical(fence.Rect.Y, dpi);
        var physW = DpiHelper.ToPhysical(fence.Rect.Width, dpi);
        var physH = DpiHelper.ToPhysical(fence.Rect.Height, dpi);

        var screenX = monitor.WorkArea.Left + (int)Math.Round(physX);
        var screenY = monitor.WorkArea.Top + (int)Math.Round(physY);
        var client = ScreenToClient(screenX, screenY);

        return new IconRect(client.X, client.Y, (int)Math.Round(physW), (int)Math.Round(physH));
    }

    /// <summary>
    /// ListView 客户区物理矩形 → 分区矩形（相对指定显示器工作区的 DIP）。
    /// 用于把用户拖出的矩形转成可持久化的形式。
    /// </summary>
    public FenceRect IconSpaceToFence(IconRect rect, MonitorInfo monitor)
    {
        var (screenX, screenY) = ClientToScreen(new IconPoint(rect.X, rect.Y));
        var dpi = monitor.Dpi;

        return new FenceRect
        {
            X = DpiHelper.ToDip(screenX - monitor.WorkArea.Left, dpi),
            Y = DpiHelper.ToDip(screenY - monitor.WorkArea.Top, dpi),
            Width = DpiHelper.ToDip(rect.Width, dpi),
            Height = DpiHelper.ToDip(rect.Height, dpi),
        };
    }

    /// <summary>
    /// 图标坐标落在哪个分区内。
    ///
    /// 用于把用户拖拽的落点判成入区/出区（§4.2 决策 5）。
    /// 多个分区重叠时取列表中靠前者。
    /// </summary>
    public Fence? FenceAt(IconPoint iconPos, IReadOnlyList<Fence> fences)
    {
        foreach (var fence in fences)
        {
            if (fence.Collapsed) continue; // 折叠的分区不接收拖入

            var rect = FenceToIconSpace(fence);
            if (rect == null) continue;     // 显示器已拔掉

            if (rect.Value.Contains(iconPos)) return fence;
        }
        return null;
    }
}
