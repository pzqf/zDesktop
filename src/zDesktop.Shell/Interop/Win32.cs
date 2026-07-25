using System.Runtime.InteropServices;
using System.Text;
using System;

namespace zDesktop.Shell.Interop;

/// <summary>
/// Win32 API 互操作声明 — 桌面接管核心 P/Invoke
/// 对应设计文档"桌面接管方案"中的技术点
/// </summary>
public static class Win32
{
    // ===== 窗口扩展样式 (WS_EX) =====
    public const int WS_EX_LAYERED = 0x00080000;       // 分层窗口，支持 per-pixel alpha
    public const int WS_EX_TOOLWINDOW = 0x00000080;    // 不在任务栏/Alt+Tab 显示
    public const int WS_EX_TRANSPARENT = 0x00000020;   // 鼠标点击透传到下层
    public const int WS_EX_NOACTIVATE = 0x08000000;    // 点击不抢焦点

    // ===== 窗口样式 (WS) =====
    public const int WS_POPUP = unchecked((int)0x80000000);

    // ===== SetWindowPos 标志 =====
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    // ===== 特殊 HWND =====
    public static readonly IntPtr HWND_BOTTOM = new(1);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    // ===== Hit-test 返回值 =====
    public const int HTTRANSPARENT = -1;
    public const int HTCLIENT = 1;

    // ===== 系统参数 =====
    public const int SPI_GETWORKAREA = 0x0030;

    /// <summary>RECT 结构 — 窗口/工作区矩形</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>POINT 结构 — 屏幕坐标</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    // ===== Z 序自愈（WM_WINDOWPOSCHANGING）=====

    /// <summary>窗口位置/Z 序即将变更 — 拦截此消息可否决他人插队</summary>
    public const int WM_WINDOWPOSCHANGING = 0x0046;

    /// <summary>
    /// WINDOWPOS 结构 — WM_WINDOWPOSCHANGING 的 lParam 指向它。
    /// 改写 hwndInsertAfter 即可强制 Z 序落点。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    /// <summary>SWP_NOZORDER — 置位表示本次不改变 Z 序</summary>
    public const uint SWP_NOZORDER = 0x0004;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(int uiAction, int uiParam, ref RECT pvParam, int fWinIni);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SystemParametersInfoString(int uiAction, int uiParam, string pvParam, int fWinIni);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    public static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    public static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetParent(IntPtr hWnd);

    // EnumWindows
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, int nFlags);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    // GWL 常量
    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;

    // ShowWindow 命令
    public const int SW_SHOWNORMAL = 1;
    public const int SW_HIDE = 0;

    /// <summary>
    /// 获取/设置窗口扩展样式（兼容 32/64 位）
    /// </summary>
    public static int GetWindowExStyle(IntPtr hWnd)
    {
        return unchecked((int)(long)GetWindowLongPtr64(hWnd, GWL_EXSTYLE));
    }

    public static int SetWindowExStyle(IntPtr hWnd, int style)
    {
        return unchecked((int)(long)SetWindowLongPtr64(hWnd, GWL_EXSTYLE, new IntPtr(style)));
    }

    // ===== 全局热键 =====

    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int VK_SPACE = 0x20;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ===== 窗口管理 =====

    public const int GW_OWNER = 4;
    public const int SW_RESTORE = 9;
    public const int SW_MINIMIZE = 6;
    public const int SW_MAXIMIZE = 3;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint GW_ENABLEDPOPUP = 6;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("dwmapi.dll", PreserveSig = false)]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int pvAttribute, int cbAttribute);

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_EXCLUDED_FROM_PEEK = 12;
    public const int DWMWA_CLOAK = 13;

    // ===== Shell 执行 =====

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr ShellExecuteW(IntPtr hwnd, string? lpOperation, string lpFile,
        string? lpParameters, string? lpDirectory, int nShowCmd);

    public const int SW_SHOWNORMAL_CMD = 1;

    // ===== 系统性能监控 API =====

    /// <summary>
    /// 获取系统 CPU 时间 — idle / kernel / user
    /// 用于计算 CPU 使用率：1 - Δidle / (Δkernel + Δuser)
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    /// <summary>FILETIME — 100 纳秒间隔计数</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        /// <summary>转为 64 位长整型（单位：100ns）</summary>
        public readonly long Value => ((long)dwHighDateTime << 32) | dwLowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>内存状态结构 — GlobalMemoryStatusEx</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;          // 内存使用率百分比 (0-100)
        public ulong ullTotalPhys;          // 物理内存总量（字节）
        public ulong ullAvailPhys;          // 可用物理内存（字节）
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        /// <summary>初始化时必须设置 dwLength</summary>
        public static MEMORYSTATUSEX Create()
        {
            return new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        }
    }
}
