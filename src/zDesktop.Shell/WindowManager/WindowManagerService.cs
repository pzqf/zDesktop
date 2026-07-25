using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.WindowManager;

/// <summary>
/// 窗口矩形（公开结构）— 避免向 App 层暴露 internal 的 Win32.RECT
/// </summary>
public readonly record struct WindowRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>宽度</summary>
    public int Width => Right - Left;

    /// <summary>高度</summary>
    public int Height => Bottom - Top;
}

/// <summary>
/// 窗口信息快照 — 枚举顶级可见窗口时返回
/// </summary>
public sealed class WindowInfo
{
    /// <summary>窗口句柄</summary>
    public IntPtr Hwnd { get; init; }

    /// <summary>窗口标题</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>所属进程名</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>窗口矩形（屏幕坐标）</summary>
    public WindowRect Rect { get; init; }

    /// <summary>是否置顶</summary>
    public bool IsTopmost { get; init; }

    /// <summary>是否最大化</summary>
    public bool IsMaximized { get; init; }

    /// <summary>是否最小化</summary>
    public bool IsMinimized { get; init; }
}

/// <summary>
/// 预设布局类型 — 对应设计稿 window-manager.html 的布局按钮
/// </summary>
public enum LayoutType
{
    /// <summary>左半屏</summary>
    LeftHalf,

    /// <summary>右半屏</summary>
    RightHalf,

    /// <summary>上半屏</summary>
    TopHalf,

    /// <summary>下半屏</summary>
    BottomHalf,

    /// <summary>左三分</summary>
    ThirdsLeft,

    /// <summary>中三分</summary>
    ThirdsCenter,

    /// <summary>右三分</summary>
    ThirdsRight,

    /// <summary>四宫格（对其他窗口也排列）</summary>
    Quadrants,
}

/// <summary>
/// 窗口管理服务 — 枚举/排列/置顶/透明度/托盘化顶级窗口
///
/// 职责：
/// 1. EnumerateWindows — 枚举所有可见顶级窗口（可见 + 有标题 + 非工具窗口 + 无父窗口）
/// 2. ApplyLayout — 将窗口吸附到工作区的预设区域（半屏/三分屏/四宫格）
/// 3. CascadeWindows — 层叠排列所有可见窗口
/// 4. ToggleTopmost — 切换窗口置顶状态
/// 5. SetTransparency — 设置窗口透明度（分层窗口 alpha）
/// 6. MinimizeToTray / RestoreFromTray — 隐藏/恢复窗口（模拟最小化到托盘）
/// 7. CloseWindow — 向目标窗口投递 WM_CLOSE
///
/// 所有 P/Invoke 调用均容错，失败返回 false 并写日志（Console.WriteLine）。
/// </summary>
public sealed class WindowManagerService
{
    // ===== Win32.cs 未定义的常量（本服务内补充）=====

    /// <summary>置顶扩展样式</summary>
    private const int WS_EX_TOPMOST = 0x00000008;

    /// <summary>分层窗口 alpha 标志（SetLayeredWindowAttributes 的 dwFlags）</summary>
    private const uint LWA_ALPHA = 0x00000002;

    /// <summary>WM_CLOSE 消息（关闭窗口）</summary>
    private const uint WM_CLOSE = 0x0010;

    /// <summary>已最小化到托盘（隐藏）的窗口及其快照</summary>
    private readonly Dictionary<IntPtr, WindowInfo> _hiddenWindows = new();

    /// <summary>
    /// PostMessage P/Invoke — Win32.cs 未声明，用于向目标窗口投递 WM_CLOSE
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 枚举所有可见的顶级窗口
    /// 过滤条件：可见 + 有非空标题 + 非工具窗口 + 无父窗口
    /// </summary>
    /// <returns>窗口信息列表</returns>
    public List<WindowInfo> EnumerateWindows()
    {
        var list = new List<WindowInfo>();
        try
        {
            Win32.EnumWindows((hwnd, _) =>
            {
                var info = BuildWindowInfo(hwnd);
                if (info != null)
                    list.Add(info);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowManager] EnumerateWindows 失败: {ex.Message}");
        }
        return list;
    }

    /// <summary>
    /// 构建单个窗口的快照信息（已应用过滤条件，不满足返回 null）
    /// </summary>
    private WindowInfo? BuildWindowInfo(IntPtr hwnd)
    {
        try
        {
            if (!Win32.IsWindowVisible(hwnd))
                return null;

            var len = Win32.GetWindowTextLength(hwnd);
            if (len <= 0)
                return null;

            var sb = new StringBuilder(len + 1);
            Win32.GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title))
                return null;

            var exStyle = Win32.GetWindowExStyle(hwnd);
            if ((exStyle & Win32.WS_EX_TOOLWINDOW) != 0)
                return null;

            // 仅保留顶级窗口（无父窗口）
            if (Win32.GetParent(hwnd) != IntPtr.Zero)
                return null;

            Win32.GetWindowRect(hwnd, out var rect);
            Win32.GetWindowThreadProcessId(hwnd, out var pid);

            string processName;
            try
            {
                processName = Process.GetProcessById((int)pid).ProcessName;
            }
            catch
            {
                processName = "unknown";
            }

            return new WindowInfo
            {
                Hwnd = hwnd,
                Title = title,
                ProcessName = processName,
                Rect = new WindowRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                IsTopmost = (exStyle & WS_EX_TOPMOST) != 0,
                IsMaximized = Win32.IsZoomed(hwnd),
                IsMinimized = Win32.IsIconic(hwnd),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowManager] BuildWindowInfo 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 将指定窗口应用预设布局（吸附到工作区对应区域）
    /// </summary>
    /// <param name="hwnd">目标窗口句柄</param>
    /// <param name="layout">布局类型</param>
    /// <returns>是否成功</returns>
    public bool ApplyLayout(IntPtr hwnd, LayoutType layout)
    {
        if (layout == LayoutType.Quadrants)
            return ApplyQuadrants(hwnd);

        try
        {
            if (hwnd == IntPtr.Zero)
                return false;

            var wa = GetWorkArea();
            var w = wa.Width;
            var h = wa.Height;

            var (x, y, cw, ch) = layout switch
            {
                LayoutType.LeftHalf => (wa.Left, wa.Top, w / 2, h),
                LayoutType.RightHalf => (wa.Left + w / 2, wa.Top, w - w / 2, h),
                LayoutType.TopHalf => (wa.Left, wa.Top, w, h / 2),
                LayoutType.BottomHalf => (wa.Left, wa.Top + h / 2, w, h - h / 2),
                LayoutType.ThirdsLeft => (wa.Left, wa.Top, w / 3, h),
                LayoutType.ThirdsCenter => (wa.Left + w / 3, wa.Top, w / 3, h),
                LayoutType.ThirdsRight => (wa.Left + 2 * w / 3, wa.Top, w - 2 * w / 3, h),
                _ => (wa.Left, wa.Top, w, h),
            };

            return Win32.MoveWindow(hwnd, x, y, cw, ch, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowManager] ApplyLayout 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 四宫格布局 — 当前窗口放入左上格，其余枚举到的可见窗口依次填入另外三格
    /// </summary>
    private bool ApplyQuadrants(IntPtr hwnd)
    {
        try
        {
            var wa = GetWorkArea();
            var w = wa.Width;
            var h = wa.Height;
            var hw = w / 2;
            var hh = h / 2;

            var quads = new (int X, int Y, int W, int H)[]
            {
                (wa.Left, wa.Top, hw, hh),
                (wa.Left + hw, wa.Top, w - hw, hh),
                (wa.Left, wa.Top + hh, hw, h - hh),
                (wa.Left + hw, wa.Top + hh, w - hw, h - hh),
            };

            Win32.MoveWindow(hwnd, quads[0].X, quads[0].Y, quads[0].W, quads[0].H, true);

            var others = EnumerateWindows()
                .Where(i => i.Hwnd != hwnd)
                .Take(3)
                .ToList();

            for (var i = 0; i < others.Count; i++)
            {
                var q = quads[i + 1];
                Win32.MoveWindow(others[i].Hwnd, q.X, q.Y, q.W, q.H, true);
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowManager] ApplyQuadrants 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 层叠排列所有可见窗口（每个窗口相对上一个偏移 24px）
    /// </summary>
    /// <returns>是否成功</returns>
    public bool CascadeWindows()
    {
        try
        {
            var wa = GetWorkArea();
            var windows = EnumerateWindows();
            const int offset = 24;
            const int cascadeWidth = 480;
            const int cascadeHeight = 360;
            var index = 0;
            foreach (var win in windows)
            {
                var x = wa.Left + Math.Min(index * offset, Math.Max(0, wa.Width - cascadeWidth));
                var y = wa.Top + Math.Min(index * offset, Math.Max(0, wa.Height - cascadeHeight));
                Win32.MoveWindow(win.Hwnd, x, y, cascadeWidth, cascadeHeight, true);
                index++;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowManager] CascadeWindows 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 还原窗口（取消最大化/最小化/布局，恢复到正常状态）
    /// </summary>
    /// <param name="hwnd">目标窗口句柄</param>
    /// <returns>是否成功</returns>
    public bool RestoreWindow(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero)
                return false;
            return Win32.ShowWindowAsync(hwnd, Win32.SW_RESTORE);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowManager] RestoreWindow 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 切换窗口置顶状态
    /// </summary>
    /// <param name="hwnd">目标窗口句柄</param>
    /// <returns>操作是否成功（新状态请通过 EnumerateWindows 的 IsTopmost 字段查询）</returns>
    public bool ToggleTopmost(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero)
                return false;

            var isTopmost = (Win32.GetWindowExStyle(hwnd) & WS_EX_TOPMOST) != 0;
            var insertAfter = isTopmost ? Win32.HWND_NOTOPMOST : Win32.HWND_TOPMOST;
            var flags = Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE;
            return Win32.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, flags);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowManager] ToggleTopmost 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 设置窗口透明度（启用分层窗口并设置 alpha）
    /// </summary>
    /// <param name="hwnd">目标窗口句柄</param>
    /// <param name="alpha">不透明度 0-255（255 为完全不透明）</param>
    /// <returns>是否成功</returns>
    public bool SetTransparency(IntPtr hwnd, byte alpha)
    {
        try
        {
            if (hwnd == IntPtr.Zero)
                return false;

            var exStyle = Win32.GetWindowExStyle(hwnd);
            if ((exStyle & Win32.WS_EX_LAYERED) == 0)
                Win32.SetWindowExStyle(hwnd, exStyle | Win32.WS_EX_LAYERED);

            return Win32.SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowManager] SetTransparency 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 最小化到托盘 — 实际为隐藏窗口并记录快照，可用 RestoreFromTray 恢复
    /// </summary>
    /// <param name="hwnd">目标窗口句柄</param>
    /// <returns>是否成功</returns>
    public bool MinimizeToTray(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero)
                return false;

            var info = BuildWindowInfo(hwnd);
            if (info != null)
                _hiddenWindows[hwnd] = info;

            return Win32.ShowWindowAsync(hwnd, Win32.SW_HIDE);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowManager] MinimizeToTray 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 从托盘恢复单个窗口
    /// </summary>
    /// <param name="hwnd">目标窗口句柄</param>
    /// <returns>是否成功</returns>
    public bool RestoreFromTray(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero)
                return false;

            var ok = Win32.ShowWindowAsync(hwnd, Win32.SW_SHOWNORMAL);
            _hiddenWindows.Remove(hwnd);
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowManager] RestoreFromTray 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 恢复所有已最小化到托盘的窗口
    /// </summary>
    public void RestoreAllFromTray()
    {
        foreach (var hwnd in _hiddenWindows.Keys.ToList())
            RestoreFromTray(hwnd);
    }

    /// <summary>
    /// 获取当前已最小化到托盘的窗口快照列表
    /// </summary>
    public IReadOnlyList<WindowInfo> GetHiddenWindows() => _hiddenWindows.Values.ToList();

    /// <summary>
    /// 关闭指定窗口（投递 WM_CLOSE，由目标窗口自行决定是否关闭）
    /// </summary>
    /// <param name="hwnd">目标窗口句柄</param>
    /// <returns>是否成功投递</returns>
    public bool CloseWindow(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero)
                return false;
            return PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowManager] CloseWindow 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 获取当前前台窗口句柄
    /// </summary>
    public IntPtr GetForegroundWindow() => Win32.GetForegroundWindow();

    /// <summary>
    /// 获取屏幕工作区（排除任务栏）
    /// </summary>
    private static WindowRect GetWorkArea()
    {
        var rect = new Win32.RECT();
        Win32.SystemParametersInfo(Win32.SPI_GETWORKAREA, 0, ref rect, 0);
        return new WindowRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }
}
