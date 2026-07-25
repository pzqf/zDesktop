using System.Runtime.InteropServices;
using System.Text;

namespace ZDesktop.Spikes.M2;

/// <summary>
/// 桌面 SysListView32 的跨进程访问封装。
///
/// 关键约束：SysListView32 属于 explorer.exe，`LVM_GETITEMPOSITION` 等消息的 lParam
/// 是**指针**，指针必须指向 explorer 自己地址空间里的内存。因此需要
/// VirtualAllocEx 在 explorer 进程中分配中转缓冲区，写入参数 → 发消息 → 读回结果。
/// </summary>
internal sealed class DesktopListView : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly IntPtr _process;
    private readonly IntPtr _remote;
    private const int RemoteBufferSize = 4096;

    public IntPtr Handle => _hwnd;
    public uint ProcessId { get; }

    private DesktopListView(IntPtr hwnd, uint pid, IntPtr process, IntPtr remote)
    {
        _hwnd = hwnd;
        ProcessId = pid;
        _process = process;
        _remote = remote;
    }

    /// <summary>定位桌面 ListView 并打开 explorer 进程内存。失败返回 null 并说明原因。</summary>
    public static DesktopListView? Open(out string error)
    {
        error = string.Empty;

        var defView = FindDefView();
        if (defView == IntPtr.Zero)
        {
            error = "未找到 SHELLDLL_DefView —— 当前可能不是交互式桌面会话";
            return null;
        }

        var listView = Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        if (listView == IntPtr.Zero)
        {
            error = "SHELLDLL_DefView 下未找到 SysListView32";
            return null;
        }

        Native.GetWindowThreadProcessId(listView, out var pid);

        var process = Native.OpenProcess(
            Native.PROCESS_VM_OPERATION | Native.PROCESS_VM_READ |
            Native.PROCESS_VM_WRITE | Native.PROCESS_QUERY_INFORMATION,
            false, pid);

        if (process == IntPtr.Zero)
        {
            error = $"OpenProcess 失败（pid={pid}, Win32Error={Marshal.GetLastWin32Error()}）"
                  + " —— 这是 Plan A 被系统加固阻断的典型表现";
            return null;
        }

        var remote = Native.VirtualAllocEx(process, IntPtr.Zero, RemoteBufferSize,
            Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);

        if (remote == IntPtr.Zero)
        {
            error = $"VirtualAllocEx 失败（Win32Error={Marshal.GetLastWin32Error()}）";
            Native.CloseHandle(process);
            return null;
        }

        return new DesktopListView(listView, pid, process, remote);
    }

    private static IntPtr FindDefView()
    {
        // 注意：FindWindow 的 lpWindowName 必须传真正的 null。
        var progman = Native.FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            var dv = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (dv != IntPtr.Zero) return dv;
        }

        // 壁纸切换后图标层可能挂到某个 WorkerW 下
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            var sb = new StringBuilder(64);
            Native.GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() == "WorkerW")
            {
                var dv = Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero) { found = dv; return false; }
            }
            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>图标数量</summary>
    public int Count => (int)Native.SendMessage(_hwnd, Native.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);

    /// <summary>窗口样式（用于判定「自动排列」等）</summary>
    public int Style => (int)(long)Native.GetWindowLongPtr(_hwnd, Native.GWL_STYLE);

    /// <summary>是否开启了「自动排列图标」—— 开启时分区在物理上无法工作</summary>
    public bool IsAutoArrange => (Style & Native.LVS_AUTOARRANGE) != 0;

    /// <summary>扩展样式</summary>
    public int ExtendedStyle =>
        (int)Native.SendMessage(_hwnd, Native.LVM_GETEXTENDEDLISTVIEWSTYLE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>是否开启「将图标与网格对齐」—— 开启时写入坐标会被吸附到最近格点</summary>
    public bool IsSnapToGrid => (ExtendedStyle & Native.LVS_EX_SNAPTOGRID) != 0;

    /// <summary>
    /// 开关「与网格对齐」。返回是否调用成功。
    /// 这是用户可见的桌面设置，spike 中改完必须还原。
    /// </summary>
    public void SetSnapToGrid(bool enabled)
    {
        Native.SendMessage(_hwnd, Native.LVM_SETEXTENDEDLISTVIEWSTYLE,
            new IntPtr(Native.LVS_EX_SNAPTOGRID),
            new IntPtr(enabled ? Native.LVS_EX_SNAPTOGRID : 0));
    }

    /// <summary>图标网格间距（物理像素）。分区内排布必须按它对齐才不会被吸附走。</summary>
    public (int Cx, int Cy) ItemSpacing
    {
        get
        {
            var r = (long)Native.SendMessage(_hwnd, Native.LVM_GETITEMSPACING, IntPtr.Zero, IntPtr.Zero);
            return ((int)(r & 0xFFFF), (int)((r >> 16) & 0xFFFF));
        }
    }

    /// <summary>读取指定图标的坐标（相对 ListView 客户区，物理像素）</summary>
    public Native.POINT GetPosition(int index)
    {
        var pt = new Native.POINT();
        var size = Marshal.SizeOf<Native.POINT>();

        Native.SendMessage(_hwnd, Native.LVM_GETITEMPOSITION, new IntPtr(index), _remote);

        var local = Marshal.AllocHGlobal(size);
        try
        {
            if (Native.ReadProcessMemory(_process, _remote, local, new IntPtr(size), out _))
                pt = Marshal.PtrToStructure<Native.POINT>(local);
        }
        finally
        {
            Marshal.FreeHGlobal(local);
        }

        return pt;
    }

    /// <summary>
    /// 设置图标坐标。用 LVM_SETITEMPOSITION32（POINT 指针版），
    /// 而非 16 位打包版 —— 多屏虚拟桌面坐标可能超出 16 位有符号范围。
    /// </summary>
    public bool SetPosition(int index, int x, int y)
    {
        var pt = new Native.POINT { X = x, Y = y };
        var size = Marshal.SizeOf<Native.POINT>();
        var local = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(pt, local, false);
            if (!Native.WriteProcessMemory(_process, _remote, local, new IntPtr(size), out _))
                return false;

            Native.SendMessage(_hwnd, Native.LVM_SETITEMPOSITION32, new IntPtr(index), _remote);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(local);
        }
    }

    /// <summary>读取图标显示名（用于把「索引」映射到「文件」）</summary>
    public string GetItemText(int index)
    {
        // 远端布局：[0..sizeof(LVITEMW)) 放结构体，其后放文本缓冲区
        var itemSize = Marshal.SizeOf<Native.LVITEMW>();
        var textOffset = (itemSize + 15) & ~15; // 16 字节对齐
        var textCapacity = 260;

        var item = new Native.LVITEMW
        {
            mask = Native.LVIF_TEXT,
            iItem = index,
            iSubItem = 0,
            pszText = _remote + textOffset,
            cchTextMax = textCapacity,
        };

        var local = Marshal.AllocHGlobal(itemSize);
        try
        {
            Marshal.StructureToPtr(item, local, false);
            if (!Native.WriteProcessMemory(_process, _remote, local, new IntPtr(itemSize), out _))
                return "<写入 LVITEM 失败>";

            var len = (int)Native.SendMessage(_hwnd, Native.LVM_GETITEMTEXTW, new IntPtr(index), _remote);
            if (len <= 0) return string.Empty;

            var bytes = Math.Min(len, textCapacity - 1) * 2;
            var textLocal = Marshal.AllocHGlobal(bytes + 2);
            try
            {
                if (!Native.ReadProcessMemory(_process, _remote + textOffset, textLocal, new IntPtr(bytes), out _))
                    return "<读取文本失败>";
                Marshal.WriteInt16(textLocal, bytes, 0);
                return Marshal.PtrToStringUni(textLocal) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(textLocal);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(local);
        }
    }

    public void Dispose()
    {
        if (_remote != IntPtr.Zero)
            Native.VirtualFreeEx(_process, _remote, IntPtr.Zero, Native.MEM_RELEASE);
        if (_process != IntPtr.Zero)
            Native.CloseHandle(_process);
    }
}
