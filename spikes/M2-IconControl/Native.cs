using System.Runtime.InteropServices;
using System.Text;

namespace ZDesktop.Spikes.M2;

/// <summary>
/// M2 spike 所需的 Win32 声明。
///
/// 与 src/ 下的 Win32.cs 分开维护 —— spike 结论未定之前不把这些 API 引入产品代码。
/// </summary>
internal static class Native
{
    // ===== 窗口查找 =====

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? cls, string? win);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    public const int GWL_STYLE = -16;

    // ===== ListView 样式 =====

    public const int LVS_ICON = 0x0000;
    public const int LVS_AUTOARRANGE = 0x0100;
    public const int LVS_ALIGNLEFT = 0x0800;
    public const int LVS_ALIGNTOP = 0x0000;
    public const int LVS_ALIGNMASK = 0x0C00;
    public const int LVS_NOSCROLL = 0x2000;

    // ===== ListView 消息 =====

    private const int LVM_FIRST = 0x1000;

    public const int LVM_GETITEMCOUNT = LVM_FIRST + 4;
    /// <summary>取图标坐标 —— lParam 指向 POINT（需远端内存）</summary>
    public const int LVM_GETITEMPOSITION = LVM_FIRST + 16;
    /// <summary>设图标坐标（16 位打包版，坐标范围受限）</summary>
    public const int LVM_SETITEMPOSITION = LVM_FIRST + 15;
    /// <summary>设图标坐标（32 位版，lParam 指向 POINT，多屏大虚拟桌面必须用它）</summary>
    public const int LVM_SETITEMPOSITION32 = LVM_FIRST + 49;
    /// <summary>取表项文本 —— lParam 指向 LVITEMW（需远端内存）</summary>
    public const int LVM_GETITEMTEXTW = LVM_FIRST + 115;
    public const int LVM_REDRAWITEMS = LVM_FIRST + 21;
    /// <summary>取图标网格间距 —— 返回 MAKELONG(cx, cy)</summary>
    public const int LVM_GETITEMSPACING = LVM_FIRST + 51;
    public const int LVM_GETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 55;
    public const int LVM_SETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 54;
    public const int LVM_ARRANGE = LVM_FIRST + 22;

    /// <summary>
    /// 「将图标与网格对齐」—— 开启时写入的坐标会被吸附到最近格点。
    /// 注意这是**扩展样式**，GWL_STYLE 里查不到，必须走 LVM_GETEXTENDEDLISTVIEWSTYLE。
    /// </summary>
    public const int LVS_EX_SNAPTOGRID = 0x00080000;

    public const int LVIF_TEXT = 0x0001;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
        public override string ToString() => $"({X},{Y})";
    }

    /// <summary>
    /// LVITEMW 的 x64 布局。字段顺序必须与 commctrl.h 一致，
    /// 错一个字节就会读到垃圾或让 explorer 崩溃。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LVITEMW
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;   // x64 下 8 字节，前面有 4 字节对齐填充由 CLR 处理
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
}
