using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Desktop;

/// <summary>
/// 单个显示器的信息快照
///
/// 坐标一律以**物理像素**保存（Win32 原样），交给 WPF 前必须经 <see cref="DpiHelper"/> 换算。
/// </summary>
public sealed class MonitorInfo
{
    /// <summary>
    /// 稳定标识 —— 取设备名（如 <c>\\.\DISPLAY1</c>）。
    ///
    /// 设计案 v3.1 §五：**不可用索引做 key**，插拔顺序变化会导致索引漂移，
    /// 配置里记录的组件/分区归属会错屏。
    /// </summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>显示器完整区域（物理像素）</summary>
    public Win32.RECT Bounds { get; init; }

    /// <summary>工作区，已排除任务栏（物理像素）</summary>
    public Win32.RECT WorkArea { get; init; }

    /// <summary>是否主显示器</summary>
    public bool IsPrimary { get; init; }

    /// <summary>该显示器的有效 DPI（96 = 100% 缩放）</summary>
    public double Dpi { get; init; } = Win32.DefaultDpi;

    /// <summary>缩放比（1.0 = 100%）</summary>
    public double Scale => DpiHelper.ScaleFromDpi(Dpi);

    /// <summary>工作区换算为 DIP —— 直接用于 WPF 窗口定位</summary>
    public (double Left, double Top, double Width, double Height) WorkAreaDip
        => DpiHelper.RectToDip(WorkArea, Dpi);

    public override string ToString()
        => $"{Key}{(IsPrimary ? "(主)" : "")} {WorkArea.Width}x{WorkArea.Height}@{Scale:P0}";
}

/// <summary>
/// 显示器集合枚举 —— 多屏支持的基础（设计案 v3.1 §八）
///
/// v2.3 实现只取 <c>SPI_GETWORKAREA</c>（主屏工作区），副屏完全没有覆盖层；
/// 叠加当时默认隐藏原生图标层的行为，副屏会变成一块空白桌面。
/// </summary>
public static class MonitorSet
{
    /// <summary>
    /// 枚举当前所有显示器。始终返回至少一项：枚举失败时回退为
    /// 基于 <c>SPI_GETWORKAREA</c> 的单屏描述，保证调用方无需处理空集合。
    /// </summary>
    public static List<MonitorInfo> Enumerate()
    {
        var result = new List<MonitorInfo>();

        try
        {
            Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Win32.RECT _, IntPtr _) =>
            {
                var mi = Win32.MONITORINFOEX.Create();
                if (Win32.GetMonitorInfo(hMonitor, ref mi))
                {
                    result.Add(new MonitorInfo
                    {
                        Key = string.IsNullOrEmpty(mi.szDevice) ? $"MONITOR-{result.Count}" : mi.szDevice,
                        Bounds = mi.rcMonitor,
                        WorkArea = mi.rcWork,
                        IsPrimary = (mi.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0,
                        Dpi = DpiHelper.GetMonitorDpi(hMonitor),
                    });
                }
                return true; // 继续枚举
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonitorSet] 枚举显示器失败: {ex.Message}");
        }

        if (result.Count == 0)
        {
            Console.WriteLine("[MonitorSet] 枚举结果为空，回退单屏模式");
            result.Add(FallbackPrimary());
        }

        Console.WriteLine($"[MonitorSet] 检测到 {result.Count} 个显示器: {string.Join(", ", result)}");
        return result;
    }

    /// <summary>取主显示器；没有标记为主的则取第一个</summary>
    public static MonitorInfo Primary(List<MonitorInfo> monitors)
        => monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];

    /// <summary>枚举失败时的兜底 —— 用 SPI_GETWORKAREA 构造单屏描述</summary>
    private static MonitorInfo FallbackPrimary()
    {
        var rect = new Win32.RECT();
        Win32.SystemParametersInfo(Win32.SPI_GETWORKAREA, 0, ref rect, 0);
        return new MonitorInfo
        {
            Key = "PRIMARY-FALLBACK",
            Bounds = rect,
            WorkArea = rect,
            IsPrimary = true,
            Dpi = Win32.DefaultDpi,
        };
    }
}
