using System.Runtime.InteropServices;

namespace zDesktop.Shell.Interop;

/// <summary>桌面壁纸摆放方式</summary>
public enum WallpaperPosition
{
    Center = 0,
    Tile = 1,
    Stretch = 2,
    Fit = 3,
    Fill = 4,
    /// <summary>跨屏 —— 一张图铺满整个虚拟屏</summary>
    Span = 5,
}

/// <summary>
/// <c>IDesktopWallpaper</c>（Windows 8+）。
///
/// <para><b>为什么不用 SPI_SETDESKWALLPAPER</b>：那个 API 只能设置「一张壁纸」，
/// 多显示器下要么拉伸要么跨屏，无法按屏分别设置，也读不出每屏当前用的是哪张图。
/// 分区背景要合成到正确的屏上，必须用这个接口。</para>
///
/// <para>方法顺序即 vtable 顺序，<b>不可调整</b> —— 顺序错了会调用到别的函数。</para>
/// </summary>
[ComImport]
[Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDesktopWallpaper
{
    void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId,
                      [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetMonitorDevicePathAt(uint monitorIndex);

    uint GetMonitorDevicePathCount();

    Win32.RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);

    void SetBackgroundColor(uint color);
    uint GetBackgroundColor();

    void SetPosition(WallpaperPosition position);
    WallpaperPosition GetPosition();

    // 以下为幻灯片相关，本产品不使用，但必须占位以保持 vtable 顺序正确
    void SetSlideshow(IntPtr items);
    IntPtr GetSlideshow();
    void SetSlideshowOptions(int options, uint slideshowTick);
    void GetSlideshowOptions(out int options, out uint slideshowTick);
    void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, int direction);
    int GetStatus();
    void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
}

[ComImport]
[Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")]
public class DesktopWallpaperClass { }

/// <summary>单个显示器的壁纸状态</summary>
/// <param name="MonitorId">Shell 的显示器设备路径（与 <c>\\.\DISPLAY1</c> 不是同一套标识）</param>
/// <param name="Rect">该屏矩形（物理像素）</param>
/// <param name="WallpaperPath">当前壁纸文件路径；未设置时为空</param>
public readonly record struct MonitorWallpaper(string MonitorId, Win32.RECT Rect, string WallpaperPath);

/// <summary>
/// 壁纸读写门面 —— 分区背景合成（设计案 v3.1 §4.3 候选 A）的底座。
///
/// COM 调用一律包 try/catch：壁纸接口在某些精简版/服务器版 Windows 上不可用，
/// 失败时应降级为「分区无背景」而不是崩溃（§七 失败降级矩阵）。
/// </summary>
public sealed class WallpaperSurface : IDisposable
{
    private IDesktopWallpaper? _api;

    /// <summary>接口是否可用</summary>
    public bool IsAvailable => _api != null;

    /// <summary>最近一次失败原因</summary>
    public string? LastError { get; private set; }

    public WallpaperSurface()
    {
        try
        {
            _api = (IDesktopWallpaper)new DesktopWallpaperClass();
        }
        catch (Exception ex)
        {
            LastError = $"IDesktopWallpaper 不可用: {ex.Message}";
            Console.WriteLine($"[Wallpaper] {LastError}");
        }
    }

    /// <summary>当前壁纸摆放方式</summary>
    public WallpaperPosition? GetPosition()
    {
        try { return _api?.GetPosition(); }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    public bool SetPosition(WallpaperPosition position)
    {
        try { _api?.SetPosition(position); return _api != null; }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    /// <summary>枚举各显示器的壁纸状态</summary>
    public List<MonitorWallpaper> Enumerate()
    {
        var result = new List<MonitorWallpaper>();
        if (_api == null) return result;

        try
        {
            var count = _api.GetMonitorDevicePathCount();
            for (uint i = 0; i < count; i++)
            {
                var id = _api.GetMonitorDevicePathAt(i);
                if (string.IsNullOrEmpty(id)) continue;

                Win32.RECT rect = default;
                var path = string.Empty;

                // 已断开的显示器仍会出现在列表里，取矩形会失败 —— 跳过而非中断枚举
                try { rect = _api.GetMonitorRECT(id); } catch { continue; }
                try { path = _api.GetWallpaper(id); } catch { }

                result.Add(new MonitorWallpaper(id, rect, path ?? string.Empty));
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Console.WriteLine($"[Wallpaper] 枚举失败: {ex.Message}");
        }

        return result;
    }

    /// <summary>设置指定显示器的壁纸；monitorId 为 null 表示全部显示器</summary>
    public bool SetWallpaper(string? monitorId, string imagePath)
    {
        if (_api == null) return false;

        try
        {
            _api.SetWallpaper(monitorId, imagePath);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Console.WriteLine($"[Wallpaper] 设置失败 ({monitorId ?? "全部"}): {ex.Message}");
            return false;
        }
    }

    /// <summary>读取指定显示器当前壁纸路径</summary>
    public string? GetWallpaper(string? monitorId)
    {
        try { return _api?.GetWallpaper(monitorId); }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    public void Dispose()
    {
        if (_api != null)
        {
            try { Marshal.ReleaseComObject(_api); } catch { }
            _api = null;
        }
    }
}
