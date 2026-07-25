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

    // ===== 虚拟屏（多屏包围盒）=====

    /// <summary>虚拟屏左上角 X。副屏位于主屏左侧时为负值。</summary>
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

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

    // ===== 显示器枚举（多屏支持，设计案 v3.1 §八）=====

    /// <summary>MONITORINFOEXW — 含设备名的显示器信息</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;   // 显示器完整区域（物理像素）
        public RECT rcWork;      // 工作区，已排除任务栏（物理像素）
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice; // 如 "\\\\.\\DISPLAY1"

        public static MONITORINFOEX Create() => new()
        {
            cbSize = Marshal.SizeOf<MONITORINFOEX>(),
            szDevice = string.Empty,
        };
    }

    /// <summary>MONITORINFOF_PRIMARY — 主显示器标志</summary>
    public const uint MONITORINFOF_PRIMARY = 0x00000001;

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcClip, IntPtr dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    // ===== Per-Monitor DPI =====

    /// <summary>MDT_EFFECTIVE_DPI — 含用户缩放设置的有效 DPI</summary>
    public const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>获取指定显示器的 DPI（Win8.1+）</summary>
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>获取窗口所在显示器的 DPI（Win10 1607+）</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>标准 DPI 基准值 —— 96 DPI 对应 100% 缩放</summary>
    public const double DefaultDpi = 96.0;

    /// <summary>DPI 变更消息（PerMonitorV2 下由系统发送）</summary>
    public const int WM_DPICHANGED = 0x02E0;

    // ===== 全屏检测（设计案 v3.1 §二 原则 6）=====

    /// <summary>
    /// 查询用户通知状态 —— 用于判断是否有全屏应用/游戏在运行。
    /// 返回 <see cref="QUNS_RUNNING_D3D_FULL_SCREEN"/> 或 <see cref="QUNS_BUSY"/> 时应隐藏覆盖层。
    /// </summary>
    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out int pquns);

    public const int QUNS_NOT_PRESENT = 1;              // 屏保运行/用户未登录
    public const int QUNS_BUSY = 2;                     // 全屏应用运行中
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;  // 全屏 D3D 应用（游戏）
    public const int QUNS_PRESENTATION_MODE = 4;        // 演示模式
    public const int QUNS_ACCEPTS_NOTIFICATIONS = 5;    // 正常状态
    public const int QUNS_QUIET_TIME = 6;               // 勿扰时段
    public const int QUNS_APP = 7;                      // Windows 应用运行中

    // ===== Explorer 重启自愈 =====

    /// <summary>
    /// 注册窗口消息 —— 用于取得 "TaskbarCreated" 消息 ID。
    /// Explorer 崩溃重启后会向所有顶层窗口广播该消息，是重建覆盖层的可靠信号。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string lpString);

    /// <summary>允许指定消息穿透 UIPI 消息过滤（广播消息接收所需）</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);

    public const uint MSGFLT_ALLOW = 1;

    // ===== 前台窗口变化事件（焦点驱动轮询的基础）=====

    /// <summary>
    /// 前台窗口切换事件。
    ///
    /// 用事件而非定时器查询焦点，是「桌面未聚焦时完全不轮询」（§4.2 决策 4、
    /// §八 空闲态 CPU &lt; 0.1%）能成立的前提 —— 哪怕 1 秒一次的焦点轮询
    /// 也会让空闲态挂着一个常驻定时器。
    /// </summary>
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    /// <summary>回调在本进程外调用，不注入目标进程 —— 对杀软友好</summary>
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    /// <summary>不接收本进程自身产生的事件</summary>
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // ===== 桌面 ListView 跨进程控制（分区功能地基，M2 spike 验证）=====

    private const int LVM_FIRST = 0x1000;

    public const int LVM_GETITEMCOUNT = LVM_FIRST + 4;
    /// <summary>取图标坐标 —— lParam 指向 POINT（须为 explorer 进程内的地址）</summary>
    public const int LVM_GETITEMPOSITION = LVM_FIRST + 16;
    /// <summary>设图标坐标（32 位版，lParam 指向 POINT）。多屏虚拟桌面必须用它而非 16 位打包版。</summary>
    public const int LVM_SETITEMPOSITION32 = LVM_FIRST + 49;
    /// <summary>取表项文本 —— lParam 指向 LVITEMW</summary>
    public const int LVM_GETITEMTEXTW = LVM_FIRST + 115;
    /// <summary>取图标网格间距 —— 返回 MAKELONG(cx, cy)</summary>
    public const int LVM_GETITEMSPACING = LVM_FIRST + 51;
    public const int LVM_GETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 55;

    public const int LVIF_TEXT = 0x0001;

    /// <summary>自动排列图标（窗口样式）—— 开启时分区无法工作</summary>
    public const int LVS_AUTOARRANGE = 0x0100;

    /// <summary>
    /// 将图标与网格对齐（**扩展**样式，GWL_STYLE 里查不到）。
    /// 开启时写入坐标会被吸附到最近格点。
    /// </summary>
    public const int LVS_EX_SNAPTOGRID = 0x00080000;

    /// <summary>
    /// LVITEMW 的 x64 布局。字段顺序必须与 commctrl.h 一致 ——
    /// 错一个字节就会读到垃圾，甚至让 explorer.exe 崩溃。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LVITEMW
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
        public int iGroupId;
        public uint cColumns;
        public IntPtr puColumns;
        public IntPtr piColFmt;
        public int iGroup;
    }

    /// <summary>
    /// 带超时的消息发送。
    /// 跨进程操作**必须**用它而非 SendMessage：Explorer 卡死时
    /// SendMessage 会无限期阻塞调用线程，把 zDesktop 一起拖死。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam,
        uint flags, int timeoutMs, out IntPtr result);

    /// <summary>目标线程挂起时立即返回，不等待超时</summary>
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    // ===== 跨进程内存 =====

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr addr, IntPtr size, uint allocType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr addr, IntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr addr, IntPtr buffer, IntPtr size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr addr, IntPtr buffer, IntPtr size, out IntPtr written);

    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_VM_WRITE = 0x0020;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;

    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint PAGE_READWRITE = 0x04;

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
