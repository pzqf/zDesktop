namespace zDesktop.Shell.Interop;

/// <summary>
/// DPI 换算的唯一入口（设计案 v3.1 §八）
///
/// Win32 API 返回的一律是**物理像素**，WPF 的 Left/Top/Width/Height 一律是**逻辑像素(DIP)**。
/// 两者混用是 v2.3 实现里覆盖层在高缩放下尺寸翻倍的根因，因此约定：
/// **业务代码禁止手写 DPI 换算，一律走本类。**
///
/// 换算关系：DIP = 物理像素 × 96 / DPI
/// </summary>
public static class DpiHelper
{
    /// <summary>由 DPI 值求缩放比（96 → 1.0，144 → 1.5）</summary>
    public static double ScaleFromDpi(double dpi) => dpi <= 0 ? 1.0 : dpi / Win32.DefaultDpi;

    /// <summary>
    /// 取指定显示器的有效 DPI。失败时回退 96（等价于 100% 缩放，不做换算）。
    /// </summary>
    public static double GetMonitorDpi(IntPtr hMonitor)
    {
        try
        {
            // S_OK == 0
            if (Win32.GetDpiForMonitor(hMonitor, Win32.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                return dpiX;
        }
        catch (DllNotFoundException)
        {
            // shcore.dll 在 Win7 上不存在 —— 回退默认 DPI
        }
        catch (EntryPointNotFoundException)
        {
        }

        return Win32.DefaultDpi;
    }

    /// <summary>
    /// 取窗口所在显示器的有效 DPI。GetDpiForWindow 需 Win10 1607+，
    /// 不可用时退回按窗口所在显示器查询。
    /// </summary>
    public static double GetWindowDpi(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            try
            {
                var dpi = Win32.GetDpiForWindow(hwnd);
                if (dpi > 0) return dpi;
            }
            catch (EntryPointNotFoundException)
            {
            }

            var monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero) return GetMonitorDpi(monitor);
        }

        return Win32.DefaultDpi;
    }

    /// <summary>物理像素 → DIP</summary>
    public static double ToDip(double physical, double dpi) => physical * Win32.DefaultDpi / (dpi <= 0 ? Win32.DefaultDpi : dpi);

    /// <summary>DIP → 物理像素</summary>
    public static double ToPhysical(double dip, double dpi) => dip * (dpi <= 0 ? Win32.DefaultDpi : dpi) / Win32.DefaultDpi;

    /// <summary>
    /// 物理像素矩形 → DIP 矩形。
    /// 用于把 SPI_GETWORKAREA / MONITORINFO 的矩形交给 WPF 窗口定位。
    /// </summary>
    public static (double Left, double Top, double Width, double Height) RectToDip(Win32.RECT rect, double dpi)
    {
        return (
            ToDip(rect.Left, dpi),
            ToDip(rect.Top, dpi),
            ToDip(rect.Width, dpi),
            ToDip(rect.Height, dpi)
        );
    }
}
