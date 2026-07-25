using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Fences;

/// <summary>
/// 壁纸层定位 —— 找到位于「壁纸之上、桌面图标之下」的那个 WorkerW。
///
/// <para><b>为什么重新启用 WorkerW</b>：v2.3 曾判定「WorkerW 方案已证伪」，
/// 但那个结论的适用范围是把**带交互的覆盖层**塞进 WorkerW —— 窗口被壁纸层遮挡、
/// 交互全废。候选 B 放进去的是**纯被动的背景渲染层**，没有任何交互需求，
/// 这正是 Wallpaper Engine / Lively 的标准做法，被大量验证过。
/// 交互仍然留在原来那个位于图标层之上的覆盖层里，两者职责分离。</para>
///
/// <para>实测本机（Win11）拓扑：向 Progman 发 <c>0x052C</c> 后，
/// WorkerW 被创建为 <b>Progman 的子窗口</b>，在子窗口 Z 序中排在 SHELLDLL_DefView
/// 之后（即之下）。Win10 经典拓扑则是 DefView 被移进一个顶层 WorkerW、
/// 紧随其后的顶层 WorkerW 才是壁纸层。两种都要兼容。</para>
/// </summary>
public static class WallpaperLayer
{
    /// <summary>请求 Explorer 生成壁纸层 WorkerW 的未文档化消息</summary>
    private const int WM_SPAWN_WORKER = 0x052C;

    /// <summary>
    /// 定位壁纸层 WorkerW，必要时请求 Explorer 生成。找不到返回 <see cref="IntPtr.Zero"/>。
    /// </summary>
    public static IntPtr Find()
    {
        var found = Locate();
        if (found != IntPtr.Zero) return found;

        // 尚未生成，请求 Explorer 创建后重试
        var progman = Win32.FindWindow("Progman", null!);
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        Win32.SendMessageTimeout(progman, WM_SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero,
            Win32.SMTO_ABORTIFHUNG, 3000, out _);

        return Locate();
    }

    private static IntPtr Locate()
    {
        // 拓扑 A（Win11 实测）：DefView 与 WorkerW 同为 Progman 的子窗口
        var progman = Win32.FindWindow("Progman", null!);
        if (progman != IntPtr.Zero &&
            Win32.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null!) != IntPtr.Zero)
        {
            var worker = Win32.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null!);
            if (worker != IntPtr.Zero) return worker;
        }

        // 拓扑 B（Win10 经典）：DefView 在某个顶层 WorkerW 内，其后的兄弟才是壁纸层
        IntPtr result = IntPtr.Zero;
        Win32.EnumWindows((hwnd, _) =>
        {
            var sb = new StringBuilder(64);
            Win32.GetClassName(hwnd, sb, sb.Capacity);
            if (sb.ToString() != "WorkerW") return true;
            if (Win32.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null!) == IntPtr.Zero) return true;

            result = Win32.FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null!);
            return false;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// 校验图标层确实位于指定壁纸层之上。
    /// 顺序反了会让背景盖住桌面图标 —— 直接违反零破坏契约，宁可不启用候选 B。
    /// </summary>
    public static bool IsBelowIcons(IntPtr worker)
    {
        if (worker == IntPtr.Zero) return false;

        var parent = Win32.GetParent(worker);
        var defView = Win32.FindWindowEx(parent, IntPtr.Zero, "SHELLDLL_DefView", null!);
        if (defView == IntPtr.Zero) return false; // 拓扑 B，交由调用方按兄弟序判断

        // FindWindowEx 按 Z 序枚举，先出现者在上
        var child = IntPtr.Zero;
        while ((child = Win32.FindWindowEx(parent, child, null!, null!)) != IntPtr.Zero)
        {
            if (child == defView) return true;   // 图标层先出现 → 在上，符合预期
            if (child == worker) return false;   // 壁纸层先出现 → 在上，会遮挡图标
        }

        return false;
    }

    /// <summary>该壁纸层是否已被其他程序占用（其下已有子窗口）</summary>
    public static bool IsOccupied(IntPtr worker)
        => worker != IntPtr.Zero
        && Win32.FindWindowEx(worker, IntPtr.Zero, null!, null!) != IntPtr.Zero;
}


/// <summary>
/// 分区背景渲染层（设计案 v3.1 §4.3 候选 B）。
///
/// <para>寄生在壁纸层 WorkerW 内、位于桌面图标**之下**，实时重绘分区底色而不碰壁纸文件，
/// 彻底消除候选 A 那约 0.5 秒的落地延迟。</para>
///
/// <para><b>为什么用 <see cref="HwndSource"/> 而不是 WPF <c>Window</c></b>：
/// WPF 的 <c>Window</c> 无法作为子窗口正常渲染 —— 它的 HwndTarget/DWM 合成假定自己是顶层窗口。
/// 实测三条路全部失败：</para>
/// <list type="number">
/// <item><c>AllowsTransparency=true</c> + <c>SetParent</c> → 画面完全不出现</item>
/// <item><c>WS_EX_LAYERED</c> + 色键 → <c>SetLayeredWindowAttributes</c> 返回 87，
/// 扩展样式确认已置位（0x080000A0），系与 WPF 渲染目标不兼容</item>
/// <item>不透明 + 补 <c>WS_CHILD</c>（样式 0x46080000）→ 仍不渲染</item>
/// </list>
/// <para><see cref="HwndSource"/> 正是为「把 WPF 内容托管进任意 HWND（含子窗口）」设计的，
/// 创建时即以 WorkerW 为父、带 <c>WS_CHILD</c>，绕开 Window 的顶层假设。</para>
///
/// <para>本层<b>永不接收鼠标</b>（<c>WS_EX_TRANSPARENT</c>），交互全部留在图标层之上的覆盖层。</para>
/// </summary>
public sealed class FenceBackgroundLayer : IDisposable
{
    private readonly System.Windows.Controls.Canvas _backdrop = new() { IsHitTestVisible = false };
    private readonly System.Windows.Controls.Canvas _fences = new() { IsHitTestVisible = false };

    private HwndSource? _source;
    private IntPtr _worker;

    /// <summary>是否已成功寄生</summary>
    public bool IsAttached => _source != null && _worker != IntPtr.Zero;

    /// <summary>
    /// 寄生到壁纸层。层级校验不通过时**拒绝寄生** ——
    /// 宁可退回候选 A，也不能让背景盖住桌面图标。
    /// </summary>
    public bool Attach()
    {
        _worker = WallpaperLayer.Find();
        if (_worker == IntPtr.Zero)
        {
            Console.WriteLine("[FenceBg] 未找到壁纸层 WorkerW，候选 B 不可用");
            return false;
        }

        if (!WallpaperLayer.IsBelowIcons(_worker))
        {
            Console.WriteLine("[FenceBg] 壁纸层 Z 序不符合预期（可能遮挡图标），拒绝寄生");
            return false;
        }

        Win32.GetWindowRect(_worker, out var rect);

        try
        {
            var parameters = new HwndSourceParameters("zDesktopFenceBackground")
            {
                ParentWindow = _worker,
                WindowStyle = unchecked((int)(Win32.WS_CHILD | Win32.WS_VISIBLE)),
                ExtendedWindowStyle = Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE,
                PositionX = 0,
                PositionY = 0,
                Width = rect.Width,
                Height = rect.Height,
                UsesPerPixelOpacity = false,
            };

            var root = new System.Windows.Controls.Grid
            {
                // 壁纸读不到时的兜底底色
                Background = System.Windows.Media.Brushes.Black,
                IsHitTestVisible = false,
            };
            root.Children.Add(_backdrop);
            root.Children.Add(_fences);

            _source = new HwndSource(parameters) { RootVisual = root };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FenceBg] 创建 HwndSource 失败: {ex.Message}");
            _worker = IntPtr.Zero;
            return false;
        }

        Console.WriteLine($"[FenceBg] 已寄生到壁纸层 0x{_worker.ToInt64():X}，" +
                          $"尺寸 {rect.Width}x{rect.Height}");
        return true;
    }

    /// <summary>Explorer 重启后壁纸层被销毁，需重新寄生</summary>
    public bool ReattachIfNeeded()
    {
        if (IsAttached && Win32.IsWindow(_worker)) return true;

        Dispose();
        return Attach();
    }

    /// <summary>
    /// 按显示器设置底图。
    /// 本层不透明，必须自己把壁纸画出来；每屏壁纸可能不同，需逐屏铺，
    /// 用一张图拉伸铺满整个虚拟屏会让双屏画面错乱。
    /// </summary>
    public void SetBackdrops(IReadOnlyList<(Core.Fences.IconRect Rect, string? Path)> backdrops)
    {
        if (_source == null) return;

        _backdrop.Children.Clear();
        var dpi = Dpi;

        foreach (var (rect, path) in backdrops)
        {
            var image = new System.Windows.Controls.Image
            {
                Width = DpiHelper.ToDip(rect.Width, dpi),
                Height = DpiHelper.ToDip(rect.Height, dpi),
                Stretch = System.Windows.Media.Stretch.UniformToFill, // 与 Windows「填充」一致
                IsHitTestVisible = false,
                Source = LoadFrozen(path),
            };

            System.Windows.Controls.Canvas.SetLeft(image, DpiHelper.ToDip(rect.X, dpi));
            System.Windows.Controls.Canvas.SetTop(image, DpiHelper.ToDip(rect.Y, dpi));
            _backdrop.Children.Add(image);
        }
    }

    /// <summary>重绘全部分区底色。参数为图标空间（ListView 客户区物理像素）矩形。</summary>
    public void Render(IReadOnlyList<(Core.Fences.IconRect Rect, string Color, bool Collapsed)> fences)
    {
        if (_source == null) return;

        _fences.Children.Clear();
        var dpi = Dpi;

        foreach (var (rect, color, collapsed) in fences)
        {
            var accent = ParseColor(color);
            var height = collapsed ? Math.Min(32, rect.Height) : rect.Height;

            var body = new System.Windows.Controls.Border
            {
                Width = DpiHelper.ToDip(rect.Width, dpi),
                Height = DpiHelper.ToDip(height, dpi),
                CornerRadius = new CornerRadius(12),
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(collapsed ? (byte)96 : (byte)64, 20, 22, 34)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(120, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1.5),
                IsHitTestVisible = false,
            };

            System.Windows.Controls.Canvas.SetLeft(body, DpiHelper.ToDip(rect.X, dpi));
            System.Windows.Controls.Canvas.SetTop(body, DpiHelper.ToDip(rect.Y, dpi));
            _fences.Children.Add(body);
        }
    }

    /// <summary>显示器配置变化后重新贴合壁纸层尺寸</summary>
    public void FitToLayer()
    {
        if (_source == null || _worker == IntPtr.Zero) return;

        Win32.GetWindowRect(_worker, out var rect);
        Win32.SetWindowPos(_source.Handle, IntPtr.Zero, 0, 0, rect.Width, rect.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    private double Dpi => _source == null ? Win32.DefaultDpi : DpiHelper.GetWindowDpi(_source.Handle);

    private static System.Windows.Media.Imaging.BitmapImage? LoadFrozen(string? path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;

        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            // 立即加载并释放文件句柄，否则第三方壁纸工具换图时会因文件被占用而失败
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FenceBg] 底图加载失败，用纯色兜底: {ex.Message}");
            return null;
        }
    }

    private static System.Windows.Media.Color ParseColor(string hex)
    {
        try
        {
            if (!string.IsNullOrEmpty(hex) && hex.StartsWith('#') && hex.Length == 7)
            {
                return System.Windows.Media.Color.FromRgb(
                    Convert.ToByte(hex.Substring(1, 2), 16),
                    Convert.ToByte(hex.Substring(3, 2), 16),
                    Convert.ToByte(hex.Substring(5, 2), 16));
            }
        }
        catch
        {
            // 配色格式错误用品牌色兜底
        }
        return System.Windows.Media.Color.FromRgb(0x6c, 0x5c, 0xe7);
    }

    public void Dispose()
    {
        _source?.Dispose();
        _source = null;
        _worker = IntPtr.Zero;
    }
}
