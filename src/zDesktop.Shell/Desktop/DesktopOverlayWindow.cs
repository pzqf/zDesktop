using System.Windows;
using System.Windows.Interop;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Desktop;

/// <summary>
/// 桌面接管窗口 — zDesktop 核心窗口
///
/// 职责：
/// 1. 透明分层窗口覆盖桌面工作区（排除任务栏）
/// 2. 默认鼠标点击透传到下层桌面图标
/// 3. Z 序锚定在桌面图标层（SHELLDLL_DefView）上方，不置顶
/// 4. 组件区域通过 hit-test 选择性捕获鼠标
/// </summary>
public class DesktopOverlayWindow : Window
{
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private IntPtr _desktopIconHwnd;

    /// <summary>组件区域 hit-test 回调 — 返回 true 表示该区域捕获鼠标</summary>
    public Func<Point, bool>? HitTestCallback { get; set; }

    /// <summary>窗口就绪事件 — ContentRendered 后触发，此时 HWND 与桌面图标层 HWND 均已就绪</summary>
    public event Action? Ready;

    /// <summary>显示器配置变更事件 — 显示器热插拔/分辨率变化时触发</summary>
    public event Action? DisplayChanged;

    public DesktopOverlayWindow()
    {
        // 无边框透明窗口
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = false;  // 不置顶，通过 SetWindowPos Z 序控制层级
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowActivated = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        // 1. 设置窗口扩展样式：工具窗口（不在 Alt+Tab 显示）
        var exStyle = Win32.GetWindowExStyle(_hwnd);
        exStyle |= Win32.WS_EX_TOOLWINDOW;
        Win32.SetWindowExStyle(_hwnd, exStyle);

        // 2. 查找桌面图标层 HWND
        _desktopIconHwnd = DesktopWindowFinder.FindDesktopIconView();
        Console.WriteLine($"[DesktopOverlay] 桌面图标层 HWND: 0x{_desktopIconHwnd.ToInt64():X}");

        // 3. 设置工作区大小（排除任务栏）
        UpdateWorkAreaBounds();

        // 4. 延迟到 ContentRendered 后再锚定 Z 序（此时窗口已可见）
        ContentRendered += OnContentRendered;
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        // 窗口已渲染完成，现在调整 Z 序
        PositionAboveDesktopIcons();
        Console.WriteLine("[DesktopOverlay] Z 序锚定完成（ContentRendered 后）");
        // 通知外部：HWND 与桌面图标层均已就绪，可执行隐藏原生图标等操作
        Ready?.Invoke();
    }

    /// <summary>
    /// 获取桌面工作区并设置窗口大小
    /// </summary>
    private void UpdateWorkAreaBounds()
    {
        var rect = new Win32.RECT();
        Win32.SystemParametersInfo(Win32.SPI_GETWORKAREA, 0, ref rect, 0);

        Width = rect.Width;
        Height = rect.Height;
        Left = rect.Left;
        Top = rect.Top;

        Console.WriteLine($"[DesktopOverlay] 工作区: {rect.Width}x{rect.Height} @ ({rect.Left},{rect.Top})");
    }

    /// <summary>
    /// 将接管窗口的 Z 序设为桌面图标层（SHELLDLL_DefView）的正上方
    /// 不使用 HWND_TOPMOST，避免浮于所有窗口之上
    /// </summary>
    public void PositionAboveDesktopIcons()
    {
        if (_hwnd == IntPtr.Zero) return;

        var insertAfter = _desktopIconHwnd != IntPtr.Zero
            ? _desktopIconHwnd
            : Win32.HWND_BOTTOM;

        Win32.SetWindowPos(
            _hwnd,
            insertAfter,
            0, 0, 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOMOVE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_SHOWWINDOW
        );
    }

    /// <summary>
    /// 隐藏原生桌面图标层（SHELLDLL_DefView）—— zDesktop 自渲染图标时调用
    /// 仅隐藏窗口显示，不删除图标数据；退出时需调用 ShowNativeIcons 恢复
    /// </summary>
    public void HideNativeIcons()
    {
        if (_desktopIconHwnd != IntPtr.Zero)
        {
            Win32.ShowWindow(_desktopIconHwnd, Win32.SW_HIDE);
            Console.WriteLine("[DesktopOverlay] 已隐藏原生桌面图标层");
        }
    }

    /// <summary>恢复原生桌面图标层显示</summary>
    public void ShowNativeIcons()
    {
        if (_desktopIconHwnd != IntPtr.Zero)
        {
            Win32.ShowWindow(_desktopIconHwnd, Win32.SW_SHOWNORMAL);
            Console.WriteLine("[DesktopOverlay] 已恢复原生桌面图标层");
        }
    }

    /// <summary>
    /// 窗口消息处理 — 核心 hit-test 逻辑
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        const int WM_DISPLAYCHANGE = 0x007E;

        if (msg == WM_NCHITTEST)
        {
            // 获取鼠标屏幕坐标
            var screenX = (short)(lParam.ToInt32() & 0xFFFF);
            var screenY = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

            // 转换为窗口客户区坐标（物理像素）
            var point = new Win32.POINT { X = screenX, Y = screenY };
            Win32.ScreenToClient(hwnd, ref point);

            // 物理像素 → WPF 逻辑像素（DIP），兼容高 DPI 缩放
            var dpiScale = _hwndSource?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
            var wpfPoint = new Point(point.X * dpiScale, point.Y * dpiScale);

            // 如果 hit-test 回调存在且该区域有组件，返回 HTCLIENT 捕获鼠标
            if (HitTestCallback != null && HitTestCallback(wpfPoint))
            {
                handled = true;
                return new IntPtr(Win32.HTCLIENT);
            }

            // 否则透传到下层桌面图标
            handled = true;
            return new IntPtr(Win32.HTTRANSPARENT);
        }

        if (msg == WM_DISPLAYCHANGE)
        {
            // 显示器配置变更 — 更新工作区并通知外部
            Console.WriteLine("[DesktopOverlay] 显示器配置变更，更新工作区");
            UpdateWorkAreaBounds();
            DisplayChanged?.Invoke();
            handled = true;
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource?.Dispose();
        base.OnClosed(e);
    }
}
