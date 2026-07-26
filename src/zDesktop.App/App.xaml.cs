using System.Windows;
using System.Windows.Controls;
using zDesktop.App.Panels;
using zDesktop.App.Tray;
using zDesktop.Core.DesktopIcons;
using zDesktop.Core.Layout;
using zDesktop.Core.Widgets;
using zDesktop.Shell.Automation;
using zDesktop.Shell.Classifier;
using zDesktop.Shell.Desktop;
using zDesktop.Shell.DesktopIcons;
using zDesktop.Shell.Fences;
using zDesktop.Shell.Hotkeys;
using zDesktop.Shell.Interop;
using zDesktop.Shell.Launcher;
using zDesktop.Shell.Layout;
using zDesktop.Shell.Search;
using zDesktop.Shell.Widgets;
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

    /// <summary>每屏的分区层，键为显示器稳定标识</summary>
    private readonly Dictionary<string, FenceLayer> _fenceLayers = new();
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
    private readonly FileClassifierService _classifier = new();
    private readonly AutomationService _automation = new();
    private GlobalHotkeyService? _hotkeys;

    /// <summary>统一主窗口（懒创建，关闭时仅隐藏）</summary>
    private MainWindow? _mainWindow;

    /// <summary>是否正在退出程序（区分主窗口隐藏与真正关闭）</summary>
    private bool _isShuttingDown;

    /// <summary>系统状态还原账本 —— 强杀/崩溃/卸载三条路径共用</summary>
    private readonly RestoreJournal _restoreJournal = RestoreJournal.Load();

    /// <summary>分区功能总控（M3）。原生控制器连不上时整体降级为不可用。</summary>
    private FenceController? _fences;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // zDesktop 是托盘常驻程序，绝不能因为「最后一个窗口关闭」就退出。
        // WPF 默认 OnLastWindowClose 会在这些场景把整个程序关掉：
        // 关闭重命名对话框、分区背景层探测失败后关闭该窗口、主窗口尚未创建时关掉设置窗。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

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

        // 0a-2. 卸载资产处置 —— 供卸载程序调用：
        //       zDesktop.App.exe --uninstall-cleanup keep|folders|restore
        var cleanupArg = e.Args.FirstOrDefault(a =>
            a.StartsWith("--uninstall-cleanup", StringComparison.OrdinalIgnoreCase));
        if (cleanupArg != null)
        {
            RunUninstallCleanup(e.Args);
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

        // 2. 分区功能初始化 —— 必须在建覆盖层之前，因为每屏要挂一个分区层
        _fences = new FenceController(_restoreJournal);
        _fences.Initialize();
        _fences.RenameRequested += OnFenceRenameRequested;

        // 分区变动后让已有组件让开：新建的分区可能正好盖在组件上，
        // 此时该让开的是组件 —— 分区位置是用户刚亲手画的，组件位置是历史遗留
        _fences.BackgroundInvalidated += OnFencesChangedApplyAvoidance;

        // 3. 按显示器建立覆盖层（每屏一个），并恢复组件布局
        BuildOverlays();

        // 5. 启动系统托盘
        _tray = new TrayIconManager();
        _tray.ToggleWidgets += OnToggleWidgets;
        _tray.ToggleStartup += OnToggleStartup;
        _tray.ShowWidgetPanel += OnShowWidgetPanel;
        _tray.ToggleIconMode += OnToggleIconMode;
        // 功能入口 — 统一通过主窗口导航
        _tray.ShowGlobalSearch += () => OnShowMainWindow("global-search");
        _tray.ShowFileClassify += () => OnShowMainWindow("file-classify");
        _tray.ShowAutomation += () => OnShowMainWindow("automation-rules");
        _tray.ShowSettings += () => OnShowMainWindow("settings");
        _tray.ToggleFenceEditMode += OnToggleFenceEditMode;
        _tray.OrganizeFences += OnOrganizeFences;
        _tray.UndoOrganize += OnUndoOrganize;
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

        // 8. 分区开始工作（焦点驱动轮询 + 首次归位与合成）
        _fences?.Start();

        // 9. 首次运行引导（§六）—— 延后到界面都就绪之后再弹
        Current.Dispatcher.BeginInvoke(new Action(ShowOnboardingIfNeeded),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (_fences is { IsAvailable: true, IsBlockedByAutoArrange: true })
        {
            _tray?.ShowBalloon("zDesktop",
                "桌面的「自动排列图标」已开启，分区功能无法生效。请右键桌面 → 查看 → 取消勾选「自动排列图标」。");
        }

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

            // 分区层插到组件宿主下方：组件应当浮在分区标题栏之上
            var fenceLayer = new FenceLayer();
            overlay.InsertLayerBelowWidgets(fenceLayer);
            _fenceLayers[monitor.Key] = fenceLayer;
            _fences?.AttachLayer(fenceLayer, monitor.Key);

            // 组件避让分区：拖拽松手时若压住分区超过 30%，自动贴到分区外缘。
            // 组件压在分区上会遮住分区里的原生图标，用户点不到就等于零破坏契约被破坏。
            var key = monitor.Key;
            overlay.Host.FenceBoxProvider = () =>
                _fences?.FenceBoxesOn(key) ?? Array.Empty<zDesktop.Core.Layout.LayoutBox>();

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

        // 分区：默认态只有标题栏命中，编辑模式下整层命中
        if (_fenceLayers.TryGetValue(overlay.Monitor.Key, out var fenceLayer) && fenceLayer.HitTest(point))
            return true;

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
        // 先从分区总控摘掉旧层，否则它会持有已销毁的层，编辑模式状态随之错乱
        _fences?.DetachLayers();

        _overlays.Clear();
        _fenceLayers.Clear();
        _primary = null;
        _iconLayer = null;
        _desktopSearchBar = null;
        _iconsLoaded = false;
    }

    /// <summary>分区变动后，让被压住的组件让开（设计案 v3.1 §3.1）</summary>
    private void OnFencesChangedApplyAvoidance()
    {
        var moved = 0;
        foreach (var overlay in _overlays)
            moved += overlay.Host.ApplyAvoidanceToAll();

        if (moved > 0)
        {
            Console.WriteLine($"[App] {moved} 个组件已让开新的分区位置");
            SaveLayout();
        }
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

            // Alt+Space → 搜索/启动（设计案 v3.1 §3.2：全局搜索与快速启动器合并为同一入口）
            // 目前跳转主窗口搜索页；M7 会换成轻量启动器窗口，届时不再打开设置窗口。
            _hotkeys.Register(Win32.MOD_ALT, Win32.VK_SPACE,
                () => OnShowMainWindow("global-search"),
                "搜索/启动 (Alt+Space)");

            // 原 Ctrl+Space → 控制中心已移除：控制中心在 v3.1 §3.4 不做清单中，
            // 指向已删除页面的热键点了没反应，比没有热键更糟。
            Console.WriteLine("[App] 全局热键已注册：Alt+Space 搜索/启动");
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
                    _appIndex, _fileIndex, _classifier, _automation,
                    _registry, _widgetHost!, id => _registry.Create(id)!);

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

    // 专注模式随控制中心一并移除（设计案 v3.1 §3.4 不做清单）。
    // 「临时隐藏全部组件」的需求由托盘的「隐藏桌面组件」承担，见 OnToggleWidgets。

    // ===== 分区（M3）=====

    /// <summary>切换分区编辑模式 —— 开启后覆盖层整层接管鼠标</summary>
    private void OnToggleFenceEditMode()
    {
        if (_fences == null || _tray == null) return;

        if (!_fences.IsAvailable)
        {
            _tray.ShowBalloon("zDesktop", $"分区功能不可用：{_fences.UnavailableReason}");
            return;
        }

        _fences.EditMode = !_fences.EditMode;
        _tray.UpdateFenceEditCheck(_fences.EditMode);

        if (_fences.EditMode)
            _tray.ShowBalloon("zDesktop", "分区编辑模式已开启：在桌面空白处拖拽即可新建分区。再次点击托盘菜单退出。");
    }

    /// <summary>一键整理 —— 先预览让用户确认，再执行（§二 原则 3）</summary>
    private void OnOrganizeFences()
    {
        if (_fences == null || _tray == null) return;

        if (!_fences.IsAvailable)
        {
            _tray.ShowBalloon("zDesktop", $"分区功能不可用：{_fences.UnavailableReason}");
            return;
        }

        if (_fences.FenceCount == 0)
        {
            _tray.ShowBalloon("zDesktop", "还没有分区。先开启「分区编辑模式」在桌面上拖出一个分区。");
            return;
        }

        var preview = _fences.PreviewOrganize();
        var total = preview.Values.Sum(v => v.Count);

        if (total == 0)
        {
            _tray.ShowBalloon("zDesktop", "没有需要整理的文件（未归属的文件都不匹配现有分区规则）。");
            return;
        }

        // 永不在用户确认前移动文件
        var answer = System.Windows.MessageBox.Show(
            $"将把 {total} 个文件归入 {preview.Count} 个分区。\n\n执行前会自动保存快照，可随时撤销。\n\n继续吗？",
            "一键整理", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);

        if (answer != System.Windows.MessageBoxResult.OK) return;

        var result = _fences.Organize();
        if (result.Succeeded) ShowUndoToast(result.AssignedCount);
        else _tray.ShowBalloon("zDesktop", "整理已中止：快照保存失败，未执行任何修改。");
    }

    /// <summary>撤销最近一次整理</summary>
    private void OnUndoOrganize()
    {
        if (_fences == null || _tray == null) return;

        var restored = _fences.UndoLatest();
        _tray.ShowBalloon("zDesktop", restored >= 0
            ? $"已撤销，还原 {restored} 个图标位置。"
            : "没有可撤销的整理记录。");
    }

    /// <summary>
    /// 卸载资产处置（设计案 v3.1 §6.2）。
    ///
    /// <para>分区一旦消失，桌面上几十个图标会散落一地，用户会觉得「这软件把我桌面搞乱了」
    /// —— 即使技术上我们一个文件都没删。卸载体验决定用户对产品的最后印象。</para>
    /// </summary>
    private void RunUninstallCleanup(string[] args)
    {
        var modeText = args.SkipWhile(a => !a.StartsWith("--uninstall-cleanup", StringComparison.OrdinalIgnoreCase))
                           .Skip(1).FirstOrDefault() ?? "keep";

        var mode = modeText.ToLowerInvariant() switch
        {
            "folders" => DispositionMode.MoveIntoFolders,
            "restore" => DispositionMode.RestoreOriginalLayout,
            _ => DispositionMode.KeepAsIs, // 默认保持现状：伤害最小
        };

        Console.WriteLine($"[Uninstall] 资产处置方式: {mode}");

        try
        {
            using var icons = new NativeIconController();
            var resolver = new DesktopItemResolver();
            var disposition = new UninstallDisposition(
                new FenceStore(), new FenceSnapshotStore(), icons, resolver);

            var result = disposition.Execute(mode);
            Console.WriteLine($"[Uninstall] 完成：创建文件夹 {result.FoldersCreated}，" +
                              $"移动文件 {result.FilesMoved}，还原图标 {result.IconsRestored}");

            foreach (var f in result.Failures) Console.WriteLine($"[Uninstall] {f}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Uninstall] 处置失败: {ex.Message}");
        }

        // 无论处置结果如何，系统状态都必须还原（壁纸、图标层等）
        _restoreJournal.RestoreAll();
        DesktopRestore.ClearRunningFlag();
    }

    // ===== 首次运行引导（M4，设计案 v3.1 §六）=====

    /// <summary>
    /// 首次运行时弹一张非模态引导卡片。
    ///
    /// <para>绝不用模态对话框：那会在用户还没看清桌面之前就挡住屏幕，
    /// 与「装上之后桌面一个像素都不变」的观感直接冲突。</para>
    /// </summary>
    private void ShowOnboardingIfNeeded()
    {
        if (_fences is not { ShouldShowOnboarding: true }) return;

        var fileCount = _fences.DesktopFileCount;
        if (fileCount < 4)
        {
            // 桌面本来就干净，没必要打扰
            _fences.MarkOnboardingShown();
            return;
        }

        var proposal = _fences.BuildProposal();
        if (proposal.Fences.Count == 0)
        {
            // 文件都不属于任何默认类别，建了也是空框
            _fences.MarkOnboardingShown();
            return;
        }

        // 两个数字相同时说「其中 N 个」读着别扭，分开措辞
        var message = proposal.UncategorizedCount == 0
            ? $"你的桌面有 {fileCount} 个项目，可以归入 {proposal.Fences.Count} 个分区。要看看整理后的效果吗？"
            : $"你的桌面有 {fileCount} 个项目，其中 {proposal.TotalFiles} 个可以归入 {proposal.Fences.Count} 个分区。要看看效果吗？";

        ToastWindow.Show(
            "zDesktop 已在后台运行",
            message,
            new[]
            {
                new ToastAction("以后再说", false, () => _fences.MarkOnboardingShown()),
                new ToastAction("预览效果", true, () => ShowProposalPreview(proposal)),
            });
    }

    /// <summary>
    /// 展示方案预览 —— 桌面上画出虚线框，但**不创建任何分区、不移动任何图标**。
    /// </summary>
    private void ShowProposalPreview(zDesktop.Core.Fences.OrganizeProposal proposal)
    {
        if (_fences == null) return;

        _fences.MarkOnboardingShown();
        _fences.ShowProposalPreview(proposal);

        var detail = string.Join("、", proposal.Fences.Select(f => $"{f.Name} {f.Files.Count} 个"));

        ToastWindow.Show(
            "整理预览",
            $"将创建 {proposal.Fences.Count} 个分区：{detail}。\n" +
            $"桌面上的虚线框就是它们的位置。现在还没有任何改动。",
            new[]
            {
                new ToastAction("取消", false, () => _fences.ClearProposalPreview()),
                new ToastAction("应用", true, () => ApplyProposal(proposal)),
            });
    }

    /// <summary>应用整理方案，随后给出可撤销的提示</summary>
    private void ApplyProposal(zDesktop.Core.Fences.OrganizeProposal proposal)
    {
        if (_fences == null) return;

        var result = _fences.ApplyProposal(proposal);

        if (!result.Succeeded)
        {
            ToastWindow.Show("整理已中止",
                "保存快照失败，未执行任何修改。桌面保持原样。",
                new[] { new ToastAction("知道了", true, () => { }) });
            return;
        }

        ShowUndoToast(result.AssignedCount);
    }

    /// <summary>
    /// 整理完成提示，30 秒内可一键撤销（§3.1）。
    /// 倒计时结束只是关掉提示，快照仍然保留，随时可从托盘撤销。
    /// </summary>
    private void ShowUndoToast(int assignedCount)
    {
        ToastWindow.Show(
            "已整理",
            $"{assignedCount} 个文件已归入分区。不满意可以立刻撤销，\n" +
            "之后也能随时从托盘菜单「分区 → 撤销上次整理」还原。",
            new[] { new ToastAction("撤销", false, () => OnUndoOrganize()) },
            autoCloseSeconds: 30);
    }

    /// <summary>分区重命名 —— 弹输入框</summary>
    private void OnFenceRenameRequested(zDesktop.Core.Fences.Fence fence)
    {
        Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            var input = TextInputWindow.Prompt("重命名分区", "分区名称：", fence.Name);
            if (input != null) _fences?.RenameFence(fence, input);
        }));
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

        // 分区的坐标空间依赖显示器配置，必须一并重建
        _fences?.OnDisplayChanged();

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
                Collapsed = entry.Collapsed,
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

        // 分区：落盘并还原壁纸（零破坏契约 —— 退出后桌面须与未安装时一致）
        _fences?.Dispose();

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
