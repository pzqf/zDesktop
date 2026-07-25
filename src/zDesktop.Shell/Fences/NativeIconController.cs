using System.Runtime.InteropServices;
using System.Text;
using zDesktop.Core.Fences;
using zDesktop.Shell.Desktop;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Fences;

/// <summary>
/// 原生桌面图标坐标控制器 —— 分区功能的地基（设计案 v3.1 §四）。
///
/// <para>由 M2 spike 验证可行（结论见 <c>spikes/M2-IconControl/结论.md</c>）：
/// 非管理员权限下可对 explorer.exe 的 SysListView32 跨进程读写图标坐标，
/// 从而在**完全不接管图标层**的前提下实现分区 —— 图标仍由 Explorer 渲染，
/// 回收站/多选/F2/互拖等原生能力一个不少。</para>
///
/// <para><b>相对 spike 的三处硬化</b>：
/// 1. 全部 SendMessage 改为 <c>SendMessageTimeout</c> —— Explorer 卡死时不能把我们一起拖住；
/// 2. 句柄失效（Explorer 重启）时自动重新获取；
/// 3. 远端缓冲区一次分配长期复用，避免每次读写都 VirtualAllocEx。</para>
/// </summary>
public sealed class NativeIconController : IDisposable
{
    /// <summary>SendMessage 超时（毫秒）。Explorer 无响应时宁可放弃本次操作，也不能阻塞 UI 线程。</summary>
    private const int MessageTimeoutMs = 2000;

    private const int RemoteBufferSize = 4096;

    private IntPtr _listView;
    private IntPtr _process;
    private IntPtr _remote;
    private uint _processId;

    /// <summary>当前是否已就绪（句柄有效且远端缓冲区已分配）</summary>
    public bool IsReady => _listView != IntPtr.Zero && _process != IntPtr.Zero && _remote != IntPtr.Zero;

    /// <summary>最近一次失败原因（供 UI 提示与降级判断）</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 建立与桌面 ListView 的连接。可重复调用（Explorer 重启后重连）。
    /// </summary>
    public bool Connect()
    {
        Disconnect();

        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
        {
            LastError = "未找到桌面 SysListView32";
            return false;
        }

        Win32.GetWindowThreadProcessId(listView, out var pid);

        var process = Win32.OpenProcess(
            Win32.PROCESS_VM_OPERATION | Win32.PROCESS_VM_READ |
            Win32.PROCESS_VM_WRITE | Win32.PROCESS_QUERY_INFORMATION,
            false, pid);

        if (process == IntPtr.Zero)
        {
            LastError = $"OpenProcess 失败（pid={pid}, err={Marshal.GetLastWin32Error()}）—— " +
                        "系统加固或安全软件可能阻断了跨进程访问";
            return false;
        }

        var remote = Win32.VirtualAllocEx(process, IntPtr.Zero, new IntPtr(RemoteBufferSize),
            Win32.MEM_COMMIT | Win32.MEM_RESERVE, Win32.PAGE_READWRITE);

        if (remote == IntPtr.Zero)
        {
            LastError = $"VirtualAllocEx 失败（err={Marshal.GetLastWin32Error()}）";
            Win32.CloseHandle(process);
            return false;
        }

        _listView = listView;
        _process = process;
        _remote = remote;
        _processId = pid;
        LastError = null;

        Console.WriteLine($"[IconController] 已连接 SysListView32=0x{listView.ToInt64():X} (explorer pid={pid})");
        return true;
    }

    /// <summary>句柄失效时自动重连；返回是否可用</summary>
    public bool EnsureConnected()
    {
        if (IsReady && Win32.IsWindow(_listView)) return true;
        return Connect();
    }

    private void Disconnect()
    {
        if (_remote != IntPtr.Zero && _process != IntPtr.Zero)
            Win32.VirtualFreeEx(_process, _remote, IntPtr.Zero, Win32.MEM_RELEASE);
        if (_process != IntPtr.Zero)
            Win32.CloseHandle(_process);

        _listView = IntPtr.Zero;
        _process = IntPtr.Zero;
        _remote = IntPtr.Zero;
        _processId = 0;
    }

    /// <summary>定位桌面 ListView（Progman 下，或壁纸切换后挂到某个 WorkerW 下）</summary>
    private static IntPtr FindDesktopListView()
    {
        var defView = DesktopWindowFinder.FindDesktopIconView();
        return defView == IntPtr.Zero
            ? IntPtr.Zero
            : Win32.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null!);
    }

    /// <summary>带超时的消息发送。超时返回 false，调用方按「本次操作失败」处理。</summary>
    private bool Send(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (!IsReady) return false;

        return Win32.SendMessageTimeout(_listView, msg, wParam, lParam,
            Win32.SMTO_ABORTIFHUNG, MessageTimeoutMs, out result) != IntPtr.Zero;
    }

    // ===== 状态查询 =====

    /// <summary>桌面图标数量；失败返回 0</summary>
    public int Count => Send(Win32.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero, out var r) ? (int)r : 0;

    /// <summary>
    /// 「自动排列图标」是否开启 —— 设计案 §4.2 决策 2 的**硬前置条件**。
    /// 开启时 Explorer 会强制重排全部图标，分区在物理上不可能工作。
    /// </summary>
    public bool IsAutoArrange
    {
        get
        {
            if (!IsReady) return false;
            var style = (int)(long)Win32.GetWindowLongPtr64(_listView, Win32.GWL_STYLE);
            return (style & Win32.LVS_AUTOARRANGE) != 0;
        }
    }

    /// <summary>
    /// 「将图标与网格对齐」是否开启。
    ///
    /// 注意这是**扩展样式**，GWL_STYLE 里查不到 —— M2 spike 最初误判为「写入失败」
    /// 就是漏查了它。我们不关闭该选项（那是改用户可见设置），而是把坐标算在格点上。
    /// </summary>
    public bool IsSnapToGrid
        => Send(Win32.LVM_GETEXTENDEDLISTVIEWSTYLE, IntPtr.Zero, IntPtr.Zero, out var r)
           && ((int)r & Win32.LVS_EX_SNAPTOGRID) != 0;

    /// <summary>
    /// 图标网格间距（物理像素）。
    /// **必须运行时查询**：随 DPI 与图标大小变化，硬编码必然在别的机器上错位。
    /// 查询失败时回退到 Windows 常见默认值，保证上层不会拿到 0 而除零。
    /// </summary>
    public (int Cx, int Cy) ItemSpacing
    {
        get
        {
            if (Send(Win32.LVM_GETITEMSPACING, IntPtr.Zero, IntPtr.Zero, out var r))
            {
                var v = (long)r;
                var cx = (int)(v & 0xFFFF);
                var cy = (int)((v >> 16) & 0xFFFF);
                if (cx > 0 && cy > 0) return (cx, cy);
            }
            return (76, 100);
        }
    }

    // ===== 坐标读写 =====

    /// <summary>读取指定图标坐标（物理像素，ListView 客户区坐标系）</summary>
    public IconPoint? GetPosition(int index)
    {
        if (!IsReady) return null;
        if (!Send(Win32.LVM_GETITEMPOSITION, new IntPtr(index), _remote, out _)) return null;

        var size = Marshal.SizeOf<Win32.POINT>();
        var local = Marshal.AllocHGlobal(size);
        try
        {
            if (!Win32.ReadProcessMemory(_process, _remote, local, new IntPtr(size), out _))
                return null;

            var pt = Marshal.PtrToStructure<Win32.POINT>(local);
            return new IconPoint(pt.X, pt.Y);
        }
        finally
        {
            Marshal.FreeHGlobal(local);
        }
    }

    /// <summary>
    /// 写入图标坐标。
    /// 用 <c>LVM_SETITEMPOSITION32</c>（POINT 指针版）而非 16 位打包版 ——
    /// 多屏虚拟桌面坐标可能超出 16 位有符号范围。
    /// </summary>
    public bool SetPosition(int index, IconPoint p)
    {
        if (!IsReady) return false;

        var pt = new Win32.POINT { X = p.X, Y = p.Y };
        var size = Marshal.SizeOf<Win32.POINT>();
        var local = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(pt, local, false);
            if (!Win32.WriteProcessMemory(_process, _remote, local, new IntPtr(size), out _))
                return false;

            return Send(Win32.LVM_SETITEMPOSITION32, new IntPtr(index), _remote, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(local);
        }
    }

    /// <summary>读取图标显示名</summary>
    public string GetDisplayName(int index)
    {
        if (!IsReady) return string.Empty;

        var itemSize = Marshal.SizeOf<Win32.LVITEMW>();
        var textOffset = (itemSize + 15) & ~15;   // 16 字节对齐
        const int textCapacity = 260;

        var item = new Win32.LVITEMW
        {
            mask = Win32.LVIF_TEXT,
            iItem = index,
            iSubItem = 0,
            pszText = _remote + textOffset,
            cchTextMax = textCapacity,
        };

        var local = Marshal.AllocHGlobal(itemSize);
        try
        {
            Marshal.StructureToPtr(item, local, false);
            if (!Win32.WriteProcessMemory(_process, _remote, local, new IntPtr(itemSize), out _))
                return string.Empty;

            if (!Send(Win32.LVM_GETITEMTEXTW, new IntPtr(index), _remote, out var lenPtr))
                return string.Empty;

            var len = (int)lenPtr;
            if (len <= 0) return string.Empty;

            var bytes = Math.Min(len, textCapacity - 1) * 2;
            var textLocal = Marshal.AllocHGlobal(bytes + 2);
            try
            {
                if (!Win32.ReadProcessMemory(_process, _remote + textOffset, textLocal, new IntPtr(bytes), out _))
                    return string.Empty;
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

    /// <summary>
    /// 一次性读出全部图标的显示名与坐标。
    /// 焦点驱动轮询（§4.2 决策 4）每次调用它做一次全量对账。
    /// </summary>
    public List<DesktopIconSnapshot> ReadAll()
    {
        var result = new List<DesktopIconSnapshot>();
        if (!EnsureConnected()) return result;

        var count = Count;
        for (var i = 0; i < count; i++)
        {
            var pos = GetPosition(i);
            if (pos == null) continue;
            result.Add(new DesktopIconSnapshot(i, GetDisplayName(i), pos.Value));
        }

        return result;
    }

    /// <summary>批量写入坐标；返回成功写入的项数</summary>
    public int SetPositions(IEnumerable<(int Index, IconPoint Point)> items)
    {
        if (!EnsureConnected()) return 0;

        var ok = 0;
        foreach (var (index, point) in items)
        {
            if (SetPosition(index, point)) ok++;
        }
        return ok;
    }

    public void Dispose() => Disconnect();
}

/// <summary>单个桌面图标的一次读取结果</summary>
/// <param name="Index">ListView 中的索引（Explorer 重排后会变，不可持久化）</param>
/// <param name="DisplayName">
/// 显示名。隐藏扩展名时不含后缀 —— 同名不同类型的文件显示名相同，
/// 因此**不可单独作为文件标识**，需由 <see cref="DesktopItemResolver"/> 消歧。
/// </param>
/// <param name="Position">坐标（物理像素，ListView 客户区）</param>
public readonly record struct DesktopIconSnapshot(int Index, string DisplayName, IconPoint Position);
