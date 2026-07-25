using System.Windows;
using System.Windows.Controls;
using zDesktop.App.Panels;
using zDesktop.App.Tray;
using zDesktop.Core.DesktopIcons;
using zDesktop.Core.Layout;
using zDesktop.Core.Widgets;
using zDesktop.Shell.Automation;
using zDesktop.Shell.Classifier;
using zDesktop.Shell.ControlCenter;
using zDesktop.Shell.Desktop;
using zDesktop.Shell.DesktopIcons;
using zDesktop.Shell.DiskMapper;
using zDesktop.Shell.Hotkeys;
using zDesktop.Shell.IconManager;
using zDesktop.Shell.Interop;
using zDesktop.Shell.Launcher;
using zDesktop.Shell.Layout;
using zDesktop.Shell.Search;
using zDesktop.Shell.Widgets;
using zDesktop.Shell.WindowManager;
using zDesktop.Widgets.Calendar;
using zDesktop.Widgets.Clock;
using zDesktop.Widgets.Launcher;
using zDesktop.Widgets.SystemMonitor;
using zDesktop.Widgets.Todos;
using zDesktop.Widgets.WallpaperManager;
using zDesktop.Widgets.Weather;

namespace zDesktop.App;

/// <summary>
/// 应用入口 — 桌面接管 + 组件宿主 + 布局持久化 + 系统托盘
///
/// 启动流程：
/// 1. 注册所有可用组件到 WidgetRegistry
/// 2. 创建透明接管窗口 + 组件宿主
/// 3. 加载 layout.json — 有配置则恢复，无则用默认布局
/// 4. 订阅布局变更事件，拖拽/关闭时自动保存
/// 5. 启动系统托盘 — 右键菜单可隐藏/显示组件、退出
/// </summary>
public partial class App : Application
{
    private DesktopOverlayWindow? _overlay;
    private WidgetHost? _widgetHost;
    private DesktopIconLayer? _iconLayer;
    private DesktopSearchBar? _desktopSearchBar;
    private readonly WidgetRegistry _registry = new();
    private readonly LayoutStore _layoutStore = new();
    private readonly DesktopIconStore _iconStore = new();
    private TrayIconManager? _tray;
    private bool _widgetsVisible = true;
    private bool _iconsLoaded;
    /// <summary>
    /// 桌面图标模式：true=zDesktop 自渲染，false=系统原生。
    ///
    /// **默认必须为 false**（设计案 v3.0 §二 零破坏契约）：自渲染图标层会隐藏
    /// SHELLDLL_DefView，导致回收站/此电脑/副屏图标不可见、框选与 F2 改名失效。
    /// 该模式仅作为用户显式开启的实验特性保留，后续将随分区功能落地一并移除。
    /// </summary>
    private bool _zdesktopIconMode;

    // ===== 效率工具服务（单例长驻）=====
    private readonly AppIndex _appIndex = new();
    private readonly FileIndexService _fileIndex = new();
    private readonly WindowManagerService _windowManager = new();
    private readonly ControlCenterService _controlCenter = new();
    private readonly FileClassifierService _classifier = new();
    private readonly DiskMapperService _diskMapper = new();
    private readonly AutomationService _automation = new();
    private readonly IconManagerService _iconManager = new();
    private GlobalHotkeyService? _hotkeys;

    /// <summary>统一主窗口（懒创建，关闭时仅隐藏）</summary>
    private MainWindow? _mainWindow;

    /// <summary>是否正在退出程序（区分主窗口隐藏与真正关闭）</summary>
    private bool _isShuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 0. 异常退出检测 — 上次如果非正常退出，先恢复原生桌面图标层
        if (DesktopRestore.WasAbnormalExit())
        {
            Console.WriteLine("[App] 检测到上次异常退出，正在恢复原生桌面图标层...");
            DesktopRestore.RestoreNativeDesktopIcons();
        }

        // 安装崩溃保护 — 捕获未处理异常/系统信号，确保恢复原生桌面
        CrashGuard.Install(EmergencyRestore);

        // 1. 注册所有可用组件
        RegisterWidgets();

        // 2. 创建接管窗口 + 图标层 + 组件宿主
        _overlay = new DesktopOverlayWindow();
        _iconLayer = new DesktopIconLayer();
        _widgetHost = new WidgetHost();
        _desktopSearchBar = new DesktopSearchBar();

        // 分层组装：图标层在底（z 序低），组件层在上（z 序高），搜索框最上层右上角
        var grid = new Grid();
        // 默认原生图标模式 — 自渲染图标层收起，原生 SHELLDLL_DefView 保持可见可用
        _iconLayer.Visibility = _zdesktopIconMode ? Visibility.Visible : Visibility.Collapsed;
        grid.Children.Add(_iconLayer);   // 底层 — 桌面图标
        grid.Children.Add(_widgetHost);  // 上层 — 浮动组件
        // 搜索框 — 右上角常驻
        _desktopSearchBar.HorizontalAlignment = HorizontalAlignment.Right;
        _desktopSearchBar.VerticalAlignment = VerticalAlignment.Top;
        _desktopSearchBar.Margin = new Thickness(0, 16, 16, 0);
        grid.Children.Add(_desktopSearchBar);
        _overlay.Content = grid;

        // 桌面搜索框回车 → 打开主窗口全局搜索页并传入关键词
        _desktopSearchBar.SearchRequested += OnDesktopSearch;

        // HitTest：zDesktop 图标模式下始终捕获（空白区域交给图标层处理）；
        // 原生模式下仅组件区域捕获，空白透传给原生桌面
        _overlay.HitTestCallback = point =>
        {
            // 搜索框区域始终捕获
            if (_desktopSearchBar!.Visibility == Visibility.Visible &&
                IsPointInSearchBar(point)) return true;
            // 组件区域始终捕获
            if (_widgetHost!.HitTest(point)) return true;
            // zDesktop 图标模式 — 图标区域或空白都捕获（空白用于取消选中）
            if (_zdesktopIconMode && _iconLayer!.Visibility == Visibility.Visible)
                return true;
            // 原生模式 — 空白透传给原生桌面
            return false;
        };

        // 3. 加载布局：有配置则恢复，无则用默认
        var layout = _layoutStore.Load();
        if (layout != null && layout.Widgets.Count > 0)
        {
            RestoreLayout(layout);
            // 加载后立即落盘一次 — 确保配置迁移（如 v1→v2）结果持久化，
            // 不依赖退出时保存（异常退出时 OnExit 可能不执行）
            SaveLayout();
        }
        else
        {
            LoadDefaultLayout();
        }

        // 4. 订阅布局变更 → 自动保存
        _widgetHost.LayoutChanged += SaveLayout;
        // 组件设置按钮 → 弹出设置面板
        _widgetHost.SettingsRequested += OnWidgetSettingsRequested;
        // 图标层首次获得尺寸后加载桌面图标
        _iconLayer.SizeChanged += OnIconLayerSized;
        // overlay 就绪后（HWND 已就绪）隐藏原生桌面图标层
        _overlay.Ready += OnOverlayReady;
        // 显示器配置变更 → 重新定位组件
        _overlay.DisplayChanged += OnDisplayChanged;

        // 5. 启动系统托盘
        _tray = new TrayIconManager();
        _tray.ToggleWidgets += OnToggleWidgets;
        _tray.ToggleStartup += OnToggleStartup;
        _tray.ShowWidgetPanel += OnShowWidgetPanel;
        _tray.ToggleIconMode += OnToggleIconMode;
        // 功能中心入口 — 统一通过主窗口导航
        _tray.ShowGlobalSearch += () => OnShowMainWindow("global-search");
        _tray.ShowWindowManager += () => OnShowMainWindow("window-manager");
        _tray.ShowControlCenter += () => OnShowMainWindow("control-center");
        _tray.ShowFileClassify += () => OnShowMainWindow("file-classify");
        _tray.ShowDiskMapper += () => OnShowMainWindow("disk-mapper");
        _tray.ShowAutomation += () => OnShowMainWindow("automation-rules");
        _tray.ShowIconManager += () => OnShowMainWindow("icon-manager");
        _tray.ExitRequested += OnExitRequested;

        // 6. 效率工具初始化
        // 后台加载应用索引 + 文件索引（不阻塞 UI）
        _ = System.Threading.Tasks.Task.Run(() => _appIndex.Load());
        _ = _fileIndex.LoadAsync();
        // 启动自动化规则监控
        _automation.Start();
        Console.WriteLine("[App] 效率工具服务已初始化：应用索引 / 文件索引 / 自动化监控");

        _overlay.Show();

        // 7. overlay 显示后注册全局热键（需 HWND 就绪）
        _overlay.Ready += RegisterHotkeys;

        Console.WriteLine("[App] zDesktop 已启动 — 桌面图标 + 组件 + 布局持久化 + 系统托盘 + 效率工具已就绪");
    }

    // ===== 全局热键 =====

    /// <summary>注册全局热键 — overlay HWND 就绪后调用</summary>
    private void RegisterHotkeys()
    {
        if (_overlay == null) return;
        try
        {
            var src = (System.Windows.Interop.HwndSource)System.Windows.Interop.HwndSource.FromVisual(_overlay);
            if (src?.Handle == null || src.Handle == IntPtr.Zero) return;

            _hotkeys = new GlobalHotkeyService(src.Handle);

            // Alt+Space → 全局搜索（跳转主窗口搜索页）
            _hotkeys.Register(Win32.MOD_ALT, Win32.VK_SPACE,
                () => OnShowMainWindow("global-search"),
                "全局搜索 (Alt+Space)");

            // Ctrl+Space → 控制中心（跳转主窗口控制中心页）
            _hotkeys.Register(Win32.MOD_CONTROL, Win32.VK_SPACE,
                () => OnShowMainWindow("control-center"),
                "控制中心 (Ctrl+Space)");

            Console.WriteLine("[App] 全局热键已注册：Alt+Space 全局搜索 / Ctrl+Space 控制中心");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[App] 全局热键注册失败: {ex.Message}");
        }
    }

    // ===== 主窗口入口 =====

    /// <summary>
    /// 创建或激活主窗口，可选跳转到指定导航页
    /// </summary>
    /// <param name="navId">目标导航标识（null 表示仅显示，不切换页面）</param>
    private void OnShowMainWindow(string? navId = null)
    {
        Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_mainWindow == null)
            {
                _mainWindow = new MainWindow(
                    _appIndex, _fileIndex, _windowManager, _controlCenter,
                    _classifier, _diskMapper, _automation, _iconManager,
                    _registry, _widgetHost!, id => _registry.Create(id)!);

                // 透传专注模式切换
                _mainWindow.FocusModeToggled += OnFocusModeToggled;
                // 透传组件布局变更
                _mainWindow.WidgetLayoutChanged += SaveLayout;

                // 主窗口关闭时仅隐藏（不退出程序），托盘退出才真正 Shutdown
                _mainWindow.Closing += (_, e) =>
                {
                    if (!_isShuttingDown)
                    {
                        e.Cancel = true;
                        _mainWindow.Hide();
                    }
                };
            }

            if (!string.IsNullOrEmpty(navId))
                _mainWindow.Navigate(navId);

            _mainWindow.Show();
            _mainWindow.Activate();
        }));
    }

    /// <summary>桌面搜索框回车提交 — 打开主窗口全局搜索页并传入关键词</summary>
    private void OnDesktopSearch(string query)
    {
        OnShowMainWindow("global-search");
        // OnShowMainWindow 内部用 BeginInvoke 异步创建/显示主窗口，
        // 在同一 Dispatcher 队列中追加搜索词设置，确保主窗口就绪后再注入查询
        Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            _mainWindow?.NavigateWithSearch("global-search", query);
        }));
    }

    /// <summary>判断点是否落在桌面搜索框范围内（供 HitTestCallback 使用）</summary>
    private bool IsPointInSearchBar(System.Windows.Point point)
    {
        if (_desktopSearchBar == null || _desktopSearchBar.Visibility != Visibility.Visible) return false;
        if (_overlay == null) return false;
        try
        {
            // point 为 overlay 窗口坐标，转换到搜索框局部坐标后判断是否命中
            var transform = _overlay.TransformToVisual(_desktopSearchBar);
            var local = transform.Transform(point);
            return _desktopSearchBar.HitTest(local);
        }
        catch
        {
            // 视觉树未就绪时安全降级
            return false;
        }
    }

    /// <summary>专注模式切换 — 隐藏/显示所有桌面组件</summary>
    private void OnFocusModeToggled(bool enabled)
    {
        if (_widgetHost == null) return;
        foreach (var container in _widgetHost.Containers)
        {
            container.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_iconLayer != null)
            _iconLayer.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        Console.WriteLine($"[App] 专注模式: {(enabled ? "已开启" : "已关闭")}");
    }

    // ===== 桌面图标 =====

    /// <summary>切换桌面图标模式：zDesktop 渲染 ↔ 系统原生</summary>
    private void OnToggleIconMode()
    {
        if (_overlay == null || _iconLayer == null || _tray == null) return;

        _zdesktopIconMode = !_zdesktopIconMode;

        if (_zdesktopIconMode)
        {
            // zDesktop 渲染模式：隐藏原生，显示自渲染图标层
            _iconLayer.Visibility = Visibility.Visible;
            _overlay.HideNativeIcons();
        }
        else
        {
            // 系统原生模式：显示原生，隐藏自渲染图标层
            _iconLayer.Visibility = Visibility.Collapsed;
            _overlay.ShowNativeIcons();
        }

        _tray.UpdateIconModeCheck(_zdesktopIconMode);
        Console.WriteLine($"[App] 桌面图标模式: {(_zdesktopIconMode ? "zDesktop 渲染" : "系统原生")}");
    }

    /// <summary>图标层首次获得尺寸后加载桌面图标（仅一次）</summary>
    private void OnIconLayerSized(object sender, SizeChangedEventArgs e)
    {
        if (_iconsLoaded || _iconLayer == null || _iconLayer.ActualWidth < 1) return;
        _iconsLoaded = true;
        LoadDesktopIcons();
    }

    /// <summary>
    /// overlay 就绪（HWND 已就绪）。
    ///
    /// 此处**不再**无条件隐藏原生桌面图标层 —— 零破坏契约要求默认保留原生桌面。
    /// 仅当用户显式切到 zDesktop 自渲染模式时才隐藏（见 <see cref="OnToggleIconMode"/>）。
    /// </summary>
    private void OnOverlayReady()
    {
        if (_zdesktopIconMode) _overlay?.HideNativeIcons();
        // 标记 zDesktop 正在运行（用于下次启动检测异常退出）
        DesktopRestore.MarkRunning();
    }

    /// <summary>显示器配置变更 — 重新定位超出工作区的组件</summary>
    private void OnDisplayChanged()
    {
        Console.WriteLine("[App] 显示器配置变更，重新定位组件");
        _widgetHost?.RepositionWidgets();
    }

    /// <summary>扫描桌面、提取图标、恢复布局、隐藏原生图标层</summary>
    private void LoadDesktopIcons()
    {
        if (_iconLayer == null || _overlay == null) return;

        // 设置右键菜单的所有者窗口句柄回调
        DesktopIconItem.GetOwnerHwnd = () =>
        {
            var src = (System.Windows.Interop.HwndSource)System.Windows.Interop.HwndSource.FromVisual(_overlay);
            return src?.Handle ?? IntPtr.Zero;
        };

        var scanned = _iconStore.Scan();
        var layout = _iconStore.LoadLayout();
        var posMap = (layout?.Icons ?? Enumerable.Empty<IconLayoutEntry>())
            .ToDictionary(i => i.SourcePath, i => (i.X, i.Y), StringComparer.OrdinalIgnoreCase);

        var hasLayout = layout != null && layout.Icons.Count > 0;
        var placed = 0;

        foreach (var (info, icon) in scanned)
        {
            var item = new DesktopIconItem(info, icon);
            if (posMap.TryGetValue(info.SourcePath, out var pos))
            {
                _iconLayer.AddIcon(item, pos.X, pos.Y);
                placed++;
            }
            else
            {
                _iconLayer.AddIcon(item, 0, 0); // 待默认排列
            }
        }

        // 无布局或全部未定位 → 默认网格排列
        if (!hasLayout || placed == 0)
        {
            _iconLayer.ArrangeDefault();
        }

        _iconLayer.LayoutChanged += SaveIconLayout;

        Console.WriteLine($"[App] 桌面图标已加载：{scanned.Count} 个{(hasLayout ? "（已恢复位置）" : "（默认排列）")}");
    }

    /// <summary>
    /// 保存图标布局到 icons-layout.json
    ///
    /// 未加载过图标时必须跳过：原生图标模式下图标层为空，若照常落盘会把用户此前
    /// 保存的图标位置覆盖成空布局。
    /// </summary>
    private void SaveIconLayout()
    {
        if (_iconLayer == null || !_iconsLoaded) return;
        _iconStore.SaveLayout(_iconLayer.GetCurrentLayout());
    }

    // ===== 系统托盘事件处理 =====

    /// <summary>切换所有组件可见性</summary>
    private void OnToggleWidgets()
    {
        if (_widgetHost == null || _tray == null) return;

        _widgetsVisible = !_widgetsVisible;

        foreach (var container in _widgetHost.Containers)
        {
            container.Visibility = _widgetsVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        _tray.UpdateToggleText(_widgetsVisible);
        Console.WriteLine($"[App] 组件可见性: {(_widgetsVisible ? "显示" : "隐藏")}");
    }

    /// <summary>切换开机自启</summary>
    private void OnToggleStartup()
    {
        if (_tray == null) return;

        var enabled = StartupHelper.Toggle();
        _tray.UpdateStartupCheck(enabled);

        var msg = enabled ? "已启用开机自启" : "已禁用开机自启";
        _tray.ShowBalloon("zDesktop", msg);
        Console.WriteLine($"[App] {msg}");
    }

    /// <summary>打开组件面板 — 跳转主窗口桌面组件页</summary>
    private void OnShowWidgetPanel()
    {
        OnShowMainWindow("desktop-widgets");
    }

    /// <summary>组件设置按钮被点击 — 弹出设置面板，应用后持久化</summary>
    private void OnWidgetSettingsRequested(WidgetContainer container)
    {
        Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            var window = new WidgetSettingsWindow(container.Widget);
            window.ConfigApplied += SaveLayout;
            window.Show();
        }));
    }

    /// <summary>退出程序 — 设置关闭标志后真正 Shutdown</summary>
    private void OnExitRequested()
    {
        _isShuttingDown = true;
        Shutdown();
    }

    // ===== 组件注册与布局 =====

    /// <summary>注册所有可用组件类型</summary>
    private void RegisterWidgets()
    {
        _registry.Register("clock", () => new ClockWidget());
        _registry.Register("calendar", () => new CalendarWidget());
        _registry.Register("system-monitor", () => new SystemMonitorWidget());
        _registry.Register("todo", () => new TodoWidget());
        _registry.Register("quick-launcher", () => new QuickLauncherWidget());
        _registry.Register("weather", () => new WeatherWidget());
        _registry.Register("wallpaper-manager", () => new WallpaperWidget());
    }

    /// <summary>从保存的布局配置恢复桌面</summary>
    private void RestoreLayout(LayoutConfig layout)
    {
        foreach (var entry in layout.Widgets)
        {
            var widget = _registry.Create(entry.WidgetId);
            if (widget == null)
            {
                Console.WriteLine($"[App] 跳过未知组件: {entry.WidgetId}");
                continue;
            }

            _widgetHost!.AddWidget(widget, new WidgetSettings
            {
                WidgetId = entry.WidgetId,
                X = entry.X,
                Y = entry.Y,
                Width = entry.Width,
                Height = entry.Height,
                IsVisible = entry.IsVisible,
                Config = entry.Config ?? new(),
            });
        }
        Console.WriteLine($"[App] 已恢复布局：{layout.Widgets.Count} 个组件");
    }

    /// <summary>首次启动的默认布局 — 所有组件统一宽度 280，左右两列对齐排列</summary>
    private void LoadDefaultLayout()
    {
        // 左列：时钟 + 日历
        _widgetHost!.AddWidget(new ClockWidget(), new WidgetSettings
        {
            WidgetId = "clock", X = 80, Y = 80, Width = 280, Height = 150,
        });
        _widgetHost.AddWidget(new CalendarWidget(), new WidgetSettings
        {
            WidgetId = "calendar", X = 80, Y = 250, Width = 280, Height = 300,
        });
        // 右列：系统监控 + 待办
        _widgetHost.AddWidget(new SystemMonitorWidget(), new WidgetSettings
        {
            WidgetId = "system-monitor", X = 1580, Y = 80, Width = 280, Height = 220,
        });
        _widgetHost.AddWidget(new TodoWidget(), new WidgetSettings
        {
            WidgetId = "todo", X = 1580, Y = 320, Width = 280, Height = 340,
        });
        Console.WriteLine("[App] 已加载默认布局");
    }

    /// <summary>保存当前布局到 layout.json</summary>
    private void SaveLayout()
    {
        if (_widgetHost == null) return;
        var config = _widgetHost.GetCurrentLayout();
        _layoutStore.Save(config);
    }

    /// <summary>紧急恢复 — 崩溃或异常退出时由 CrashGuard 调用</summary>
    private void EmergencyRestore()
    {
        _overlay?.ShowNativeIcons();
        DesktopRestore.ClearRunningFlag();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 停止效率工具
        _automation.Stop();
        _hotkeys?.Dispose();

        SaveLayout();
        SaveIconLayout();
        _overlay?.ShowNativeIcons(); // 恢复原生桌面图标层
        DesktopRestore.ClearRunningFlag(); // 清除运行标记
        _tray?.Dispose();
        _overlay?.Close();
        base.OnExit(e);
    }
}
