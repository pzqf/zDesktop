using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Desktop;

/// <summary>
/// 桌面覆盖层窗口 — zDesktop 核心窗口，**每个显示器一个实例**
///
/// 职责：
/// 1. 透明分层窗口覆盖所属显示器的工作区（排除任务栏）
/// 2. 默认鼠标点击透传到下层原生桌面图标
/// 3. Z 序锚定在桌面图标层（SHELLDLL_DefView）上方，不置顶
/// 4. 组件区域通过 hit-test 选择性捕获鼠标
/// 5. Explorer 重启 / 显示器变更 / 全屏应用 三种事件的自愈与让位
///
/// 坐标约定（设计案 v3.1 §八）：所有 Win32 交互用物理像素，所有 WPF 属性用 DIP，
/// 换算一律走 <see cref="DpiHelper"/>，本类内不手写换算系数。
/// </summary>
public class DesktopOverlayWindow : Window
{
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private IntPtr _desktopIconHwnd;

    /// <summary>Explorer 重启广播消息 ID（RegisterWindowMessage("TaskbarCreated")）</summary>
    private uint _taskbarCreatedMsg;

    /// <summary>Z 序兜底校验定时器 — 消息拦截漏掉的情况由它低频兜底</summary>
    private DispatcherTimer? _zOrderTimer;

    /// <summary>当前是否因全屏应用而让位隐藏</summary>
    private bool _yieldedToFullscreen;

    /// <summary>本覆盖层所属的显示器</summary>
    public MonitorInfo Monitor { get; private set; }

    /// <summary>是否为主显示器上的覆盖层（图标层/搜索框等单例元素只挂在主屏）</summary>
    public bool IsPrimary => Monitor.IsPrimary;

    /// <summary>组件区域 hit-test 回调 — 返回 true 表示该区域捕获鼠标</summary>
    public Func<Point, bool>? HitTestCallback { get; set; }

    /// <summary>窗口就绪事件 — ContentRendered 后触发，此时 HWND 与桌面图标层 HWND 均已就绪</summary>
    public event Action? Ready;

    /// <summary>显示器配置变更事件 — 显示器热插拔/分辨率/DPI 变化时触发</summary>
    public event Action? DisplayChanged;

    /// <summary>Explorer 重启事件 — 收到 TaskbarCreated 广播时触发</summary>
    public event Action? ExplorerRestarted;

    /// <summary>全屏让位状态变更事件（true=已让位隐藏）</summary>
    public event Action<bool>? FullscreenYieldChanged;

    public DesktopOverlayWindow(MonitorInfo monitor)
    {
        Monitor = monitor;

        // 无边框透明窗口
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = false;  // 不置顶，通过 SetWindowPos 精确控制 Z 序
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowActivated = false;
        // 窗口位置完全由 MonitorInfo 决定，不交给 WPF 自动定位
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        // 1. 工具窗口（不在 Alt+Tab 显示）
        var exStyle = Win32.GetWindowExStyle(_hwnd);
        exStyle |= Win32.WS_EX_TOOLWINDOW;
        Win32.SetWindowExStyle(_hwnd, exStyle);

        // 2. 查找桌面图标层 HWND
        _desktopIconHwnd = DesktopWindowFinder.FindDesktopIconView();
        Console.WriteLine($"[Overlay:{Monitor.Key}] 桌面图标层 HWND: 0x{_desktopIconHwnd.ToInt64():X}");

        // 3. 注册并放行 Explorer 重启广播消息。
        //    广播消息会被 UIPI 过滤，须显式放行才能收到。
        _taskbarCreatedMsg = Win32.RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreatedMsg != 0)
        {
            try
            {
                Win32.ChangeWindowMessageFilterEx(_hwnd, _taskbarCreatedMsg, Win32.MSGFLT_ALLOW, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Overlay:{Monitor.Key}] 放行 TaskbarCreated 失败: {ex.Message}");
            }
        }

        // 4. 按所属显示器的工作区定位
        ApplyMonitorBounds();

        // 5. 延迟到 ContentRendered 后再锚定 Z 序（此时窗口已可见）
        ContentRendered += OnContentRendered;
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        PositionAboveDesktopIcons();
        StartZOrderKeeper();
        Console.WriteLine($"[Overlay:{Monitor.Key}] Z 序锚定完成，兜底校验已启动");
        Ready?.Invoke();
    }

    /// <summary>
    /// 按所属显示器的工作区定位窗口。
    ///
    /// MONITORINFO 的 rcWork 是**物理像素**，WPF 的 Left/Top/Width/Height 是 **DIP**，
    /// 必须按该显示器自己的 DPI 换算——混合缩放的多屏场景下各屏 DPI 不同，
    /// 用统一系数会让副屏错位。
    /// </summary>
    private void ApplyMonitorBounds()
    {
        var (left, top, width, height) = Monitor.WorkAreaDip;

        Left = left;
        Top = top;
        Width = width;
        Height = height;

        Console.WriteLine($"[Overlay:{Monitor.Key}] 定位: 物理 {Monitor.WorkArea.Width}x{Monitor.WorkArea.Height} " +
                          $"@({Monitor.WorkArea.Left},{Monitor.WorkArea.Top}) → DIP {width:F0}x{height:F0} " +
                          $"@({left:F0},{top:F0}) 缩放 {Monitor.Scale:P0}");
    }

    /// <summary>显示器信息变更（分辨率/DPI/位置）后重新定位</summary>
    public void UpdateMonitor(MonitorInfo monitor)
    {
        Monitor = monitor;
        ApplyMonitorBounds();
        PositionAboveDesktopIcons();
    }

    /// <summary>
    /// 将覆盖层 Z 序设为桌面图标层（SHELLDLL_DefView）的正上方。
    /// 不使用 HWND_TOPMOST，避免浮于所有窗口之上（零破坏契约原则 2）。
    /// </summary>
    public void PositionAboveDesktopIcons()
    {
        if (_hwnd == IntPtr.Zero) return;

        Win32.SetWindowPos(
            _hwnd,
            ResolveZOrderAnchor(),
            0, 0, 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOMOVE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_SHOWWINDOW
        );
    }

    /// <summary>
    /// 解析 Z 序锚点 — 返回覆盖层应插入其上方的窗口句柄。
    /// 句柄失效（如 Explorer 重启）时重新查找；仍找不到则退回 HWND_BOTTOM。
    /// </summary>
    private IntPtr ResolveZOrderAnchor()
    {
        if (_desktopIconHwnd == IntPtr.Zero || !Win32.IsWindow(_desktopIconHwnd))
        {
            var found = DesktopWindowFinder.FindDesktopIconView();
            if (found != _desktopIconHwnd)
            {
                Console.WriteLine($"[Overlay:{Monitor.Key}] 桌面图标层句柄已刷新: " +
                                  $"0x{_desktopIconHwnd.ToInt64():X} → 0x{found.ToInt64():X}");
                _desktopIconHwnd = found;
            }
        }

        return _desktopIconHwnd != IntPtr.Zero ? _desktopIconHwnd : Win32.HWND_BOTTOM;
    }

    /// <summary>启动 Z 序兜底校验（低频，仅在锚点漂移时才真正调用 SetWindowPos）</summary>
    private void StartZOrderKeeper()
    {
        _zOrderTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _zOrderTimer.Tick += (_, _) =>
        {
            if (_hwnd == IntPtr.Zero) return;
            if (_desktopIconHwnd == IntPtr.Zero || !Win32.IsWindow(_desktopIconHwnd))
                PositionAboveDesktopIcons();
        };
        _zOrderTimer.Start();
    }

    /// <summary>
    /// 全屏让位（设计案 v3.1 §二 原则 6）。
    ///
    /// 由 <see cref="FullscreenGuard"/> 驱动：检测到全屏应用/游戏时隐藏覆盖层并停掉
    /// Z 序定时器，确保全屏期间零存在感、零 CPU。
    /// </summary>
    public void SetFullscreenYield(bool yield)
    {
        if (_yieldedToFullscreen == yield) return;
        _yieldedToFullscreen = yield;

        if (yield)
        {
            _zOrderTimer?.Stop();
            Visibility = Visibility.Hidden;
        }
        else
        {
            Visibility = Visibility.Visible;
            _zOrderTimer?.Start();
            PositionAboveDesktopIcons();
        }

        Console.WriteLine($"[Overlay:{Monitor.Key}] 全屏让位: {(yield ? "已隐藏" : "已恢复")}");
        FullscreenYieldChanged?.Invoke(yield);
    }

    /// <summary>
    /// 隐藏原生桌面图标层（SHELLDLL_DefView）—— 仅自渲染图标实验模式使用。
    /// 默认路径不会调用（零破坏契约原则 1）。
    /// </summary>
    public void HideNativeIcons()
    {
        if (_desktopIconHwnd != IntPtr.Zero)
        {
            Win32.ShowWindow(_desktopIconHwnd, Win32.SW_HIDE);
            Console.WriteLine("[Overlay] 已隐藏原生桌面图标层");
        }
    }

    /// <summary>恢复原生桌面图标层显示</summary>
    public void ShowNativeIcons()
    {
        // 句柄可能已因 Explorer 重启失效，这里重新解析以确保还原一定生效
        var hwnd = ResolveZOrderAnchor();
        if (hwnd != IntPtr.Zero && hwnd != Win32.HWND_BOTTOM)
        {
            Win32.ShowWindow(hwnd, Win32.SW_SHOWNORMAL);
            Console.WriteLine("[Overlay] 已恢复原生桌面图标层");
        }
    }

    /// <summary>窗口消息处理 — hit-test / Z 序自愈 / 显示器变更 / Explorer 重启</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        const int WM_DISPLAYCHANGE = 0x007E;

        // Explorer 重启 —— 桌面图标层被销毁重建，需重新查找句柄并重锚 Z 序
        if (_taskbarCreatedMsg != 0 && msg == (int)_taskbarCreatedMsg)
        {
            Console.WriteLine($"[Overlay:{Monitor.Key}] 收到 TaskbarCreated，Explorer 已重启，开始自愈");
            _desktopIconHwnd = IntPtr.Zero; // 强制下次 Resolve 时重新查找
            // 交给 Dispatcher 异步执行：此刻 Explorer 可能尚未建好 DefView
            Dispatcher.BeginInvoke(new Action(() =>
            {
                PositionAboveDesktopIcons();
                ExplorerRestarted?.Invoke();
            }), DispatcherPriority.Background);
            return IntPtr.Zero;
        }

        // Z 序自愈 — 有窗口试图插到覆盖层与桌面图标层之间时，改写落点强制锚回图标层上方。
        // 不置 handled：Z 序只是被修正，消息仍需交由默认流程继续处理。
        if (msg == Win32.WM_WINDOWPOSCHANGING)
        {
            var wp = Marshal.PtrToStructure<Win32.WINDOWPOS>(lParam);
            if ((wp.flags & Win32.SWP_NOZORDER) == 0)
            {
                var anchor = ResolveZOrderAnchor();
                if (wp.hwndInsertAfter != anchor)
                {
                    wp.hwndInsertAfter = anchor;
                    Marshal.StructureToPtr(wp, lParam, false);
                }
            }
        }

        if (msg == WM_NCHITTEST)
        {
            // lParam 低/高 16 位为鼠标屏幕坐标（物理像素）
            var screenX = (short)(lParam.ToInt32() & 0xFFFF);
            var screenY = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

            var point = new Win32.POINT { X = screenX, Y = screenY };
            Win32.ScreenToClient(hwnd, ref point);

            // 物理像素 → DIP，按本窗口所在显示器的 DPI
            var dpi = DpiHelper.GetWindowDpi(hwnd);
            var wpfPoint = new Point(DpiHelper.ToDip(point.X, dpi), DpiHelper.ToDip(point.Y, dpi));

            if (HitTestCallback != null && HitTestCallback(wpfPoint))
            {
                handled = true;
                return new IntPtr(Win32.HTCLIENT);
            }

            // 否则透传到下层原生桌面图标
            handled = true;
            return new IntPtr(Win32.HTTRANSPARENT);
        }

        // 显示器配置变更 / DPI 变更 —— 由 App 层重新枚举显示器并重建覆盖层集合
        if (msg == WM_DISPLAYCHANGE || msg == Win32.WM_DPICHANGED)
        {
            Console.WriteLine($"[Overlay:{Monitor.Key}] 显示器或 DPI 配置变更");
            DisplayChanged?.Invoke();
            // 不置 handled：WM_DPICHANGED 仍需交由 WPF 完成自身的缩放处理
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _zOrderTimer?.Stop();
        _zOrderTimer = null;
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource?.Dispose();
        base.OnClosed(e);
    }
}
