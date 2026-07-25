using System.Runtime.InteropServices;
using System.Text;

namespace ZDesktop.Spikes.M3;

/// <summary>
/// M3-B 探针：实测「ListView 客户区坐标」与「屏幕/显示器工作区坐标」的映射关系。
///
/// <para><b>只读</b> —— 不写任何坐标、不改任何设置，可安全重复运行。</para>
///
/// <para>要回答的问题：分区矩形存的是「相对所属显示器工作区的 DIP」，
/// 而 Explorer 的图标坐标是「ListView 客户区物理像素」。
/// 两者之间差一个原点平移和一个 DPI 缩放，具体差多少必须实测，不能推断 ——
/// 尤其是多屏且副屏在主屏左侧（坐标为负）时。</para>
/// </summary>
internal static class Program
{
    public static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== M3-B 坐标空间探针（只读）===\n");

        // ---- 虚拟屏 ----
        var vx = N.GetSystemMetrics(N.SM_XVIRTUALSCREEN);
        var vy = N.GetSystemMetrics(N.SM_YVIRTUALSCREEN);
        var vw = N.GetSystemMetrics(N.SM_CXVIRTUALSCREEN);
        var vh = N.GetSystemMetrics(N.SM_CYVIRTUALSCREEN);
        Console.WriteLine($"虚拟屏（物理像素）: 原点 ({vx},{vy})  尺寸 {vw}x{vh}");

        // ---- 显示器 ----
        Console.WriteLine("\n显示器：");
        var monitors = new List<(string Name, N.RECT Mon, N.RECT Work, bool Primary, uint Dpi)>();
        N.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref N.RECT _, IntPtr _) =>
        {
            var mi = N.MONITORINFOEX.Create();
            if (N.GetMonitorInfo(h, ref mi))
            {
                uint dpi = 96;
                try { N.GetDpiForMonitor(h, 0, out dpi, out _); } catch { }
                monitors.Add((mi.szDevice, mi.rcMonitor, mi.rcWork,
                    (mi.dwFlags & 1) != 0, dpi));
            }
            return true;
        }, IntPtr.Zero);

        foreach (var m in monitors)
        {
            Console.WriteLine($"  {m.Name}{(m.Primary ? " (主)" : "")}");
            Console.WriteLine($"    全区 ({m.Mon.Left},{m.Mon.Top})-({m.Mon.Right},{m.Mon.Bottom})  " +
                              $"{m.Mon.Right - m.Mon.Left}x{m.Mon.Bottom - m.Mon.Top}");
            Console.WriteLine($"    工作区 ({m.Work.Left},{m.Work.Top})-({m.Work.Right},{m.Work.Bottom})  " +
                              $"DPI={m.Dpi} 缩放={m.Dpi / 96.0:P0}");
        }

        // ---- ListView ----
        var defView = FindDefView();
        if (defView == IntPtr.Zero) { Console.WriteLine("\n未找到 SHELLDLL_DefView"); return 2; }

        var listView = N.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        if (listView == IntPtr.Zero) { Console.WriteLine("\n未找到 SysListView32"); return 2; }

        N.GetWindowRect(listView, out var lvWin);
        N.GetClientRect(listView, out var lvClient);
        var origin = new N.POINT { X = 0, Y = 0 };
        N.ClientToScreen(listView, ref origin);

        Console.WriteLine($"\nSysListView32 = 0x{listView.ToInt64():X}");
        Console.WriteLine($"  窗口矩形（屏幕物理像素）: ({lvWin.Left},{lvWin.Top})-({lvWin.Right},{lvWin.Bottom})  " +
                          $"{lvWin.Right - lvWin.Left}x{lvWin.Bottom - lvWin.Top}");
        Console.WriteLine($"  客户区尺寸: {lvClient.Right}x{lvClient.Bottom}");
        Console.WriteLine($"  客户区原点在屏幕上的位置: ({origin.X},{origin.Y})   ← 这就是平移量");

        // ---- 覆盖范围判定 ----
        Console.WriteLine("\n判定：");
        var coversVirtual = (lvWin.Right - lvWin.Left) >= vw - 8 && (lvWin.Bottom - lvWin.Top) >= vh - 8;
        Console.WriteLine($"  ListView 是否覆盖整个虚拟屏: {(coversVirtual ? "是" : "否")}");
        if (coversVirtual)
            Console.WriteLine("    → 图标坐标是**全虚拟屏**统一坐标系，跨屏分区只需按显示器矩形切分");
        else
            Console.WriteLine("    → ListView 仅覆盖部分区域，副屏图标可能由另一个 ListView 承载，需逐屏定位");

        // ---- 采样若干图标，换算到屏幕坐标 ----
        Console.WriteLine("\n图标坐标换算抽样（客户区 → 屏幕 → 所属显示器）：");
        using var reader = IconReader.Open(listView);
        if (reader == null)
        {
            Console.WriteLine("  无法打开 explorer 进程内存，跳过抽样");
        }
        else
        {
            var count = reader.Count;
            Console.WriteLine($"  图标总数 {count}");
            for (var i = 0; i < Math.Min(count, 6); i++)
            {
                var p = reader.GetPosition(i);
                if (p == null) continue;

                var screenX = origin.X + p.Value.X;
                var screenY = origin.Y + p.Value.Y;
                var owner = monitors.FirstOrDefault(m =>
                    screenX >= m.Mon.Left && screenX < m.Mon.Right &&
                    screenY >= m.Mon.Top && screenY < m.Mon.Bottom);

                var ownerName = owner.Name ?? "<不在任何显示器内>";
                var relX = owner.Name != null ? screenX - owner.Work.Left : 0;
                var relY = owner.Name != null ? screenY - owner.Work.Top : 0;
                var dip = owner.Name != null
                    ? $"  相对工作区 DIP ({relX * 96.0 / owner.Dpi:F0},{relY * 96.0 / owner.Dpi:F0})"
                    : "";

                Console.WriteLine($"  [{i,2}] 客户区 ({p.Value.X,5},{p.Value.Y,4}) → 屏幕 ({screenX,5},{screenY,4}) " +
                                  $"→ {ownerName}{dip}");
            }
        }

        Console.WriteLine("\n结论要点：");
        Console.WriteLine($"  屏幕坐标 = 客户区坐标 + ({origin.X},{origin.Y})");
        Console.WriteLine("  相对显示器工作区的 DIP = (屏幕坐标 - 工作区原点) × 96 / 该屏 DPI");
        return 0;
    }

    private static IntPtr FindDefView()
    {
        var progman = N.FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            var dv = N.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (dv != IntPtr.Zero) return dv;
        }

        IntPtr found = IntPtr.Zero;
        N.EnumWindows((h, _) =>
        {
            var sb = new StringBuilder(64);
            N.GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() == "WorkerW")
            {
                var dv = N.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero) { found = dv; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}

/// <summary>只读的图标坐标读取器</summary>
internal sealed class IconReader : IDisposable
{
    private readonly IntPtr _lv, _proc, _remote;

    private IconReader(IntPtr lv, IntPtr proc, IntPtr remote)
    {
        _lv = lv; _proc = proc; _remote = remote;
    }

    public static IconReader? Open(IntPtr listView)
    {
        N.GetWindowThreadProcessId(listView, out var pid);
        var proc = N.OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x0400, false, pid);
        if (proc == IntPtr.Zero) return null;

        var remote = N.VirtualAllocEx(proc, IntPtr.Zero, new IntPtr(256), 0x1000 | 0x2000, 0x04);
        if (remote == IntPtr.Zero) { N.CloseHandle(proc); return null; }

        return new IconReader(listView, proc, remote);
    }

    public int Count => (int)N.SendMessage(_lv, 0x1004, IntPtr.Zero, IntPtr.Zero);

    public N.POINT? GetPosition(int index)
    {
        N.SendMessage(_lv, 0x1010, new IntPtr(index), _remote);
        var size = Marshal.SizeOf<N.POINT>();
        var local = Marshal.AllocHGlobal(size);
        try
        {
            if (!N.ReadProcessMemory(_proc, _remote, local, new IntPtr(size), out _)) return null;
            return Marshal.PtrToStructure<N.POINT>(local);
        }
        finally { Marshal.FreeHGlobal(local); }
    }

    public void Dispose()
    {
        if (_remote != IntPtr.Zero) N.VirtualFreeEx(_proc, _remote, IntPtr.Zero, 0x8000);
        if (_proc != IntPtr.Zero) N.CloseHandle(_proc);
    }
}

internal static class N
{
    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77,
                     SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        public static MONITORINFOEX Create() => new() { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
    }

    public delegate bool MonitorEnumProc(IntPtr h, IntPtr hdc, ref RECT clip, IntPtr data);
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr h, int type, out uint x, out uint y);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? c, string? w);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr after, string? c, string? w);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr a, IntPtr s, uint t, uint pr);
    [DllImport("kernel32.dll")] public static extern bool VirtualFreeEx(IntPtr p, IntPtr a, IntPtr s, uint t);
    [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr p, IntPtr a, IntPtr b, IntPtr s, out IntPtr r);
}
