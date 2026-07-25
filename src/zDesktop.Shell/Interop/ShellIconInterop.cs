using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace zDesktop.Shell.Interop;

/// <summary>
/// Shell 图标互操作 — 使用 IShellItemImageFactory 获取高分辨率图标
///
/// 相比 Icon.ExtractAssociatedIcon（仅 32x32），此方案可请求 48x48 或更大尺寸，
/// 并自动解析 .lnk 快捷方式的目标图标，清晰度与资源管理器一致
/// </summary>
internal static class ShellIconInterop
{
    // SIIGBF 标志
    private const uint SIIGBF_RESIZETOFIT = 0x0;
    private const uint SIIGBF_BIGGERSIZEOK = 0x1;
    private const uint SIIGBF_ICONONLY = 0x4;

    private static readonly Guid IID_IShellItemImageFactory =
        new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage([In] SIZE size, [In] uint flags, [Out] out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// 获取指定尺寸的高清图标
    /// 对 .lnk / .exe / 文件夹均适用，自动解析快捷方式目标
    /// </summary>
    /// <param name="path">文件/文件夹路径</param>
    /// <param name="size">请求尺寸（逻辑像素，通常 48）</param>
    /// <returns>冻结的 BitmapSource，失败返回 null</returns>
    public static ImageSource? GetIcon(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        try
        {
            var iid = IID_IShellItemImageFactory;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            if (hr != 0 || factory == null)
                return null;

            var sz = new SIZE { cx = size, cy = size };
            hr = factory.GetImage(sz, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out IntPtr hbm);
            if (hr != 0 || hbm == IntPtr.Zero)
                return null;

            // HBITMAP → BitmapSource
            var bmp = Imaging.CreateBitmapSourceFromHBitmap(
                hbm,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            DeleteObject(hbm);
            return bmp;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ShellIconInterop] 获取图标失败 {path}: {ex.Message}");
            return null;
        }
        finally
        {
            if (factory != null)
                Marshal.ReleaseComObject(factory);
        }
    }
}
