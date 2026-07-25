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
    /// <summary>
    /// 每个显示器一个覆盖层（设计案 v3.1 §八）。
    /// v2.3 只在主屏建覆盖层，副屏完全没有 zDesktop。
    /// </summary>
    private readonly List<MonitorOverlay> _overlays = new();

    /// <summary>主显示器覆盖层 —— 图标层、搜索框等单例图层只挂在它上面</summary>
    private MonitorOverlay? _primary;

    /// <summary>主屏覆盖层窗口的便捷访问（图标层还原等操作只针对原生图标层，与屏无关）</summary>
    private DesktopOverlayWindow? _overlay => _primary?.Window;

    /// <summary>主屏组件宿主 —— 主窗口的组件页仍按主屏操作（多屏组件管理属 M5）</summary>
    private WidgetHost? _widgetHost => _primary?.Host;

    private DesktopIconLayer? _iconLayer;
    private DesktopSearchBar? _desktopSearchBar;

    /// <summary>全屏应用检测 —— 全屏期间隐藏覆盖层并停摆定时器</summary>
    private FullscreenGuard? _fullscreenGuard;

    /// <summary>显示器重建防抖 —— 热插拔时系统会连发多条消息</summary>
    private System.Windows.Threading.DispatcherTimer? _rebuildDebounce;
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

    /// <summary>系统状态还原账本 —— 强杀/崩溃/卸载三条路径共用</summary>
    private readonly RestoreJournal _restoreJournal = RestoreJournal.Load();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 0a. 还原模式 —— 供卸载程序调用：zDesktop.App.exe --restore
        //     只还原系统状态然后立即退出，不启动任何 UI
        if (e.Args.Any(a => string.Equals(a, "--restore", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("[App] 还原模式：正在还原系统状态并退出");
            _restoreJournal.RestoreAll();
            DesktopRestore.ClearRunningFlag();
            _isShuttingDown = true;
            Shutdown();
            return;
        }

        // 0b. 异常退出检测 —— 上次非正常退出（含 taskkill /F）时按账本还原。
        //     taskkill 触发 TerminateProcess，进程内钩子一律不执行，
        //     因此只能靠「动手前落盘的账本」在下次启动时兜底。
        if (DesktopRestore.WasAbnormalExit())
        {
            Console.WriteLine("[App] 检测到上次异常退出，按还原账本恢复系统状态…");
            _restoreJournal.RestoreAll();
        }

        // 安装崩溃保护 — 捕获未处理异常/系统信号，确保恢复原生桌面
        CrashGuard.Install(EmergencyRestore);

        // 1. 注册所有可用组件
        RegisterWidgets();

        // 2. 按显示器建立覆盖层（每屏一个），并恢复组件布局
        BuildOverlays();

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

        // 7. 全屏检测 — 全屏应用期间隐藏全部覆盖层并停摆定时器（零存在感）
        _fullscreenGuard = new FullscreenGuard();
        _fullscreenGuard.FullscreenChanged += OnFullscreenChanged;
        _fullscreenGuard.Start();

        Console.WriteLine($"[App] zDesktop 已启动 — {_overlays.Count} 个显示器覆盖层 / 组件 / 托盘 / 效率工具已就绪");
    }

    // ===== 覆盖层集合管理（多屏）=====

    /// <summary>
    /// 枚举显示器并为每个显示器建立覆盖层，随后恢复组件布局。
    ///
    /// 图标层与桌面搜索框是单例图层，只挂在主屏覆盖层上。
    /// </summary>
    private void BuildOverlays()
    {
        var monitors = MonitorSet.Enumerate();
        var primaryMonitor = MonitorSet.Primary(monitors);

        foreach (var monitor in monitors)
        {
            var overlay = new MonitorOverlay(monitor);

            overlay.Host.LayoutChanged += SaveLayout;
            overlay.Host.SettingsRequested += OnWidgetSettingsRequested;
            overlay.Window.DisplayChanged += OnDisplayChanged;
            overlay.Window.ExplorerRestarted += OnExplorerRestarted;

            if (monitor.Key == primaryMonitor.Key)
            {
                _primary = overlay;
                AttachPrimaryLayers(overlay);
                overlay.Window.Ready += OnOverlayReady;
                overlay.Window.Ready += RegisterHotkeys;
            }

            // 每个覆盖层用自己的组件宿主做命中测试；主屏额外算上图标层与搜索框
            var isPrimary = monitor.Key == primaryMonitor.Key;
            overlay.Window.HitTestCallback = point => HitTestOverlay(overlay, isPrimary, point);

            _overlays.Add(overlay);
        }

        // 兜底：万一没有任何显示器被标记为主屏，取第一个
        _primary ??= _overlays.FirstOrDefault();

        // 恢复布局：有配置则按显示器归属还原，无则用默认
        var layout = _layoutStore.Load();
        if (layout != null && layout.Widgets.Count > 0)
        {
            RestoreLayout(layout);
            // 加载后立即落盘一次 — 确保配置迁移（v3→v4 补 MonitorKey）结果持久化，
            // 不依赖退出时保存（异常退出时 OnExit 可能不执行）
            SaveLayout();
        }
        else
        {
            LoadDefaultLayout();
        }

        foreach (var overlay in _overlays)
            overlay.Window.Show();
    }

    /// <summary>把图标层与桌面搜索框挂到主屏覆盖层</summary>
    private void AttachPrimaryLayers(MonitorOverlay overlay)
    {
        _iconLayer = new DesktopIconLayer
        {
            // 默认原生图标模式 — 自渲染图标层收起，原生 SHELLDLL_DefView 保持可见可用
            Visibility = _zdesktopIconMode ? Visibility.Visible : Visibility.Collapsed,
        };
        _iconLayer.SizeChanged += OnIconLayerSized;
        overlay.InsertLayerBelowWidgets(_iconLayer);

        _desktopSearchBar = new DesktopSearchBar
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 16, 16, 0),
        };
        _desktopSearchBar.SearchRequested += OnDesktopSearch;
        overlay.AddLayerAboveWidgets(_desktopSearchBar);
    }

    /// <summary>
    /// 单个覆盖层的命中测试：组件区域捕获鼠标，其余透传给原生桌面。
    /// 主屏额外考虑搜索框与自渲染图标层。
    /// </summary>
    private bool HitTestOverlay(MonitorOverlay overlay, bool isPrimary, System.Windows.Point point)
    {
        if (isPrimary)
        {
            if (_desktopSearchBar is { Visibility: Visibility.Visible } && IsPointInSearchBar(point))
                return true;
        }

        if (overlay.Host.HitTest(point)) return true;

        // zDesktop 自渲染图标模式（实验）— 整屏捕获，空白用于取消选中
        if (isPrimary && _zdesktopIconMode && _iconLayer is { Visibility: Visibility.Visible })
            return true;

        // 原生模式 — 透传给原生桌面
        return false;
    }

    /// <summary>关闭并清理全部覆盖层（重建或退出时调用）</summary>
    private void CloseOverlays()
    {
        foreach (var overlay in _overlays)
        {
            overlay.Host.LayoutChanged -= SaveLayout;
            overlay.Host.SettingsRequested -= OnWidgetSettingsRequested;
            overlay.Window.DisplayChanged -= OnDisplayChanged;
            overlay.Window.ExplorerRestarted -= OnExplorerRestarted;
            overlay.Window.Close();
        }
        _overlays.Clear();
        _primary = null;
        _iconLayer = null;
        _desktopSearchBar = null;
        _iconsLoaded = false;
    }

    /// <summary>全屏状态变化 — 所有覆盖层统一让位/恢复</summary>
    private void OnFullscreenChanged(bool isFullscreen)
    {
        foreach (var overlay in _overlays)
            overlay.Window.SetFullscreenYield(isFullscreen);
    }

    /// <summary>Explorer 重启 — 图标层数据随原生桌面一同重建，需要重新扫描</summary>
    private void OnExplorerRestarted()
    {
        Console.WriteLine("[App] Explorer 已重启，覆盖层完成重锚");
        if (_zdesktopIconMode && _iconLayer != null)
        {
            // 自渲染模式下原生图标层被重建为可见，需重新隐藏
            _overlay?.HideNativeIcons();
        }
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

    /// <summary>专注模式切换 — 隐藏/显示全部显示器上的桌面组件</summary>
    private void OnFocusModeToggled(bool enabled)
    {
        foreach (var overlay in _overlays)
        {
            foreach (var container in overlay.Host.Containers)
                container.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        }

        if (_iconLayer != null && _zdesktopIconMode)
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
            // zDesktop 渲染模式：隐藏原生，显示自渲染图标层。
            // 先记账再动手 —— 若此刻被强杀，下次启动才能知道要还原什么。
            _restoreJournal.MarkNativeIconsHidden();
            _iconLayer.Visibility = Visibility.Visible;
            _overlay.HideNativeIcons();
        }
        else
        {
            // 系统原生模式：显示原生，隐藏自渲染图标层
            _iconLayer.Visibility = Visibility.Collapsed;
            _overlay.ShowNativeIcons();
            _restoreJournal.ClearNativeIconsHidden();
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
        if (_zdesktopIconMode)
        {
            _restoreJournal.MarkNativeIconsHidden(); // 先记账再动手
            _overlay?.HideNativeIcons();
        }
        // 标记 zDesktop 正在运行（用于下次启动检测异常退出）
        DesktopRestore.MarkRunning();
    }

    /// <summary>
    /// 显示器配置变更（热插拔 / 分辨率 / 缩放）— 防抖后整体重建覆盖层集合。
    ///
    /// 系统在一次变更中会连发多条 WM_DISPLAYCHANGE / WM_DPICHANGED，
    /// 且各覆盖层都会转发，故用 500ms 防抖合并为一次重建。
    /// </summary>
    private void OnDisplayChanged()
    {
        _rebuildDebounce ??= CreateRebuildDebounce();
        _rebuildDebounce.Stop();
        _rebuildDebounce.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreateRebuildDebounce()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RebuildOverlays();
        };
        return timer;
    }

    /// <summary>
    /// 重建覆盖层集合 — 先落盘当前布局，销毁旧覆盖层，按新显示器配置重建后还原。
    ///
    /// 组件实例会被重新创建（而非搬迁），因为组件宿主与覆盖层窗口一一绑定。
    /// 布局按 MonitorKey 还原，显示器还在则回到原屏，已移除则落到主屏。
    /// </summary>
    private void RebuildOverlays()
    {
        Console.WriteLine("[App] 显示器配置变更，重建覆盖层集合");
        SaveLayout();
        CloseOverlays();
        BuildOverlays();

        foreach (var overlay in _overlays)
            overlay.Host.RepositionWidgets();

        // 重建期间若正处于全屏让位状态，新覆盖层需要立即跟上
        if (_fullscreenGuard?.IsFullscreen == true)
            OnFullscreenChanged(true);
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

    /// <summary>
    /// 从保存的布局配置恢复桌面组件。
    ///
    /// 按 <see cref="WidgetLayoutEntry.MonitorKey"/> 分派到对应显示器的宿主：
    /// - key 为空（v3 及更早的配置）→ 主屏
    /// - key 指向的显示器已移除 → 落到主屏（设计案 v3.1 §五 孤儿处理）
    /// </summary>
    private void RestoreLayout(LayoutConfig layout)
    {
        if (_primary == null) return;

        var orphaned = 0;

        foreach (var entry in layout.Widgets)
        {
            var widget = _registry.Create(entry.WidgetId);
            if (widget == null)
            {
                Console.WriteLine($"[App] 跳过未知组件: {entry.WidgetId}");
                continue;
            }

            var target = ResolveHost(entry.MonitorKey, ref orphaned);

            target.AddWidget(widget, new WidgetSettings
            {
                WidgetId = entry.WidgetId,
                MonitorKey = target.MonitorKey,
                X = entry.X,
                Y = entry.Y,
                Width = entry.Width,
                Height = entry.Height,
                IsVisible = entry.IsVisible,
                Config = entry.Config ?? new(),
            });
        }

        Console.WriteLine($"[App] 已恢复布局：{layout.Widgets.Count} 个组件" +
                          (orphaned > 0 ? $"（{orphaned} 个因显示器已移除迁至主屏）" : ""));
    }

    /// <summary>按显示器标识找宿主；找不到则回落主屏并计数</summary>
    private WidgetHost ResolveHost(string monitorKey, ref int orphaned)
    {
        if (string.IsNullOrEmpty(monitorKey)) return _primary!.Host;

        var match = _overlays.FirstOrDefault(o => o.Monitor.Key == monitorKey);
        if (match != null) return match.Host;

        orphaned++;
        return _primary!.Host;
    }

    /// <summary>
    /// 首次启动的默认布局 — 全部落在主屏，左右两列对齐。
    ///
    /// 坐标按主屏工作区实际宽度计算，不再硬编码 1580（那个值在 1080p 上会把
    /// 右列组件推出屏幕外）。
    /// </summary>
    private void LoadDefaultLayout()
    {
        if (_primary == null) return;

        var host = _primary.Host;
        var (_, _, workWidth, _) = _primary.Monitor.WorkAreaDip;

        const double widgetWidth = 280;
        const double margin = 80;
        // 右列贴右边缘；工作区过窄时退化为紧挨左列，保证始终可见
        var rightX = Math.Max(margin + widgetWidth + 20, workWidth - widgetWidth - margin);

        host.AddWidget(new ClockWidget(), new WidgetSettings
        {
            WidgetId = "clock", MonitorKey = host.MonitorKey,
            X = margin, Y = 80, Width = widgetWidth, Height = 150,
        });
        host.AddWidget(new CalendarWidget(), new WidgetSettings
        {
            WidgetId = "calendar", MonitorKey = host.MonitorKey,
            X = margin, Y = 250, Width = widgetWidth, Height = 300,
        });
        host.AddWidget(new SystemMonitorWidget(), new WidgetSettings
        {
            WidgetId = "system-monitor", MonitorKey = host.MonitorKey,
            X = rightX, Y = 80, Width = widgetWidth, Height = 220,
        });
        host.AddWidget(new TodoWidget(), new WidgetSettings
        {
            WidgetId = "todo", MonitorKey = host.MonitorKey,
            X = rightX, Y = 320, Width = widgetWidth, Height = 340,
        });

        Console.WriteLine($"[App] 已加载默认布局（主屏工作区宽 {workWidth:F0} DIP，右列 X={rightX:F0}）");
    }

    /// <summary>
    /// 保存当前布局到 layout.json —— 聚合全部显示器上的组件条目。
    ///
    /// 覆盖层集合为空时直接返回，避免重建过程中把空布局写回去覆盖用户配置。
    /// </summary>
    private void SaveLayout()
    {
        if (_overlays.Count == 0) return;

        var config = new LayoutConfig();
        foreach (var overlay in _overlays)
            config.Widgets.AddRange(overlay.Host.GetEntries());

        _layoutStore.Save(config);
    }

    /// <summary>
    /// 紧急恢复 — 崩溃或异常退出时由 CrashGuard 调用。
    ///
    /// 只做「还原原生桌面」这一件必须做的事，且不依赖 WPF 消息循环仍然健康：
    /// 直接按句柄还原图标层可见性，不走 Dispatcher。
    /// </summary>
    private void EmergencyRestore()
    {
        try
        {
            _overlay?.ShowNativeIcons();
        }
        catch
        {
            // 崩溃路径上不允许再抛异常，下面的账本还原是无窗口依赖的兜底
        }

        _restoreJournal.RestoreAll();
        DesktopRestore.ClearRunningFlag();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 停止全部定时器与后台服务
        _fullscreenGuard?.Dispose();
        _rebuildDebounce?.Stop();
        _automation.Stop();
        _hotkeys?.Dispose();

        SaveLayout();
        SaveIconLayout();

        // 还原原生桌面图标层 —— 无论此前是否切到过自渲染模式都执行，
        // 保证零破坏契约在退出路径上一定兑现
        _overlay?.ShowNativeIcons();
        _restoreJournal.ClearNativeIconsHidden();
        DesktopRestore.ClearRunningFlag();

        _tray?.Dispose();
        CloseOverlays();

        base.OnExit(e);
    }
}
