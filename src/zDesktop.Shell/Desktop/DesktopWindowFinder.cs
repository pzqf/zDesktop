using System.Text;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Desktop;

/// <summary>
/// 桌面图标层窗口查找器
/// 定位 SHELLDLL_DefView（桌面图标容器）和其后的 WorkerW
/// 这是 Z 序控制的基础 — 接管窗口要插入到 SHELLDLL_DefView 上方
/// </summary>
public static class DesktopWindowFinder
{
    /// <summary>
    /// 查找桌面图标层窗口 (SHELLDLL_DefView) 的 HWND
    /// 不同 Windows 版本中，SHELLDLL_DefView 的父窗口可能是 Progman 或 WorkerW
    /// </summary>
    public static IntPtr FindDesktopIconView()
    {
        // 方案1：直接在 Progman 下查找
        var progman = Win32.FindWindow("Progman", null!);
        var defView = Win32.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null!);

        if (defView != IntPtr.Zero)
            return defView;

        // 方案2：遍历 WorkerW 窗口查找（壁纸切换后图标层会移到 WorkerW 下）
        defView = FindDefViewUnderWorkerW();
        return defView;
    }

    /// <summary>
    /// 遍历顶层 WorkerW 窗口，查找包含 SHELLDLL_DefView 的那个
    /// </summary>
    private static IntPtr FindDefViewUnderWorkerW()
    {
        var progman = Win32.FindWindow("Progman", null!);
        IntPtr workerW = IntPtr.Zero;
        IntPtr result = IntPtr.Zero;

        // 枚举 Progman 之后的同级窗口
        Win32.EnumWindowsProc callback = (hWnd, lParam) =>
        {
            var sb = new StringBuilder(256);
            Win32.GetClassName(hWnd, sb, sb.Capacity);

            if (sb.ToString() == "WorkerW")
            {
                // 检查这个 WorkerW 是否有 SHELLDLL_DefView 子窗口
                var child = Win32.FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null!);
                if (child != IntPtr.Zero)
                {
                    result = child;
                    return false; // 找到了，停止枚举
                }
            }
            return true; // 继续枚举
        };

        Win32.EnumWindows(callback, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// 获取桌面图标层的父窗口（Progman 或 WorkerW）
    /// 用于 Z 序定位
    /// </summary>
    public static IntPtr GetDesktopIconParent()
    {
        var defView = FindDesktopIconView();
        if (defView == IntPtr.Zero)
            return Win32.FindWindow("Progman", null!);

        return Win32.GetParent(defView);
    }
}
