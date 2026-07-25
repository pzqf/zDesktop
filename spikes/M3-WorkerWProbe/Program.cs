using System.Runtime.InteropServices;
using System.Text;

namespace ZDesktop.Spikes.WorkerW;

/// <summary>
/// 候选 B 前置探测：摸清本机桌面窗口拓扑（<b>只读，不创建也不重定父任何窗口</b>）。
///
/// <para>候选 B 要把分区背景放进一个位于「壁纸之上、桌面图标之下」的窗口，
/// 从而实时渲染、零延迟，替代候选 A 的「合成进壁纸」（实测有约 0.5 秒落地延迟）。</para>
///
/// <para>要先回答三个问题：</para>
/// <list type="number">
/// <item>本机 Progman / WorkerW / DefView 的父子与兄弟关系是什么样</item>
/// <item>发送 <c>0x052C</c> 后 Explorer 是否会生成可用的壁纸层 WorkerW</item>
/// <item><b>是否已被第三方占用</b> —— 本机装有元气桌面，它极可能也在用同一层，
/// 两个程序抢同一个 WorkerW 会互相顶掉</item>
/// </list>
/// </summary>
internal static class Program
{
    public static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 候选 B 前置探测：桌面窗口拓扑（只读）===\n");

        DumpTopology("发送 0x052C 之前");

        Console.WriteLine("\n--- 向 Progman 发送 0x052C（请求生成壁纸层 WorkerW）---");
        var progman = N.FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            Console.WriteLine("未找到 Progman，中止");
            return 2;
        }

        // 该消息让 Explorer 把桌面图标层分离到一个新 WorkerW，
        // 并在其后留出一个用于绘制壁纸的 WorkerW。这是 Wallpaper Engine / Lively 的标准做法。
        var ok = N.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0x0002, 3000, out _);
        Console.WriteLine($"SendMessageTimeout 返回: {(ok != IntPtr.Zero ? "成功" : "失败/超时")}");

        Thread.Sleep(500);
        DumpTopology("发送 0x052C 之后");

        Console.WriteLine("\n--- 定位壁纸层 WorkerW ---");
        var wallpaperWorker = FindWallpaperWorker(out var defViewWorker);

        Console.WriteLine($"承载 DefView 的窗口 : {Describe(defViewWorker)}");
        Console.WriteLine($"壁纸层 WorkerW      : {Describe(wallpaperWorker)}");

        if (wallpaperWorker == IntPtr.Zero)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n未能定位壁纸层 WorkerW —— 候选 B 在本机不可行");
            Console.ResetColor();
            return 1;
        }

        Console.WriteLine("\n--- 校验层级：图标必须在我们的背景之上 ---");
        var zOk = VerifyZOrder(defViewWorker, defViewWorker, wallpaperWorker);
        if (zOk)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  图标层在壁纸层之上 —— 背景放进壁纸层不会遮挡图标");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  层级不符合预期，背景可能盖住图标");
        }
        Console.ResetColor();

        Console.WriteLine("\n--- 检查该层是否已被第三方占用 ---");
        var children = ChildrenOf(wallpaperWorker);
        if (children.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("壁纸层 WorkerW 下没有子窗口 —— 未被占用，可安全放入我们的背景窗口");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"壁纸层 WorkerW 下已有 {children.Count} 个子窗口：");
            foreach (var c in children) Console.WriteLine($"  {Describe(c)}");
            Console.WriteLine("若属第三方壁纸程序，两者会互相覆盖，需要在设计中给出共存策略");
            Console.ResetColor();
        }

        N.GetWindowRect(wallpaperWorker, out var r);
        Console.WriteLine($"\n壁纸层矩形: ({r.Left},{r.Top})-({r.Right},{r.Bottom})  {r.Right - r.Left}x{r.Bottom - r.Top}");
        Console.WriteLine($"虚拟屏     : ({N.GetSystemMetrics(76)},{N.GetSystemMetrics(77)}) {N.GetSystemMetrics(78)}x{N.GetSystemMetrics(79)}");

        Console.WriteLine("\n结论：本机具备候选 B 所需的窗口层级，可进入实做 spike。");
        return 0;
    }

    /// <summary>
    /// 定位壁纸层 WorkerW —— 需同时兼容两种拓扑。
    ///
    /// <para><b>拓扑 A（Windows 11，本机实测）</b>：0x052C 后 WorkerW 被创建成
    /// <b>Progman 的子窗口</b>，在子窗口 Z 序中排在 SHELLDLL_DefView <b>之后</b>
    /// （FindWindowEx 按 Z 序枚举，靠前者在上）。图标层在上、壁纸层在下，
    /// 正是我们需要的层级。</para>
    ///
    /// <para><b>拓扑 B（Windows 10 经典）</b>：DefView 被移进一个顶层 WorkerW，
    /// 紧随其后的顶层 WorkerW 兄弟才是壁纸层。</para>
    /// </summary>
    private static IntPtr FindWallpaperWorker(out IntPtr defViewHost)
    {
        defViewHost = IntPtr.Zero;

        // 拓扑 A：DefView 直接挂在 Progman 下
        var progman = N.FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            var defView = N.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                defViewHost = progman;
                // 取 Progman 下位于 DefView 之后的 WorkerW
                var worker = N.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
                if (worker != IntPtr.Zero) return worker;
            }
        }

        // 拓扑 B：DefView 在某个顶层 WorkerW 里
        IntPtr host = IntPtr.Zero, result = IntPtr.Zero;
        N.EnumWindows((hwnd, _) =>
        {
            if (ClassOf(hwnd) != "WorkerW") return true;
            if (N.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;

            host = hwnd;
            result = N.FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null);
            return false;
        }, IntPtr.Zero);

        if (host != IntPtr.Zero) defViewHost = host;
        return result;
    }

    /// <summary>
    /// 校验层级：在同一父窗口下，DefView 必须排在壁纸层**之前**（即在其之上），
    /// 否则我们的背景会盖住桌面图标。
    /// </summary>
    private static bool VerifyZOrder(IntPtr parent, IntPtr defViewHost, IntPtr worker)
    {
        // 拓扑 B 下两者是顶层兄弟，父窗口为桌面
        var scope = parent == worker ? IntPtr.Zero : parent;

        var order = new List<IntPtr>();
        var child = IntPtr.Zero;
        while ((child = N.FindWindowEx(scope, child, null, null)) != IntPtr.Zero)
            order.Add(child);

        var defView = N.FindWindowEx(defViewHost, IntPtr.Zero, "SHELLDLL_DefView", null);
        var iDef = order.IndexOf(defView != IntPtr.Zero && scope == defViewHost ? defView : defViewHost);
        var iWorker = order.IndexOf(worker);

        Console.WriteLine($"  同级 Z 序：图标层 index={iDef}，壁纸层 index={iWorker}（越小越靠上）");
        return iDef >= 0 && iWorker >= 0 && iDef < iWorker;
    }

    private static void DumpTopology(string title)
    {
        Console.WriteLine($"--- {title} ---");

        var progman = N.FindWindow("Progman", null);
        Console.WriteLine($"Progman: {Describe(progman)}");
        foreach (var c in ChildrenOf(progman))
            Console.WriteLine($"  └ {Describe(c)}");

        var count = 0;
        N.EnumWindows((hwnd, _) =>
        {
            if (ClassOf(hwnd) != "WorkerW") return true;
            count++;

            var defView = N.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            var kids = ChildrenOf(hwnd);
            var visible = N.IsWindowVisible(hwnd);

            // 只打印有内容或可见的，否则十几个空 WorkerW 会淹没输出
            if (defView != IntPtr.Zero || kids.Count > 0 || visible)
            {
                Console.WriteLine($"WorkerW {Describe(hwnd)} visible={visible}" +
                                  (defView != IntPtr.Zero ? "  ★含 DefView" : ""));
                foreach (var k in kids) Console.WriteLine($"  └ {Describe(k)}");
            }
            return true;
        }, IntPtr.Zero);

        Console.WriteLine($"WorkerW 顶层窗口共 {count} 个（仅列出可见或有子窗口的）");
    }

    private static List<IntPtr> ChildrenOf(IntPtr parent)
    {
        var list = new List<IntPtr>();
        if (parent == IntPtr.Zero) return list;

        var child = IntPtr.Zero;
        while ((child = N.FindWindowEx(parent, child, null, null)) != IntPtr.Zero)
            list.Add(child);

        return list;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "<null>";
        var sb = new StringBuilder(128);
        N.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "<null>";

        N.GetWindowThreadProcessId(hwnd, out var pid);
        var proc = "?";
        try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }

        var title = new StringBuilder(128);
        N.GetWindowText(hwnd, title, title.Capacity);
        var t = title.ToString();

        return $"0x{hwnd.ToInt64():X} [{ClassOf(hwnd)}] pid={pid}({proc})" +
               (string.IsNullOrEmpty(t) ? "" : $" \"{t}\"");
    }
}

internal static class N
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? c, string? w);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? c, string? w);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr h, StringBuilder s, int n);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr h);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr h, out RECT r);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int i);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, IntPtr l,
        uint flags, int timeout, out IntPtr result);
}
