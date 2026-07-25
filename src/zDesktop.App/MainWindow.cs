using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using zDesktop.App.Pages;
using zDesktop.Core.Widgets;
using zDesktop.Shell.Automation;
using zDesktop.Shell.Classifier;
using zDesktop.Shell.Launcher;
using zDesktop.Shell.Search;
using zDesktop.Shell.Styles;
using zDesktop.Shell.Widgets;

// 项目同时启用 WPF + WinForms + System.Drawing，FontFamily 在 System.Drawing 与 System.Windows.Media 间歧义，显式别名优先 WPF
using FontFamily = System.Windows.Media.FontFamily;

namespace zDesktop.App;

/// <summary>
/// 主窗口 — 统一产品外壳：左侧导航栏 + 右侧内容页切换
///
/// 取代旧 7 个独立弹窗（PanelWindowBase），将所有功能聚合到单一窗口中：
/// - 左侧导航栏（宽 200px）：Logo + 分组导航（美化/管理/效率/系统/预览）+ 底部壁纸缩略图
/// - 右侧内容区：顶部标题栏（56px，显示当前页标题 + 关闭/最小化按钮）+ 滚动内容区
///
/// 交互：
/// - 点击导航项 → 懒加载对应 ContentPage 并缓存复用
/// - 顶部标题栏支持拖拽移动窗口
/// - 右上角关闭按钮隐藏窗口（不退出程序，托盘仍驻留）
/// - Navigate(navId) 供托盘菜单和热键编程式跳转
///
/// 所有颜色 / 字体 / 圆角一律引用 <see cref="Theme"/> 常量。
/// </summary>
public sealed class MainWindow : Window
{
    // ===== 服务依赖（由 App 注入）=====
    private readonly AppIndex _appIndex;
    private readonly FileIndexService _fileIndex;
    private readonly FileClassifierService _classifier;
    private readonly AutomationService _automation;
    private readonly WidgetRegistry _registry;
    private readonly WidgetHost _host;
    private readonly Func<string, WidgetBase> _createWidget;

    // ===== UI 元素 =====
    /// <summary>标题栏文本（显示当前页标题）</summary>
    private readonly TextBlock _titleText;

    /// <summary>内容区宿主（承载当前 ContentPage）</summary>
    private readonly Grid _contentHost;

    /// <summary>导航项视图列表（用于切换高亮）</summary>
    private readonly List<NavItemView> _navViews = new();

    /// <summary>当前选中的导航标识</summary>
    private string _currentNavId = string.Empty;

    // ===== 页面缓存（懒加载）=====
    private readonly Dictionary<string, ContentPage> _pageCache = new();

    // ===== 导航图标字体（兼容 emoji 渲染）=====
    private static readonly FontFamily IconFont =
        new("Segoe UI Emoji, Segoe UI Symbol, Microsoft YaHei UI, Microsoft YaHei");

    /// <summary>
    /// 组件布局变更事件 — 从 WidgetPanelPage 透传。
    /// 由 App 订阅，用于保存桌面组件布局。
    /// </summary>
    public event Action? WidgetLayoutChanged;

    /// <summary>
    /// 构造主窗口
    /// </summary>
    /// <param name="appIndex">应用索引服务</param>
    /// <param name="fileIndex">文件索引服务</param>
    /// <param name="classifier">文件分类服务</param>
    /// <param name="automation">自动化服务</param>
    /// <param name="registry">组件注册表</param>
    /// <param name="host">组件宿主</param>
    /// <param name="createWidget">组件工厂（按 Id 创建组件实例）</param>
    public MainWindow(
        AppIndex appIndex,
        FileIndexService fileIndex,
        FileClassifierService classifier,
        AutomationService automation,
        WidgetRegistry registry,
        WidgetHost host,
        Func<string, WidgetBase> createWidget)
    {
        _appIndex = appIndex;
        _fileIndex = fileIndex;
        _classifier = classifier;
        _automation = automation;
        _registry = registry;
        _host = host;
        _createWidget = createWidget;

        // ===== 窗口基础设置 =====
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 1020;
        Height = 680;
        MinWidth = 860;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ===== 外层玻璃拟态容器 =====
        var outerBorder = new Border
        {
            Background = Theme.ContainerBackground,
            BorderBrush = Theme.ContainerBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ContainerRadius,
            Effect = new DropShadowEffect
            {
                Color = Theme.ShadowColor,
                BlurRadius = 40,
                ShadowDepth = 8,
                Opacity = 0.5,
            },
        };

        // ===== 主布局：左侧导航 | 右侧内容 =====
        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // --- 左侧导航栏 ---
        var sidebar = BuildSidebar();
        Grid.SetColumn(sidebar, 0);
        mainGrid.Children.Add(sidebar);

        // --- 右侧内容区 ---
        var contentArea = BuildContentArea(out _titleText, out _contentHost);
        Grid.SetColumn(contentArea, 1);
        mainGrid.Children.Add(contentArea);

        outerBorder.Child = mainGrid;
        Content = outerBorder;

        // 默认导航到首页
        Navigate("home");
    }

    // ============================================================
    //  公开方法
    // ============================================================

    /// <summary>
    /// 编程式切换页面 — 供托盘菜单和热键调用
    /// </summary>
    /// <param name="navId">目标导航标识（如 "global-search" / "automation-rules"）</param>
    public void Navigate(string navId)
    {
        if (string.IsNullOrEmpty(navId)) return;

        try
        {
            var page = GetOrCreatePage(navId);
            if (page == null) return;

            // 切换内容区
            _contentHost.Children.Clear();
            _contentHost.Children.Add(page);

            // 更新标题栏
            _titleText.Text = page.Title;

            // 更新导航高亮
            _currentNavId = navId;
            UpdateNavSelection();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainWindow] 导航到 {navId} 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 导航到指定页面并执行搜索 — 供桌面搜索框等外部入口调用
    /// </summary>
    /// <param name="navId">目标导航标识（如 "global-search"）</param>
    /// <param name="searchQuery">搜索关键词（仅对全局搜索页有效）</param>
    public void NavigateWithSearch(string navId, string searchQuery)
    {
        Navigate(navId);

        // 如果是全局搜索页，设置搜索词
        if (navId == "global-search" && !string.IsNullOrEmpty(searchQuery))
        {
            if (_pageCache.TryGetValue(navId, out var page) && page is GlobalSearchPage searchPage)
            {
                searchPage.SetSearchQuery(searchQuery);
            }
        }
    }

    // ============================================================
    //  侧边栏构建
    // ============================================================

    /// <summary>构建左侧导航栏（Logo + 分组导航 + 壁纸缩略图）</summary>
    private Border BuildSidebar()
    {
        var dock = new DockPanel { LastChildFill = true };

        // --- Logo 区（顶部）---
        var logo = BuildLogo();
        DockPanel.SetDock(logo, Dock.Top);
        dock.Children.Add(logo);

        // --- 壁纸缩略图（底部）---
        var wallpaper = BuildWallpaperThumbnail();
        DockPanel.SetDock(wallpaper, Dock.Bottom);
        dock.Children.Add(wallpaper);

        // --- 导航项（中间，可滚动）---
        var navScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 4, 0, 4),
        };
        var navPanel = BuildNavPanel();
        navScroll.Content = navPanel;
        dock.Children.Add(navScroll);

        return new Border
        {
            Child = dock,
            Background = Theme.HeaderBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
    }

    /// <summary>构建 Logo 区（监视器图标 + zDesktop 文字，点击回到首页）</summary>
    private UIElement BuildLogo()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(16, 18, 16, 14),
            Cursor = Cursors.Hand,
        };

        var icon = new TextBlock
        {
            Text = "🖥",
            FontFamily = IconFont,
            FontSize = 18,
            Foreground = Theme.PrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(icon);

        var title = new TextBlock
        {
            Text = "zDesktop",
            FontFamily = Theme.TitleFont,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        panel.Children.Add(title);

        panel.MouseLeftButtonUp += (_, _) => Navigate("home");

        return panel;
    }

    /// <summary>构建导航项面板（含分组标题）</summary>
    private StackPanel BuildNavPanel()
    {
        var panel = new StackPanel();

        foreach (var item in NavItems)
        {
            // 分组标题
            if (item.Group != null)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = item.Group,
                    FontFamily = Theme.UiFont,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Theme.TextFaint,
                    Margin = new Thickness(16, 12, 0, 4),
                });
            }

            // 导航项
            panel.Children.Add(CreateNavItem(item));
        }

        return panel;
    }

    /// <summary>创建单个导航项</summary>
    private Border CreateNavItem(NavItem item)
    {
        var icon = new TextBlock
        {
            Text = item.Icon,
            FontFamily = IconFont,
            FontSize = 14,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 20,
        };

        var label = new TextBlock
        {
            Text = item.Label,
            FontFamily = Theme.UiFont,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(icon);
        stack.Children.Add(label);

        var border = new Border
        {
            Child = stack,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(8, 1, 8, 1),
            CornerRadius = Theme.ControlRadius,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };

        var view = new NavItemView
        {
            NavId = item.Id,
            Container = border,
            Icon = icon,
            Label = label,
        };
        _navViews.Add(view);

        border.MouseLeftButtonUp += (_, _) => Navigate(item.Id);
        border.MouseEnter += (_, _) =>
        {
            if (_currentNavId != item.Id)
            {
                border.Background = Theme.InputBackground;
                label.Foreground = Theme.TextRegular;
                icon.Foreground = Theme.TextRegular;
            }
        };
        border.MouseLeave += (_, _) =>
        {
            if (_currentNavId != item.Id)
            {
                border.Background = Brushes.Transparent;
                label.Foreground = Theme.TextSecondary;
                icon.Foreground = Theme.TextSecondary;
            }
        };

        return border;
    }

    /// <summary>更新所有导航项的选中态样式</summary>
    private void UpdateNavSelection()
    {
        foreach (var view in _navViews)
        {
            var selected = view.NavId == _currentNavId;
            view.Container.Background = selected ? Theme.PrimarySubtle : Brushes.Transparent;
            view.Label.Foreground = selected ? Theme.PrimaryBrush : Theme.TextSecondary;
            view.Icon.Foreground = selected ? Theme.PrimaryBrush : Theme.TextSecondary;
            view.Label.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Medium;
        }
    }

    /// <summary>构建底部壁纸缩略图占位</summary>
    private UIElement BuildWallpaperThumbnail()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(16, 12, 16, 16),
        };

        // 缩略图占位（品牌色底 + Z 字）
        var thumb = new Border
        {
            Width = 48,
            Height = 32,
            CornerRadius = Theme.SmallRadius,
            Background = Theme.PrimarySubtle,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "Z",
                FontFamily = Theme.UiFont,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.PrimaryBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        panel.Children.Add(thumb);

        var info = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        info.Children.Add(new TextBlock
        {
            Text = "zDesktop",
            FontFamily = Theme.UiFont,
            FontSize = 12,
            Foreground = Theme.TextPrimary,
        });
        info.Children.Add(new TextBlock
        {
            Text = "桌面已接管",
            FontFamily = Theme.UiFont,
            FontSize = 10,
            Foreground = Theme.TextSecondary,
        });
        panel.Children.Add(info);

        return new Border
        {
            Child = panel,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = Theme.HeaderBackground,
        };
    }

    // ============================================================
    //  内容区构建
    // ============================================================

    /// <summary>构建右侧内容区（标题栏 + 内容宿主）</summary>
    private Grid BuildContentArea(out TextBlock titleText, out Grid contentHost)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // --- 标题栏 ---
        var headerBorder = BuildTitleBar(out titleText);
        Grid.SetRow(headerBorder, 0);
        grid.Children.Add(headerBorder);

        // --- 内容宿主 ---
        contentHost = new Grid();
        Grid.SetRow(contentHost, 1);
        grid.Children.Add(contentHost);

        return grid;
    }

    /// <summary>构建标题栏（标题文本 + 最小化/关闭按钮 + 拖拽移动）</summary>
    private Border BuildTitleBar(out TextBlock titleText)
    {
        titleText = new TextBlock
        {
            Text = "",
            FontFamily = Theme.TitleFont,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24, 0, 0, 0),
        };

        var dock = new DockPanel
        {
            LastChildFill = true,
        };

        // 按钮区（右侧）
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(btnPanel, Dock.Right);

        var minimizeBtn = CreateCaptionButton("—");
        minimizeBtn.Click += (_, _) => WindowState = WindowState.Minimized;
        minimizeBtn.Margin = new Thickness(0, 0, 4, 0);
        btnPanel.Children.Add(minimizeBtn);

        var closeBtn = CreateCaptionButton("✕");
        closeBtn.Click += (_, _) => Hide();
        btnPanel.Children.Add(closeBtn);

        dock.Children.Add(btnPanel);
        dock.Children.Add(titleText);

        var border = new Border
        {
            Child = dock,
            Background = Theme.HeaderBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        // 拖拽移动窗口
        border.MouseLeftButtonDown += (_, _) =>
        {
            try { DragMove(); }
            catch { /* 窗口无法拖拽时忽略 */ }
        };

        return border;
    }

    /// <summary>创建标题栏按钮（最小化/关闭）</summary>
    private Button CreateCaptionButton(string glyph)
    {
        return new Button
        {
            Content = glyph,
            Width = 32,
            Height = 32,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.TextSecondary,
            Cursor = Cursors.Hand,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0),
        };
    }

    // ============================================================
    //  页面懒加载
    // ============================================================

    /// <summary>获取或创建指定导航页（首次创建时缓存，后续复用）</summary>
    private ContentPage? GetOrCreatePage(string navId)
    {
        if (_pageCache.TryGetValue(navId, out var cached))
            return cached;

        ContentPage? page = null;
        try
        {
            page = CreatePage(navId);
            if (page != null)
            {
                _pageCache[navId] = page;

                // 订阅页面事件
                if (page is WidgetPanelPage wp)
                {
                    wp.WidgetAdded += () => WidgetLayoutChanged?.Invoke();
                    wp.WidgetRemoved += () => WidgetLayoutChanged?.Invoke();
                    wp.WidgetConfigured += () => WidgetLayoutChanged?.Invoke();
                }
                else if (page is HomePage hp)
                {
                    hp.NavigateRequested += navId => Navigate(navId);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainWindow] 创建页面 {navId} 失败: {ex.Message}");
        }

        return page;
    }

    /// <summary>按导航标识创建对应内容页</summary>
    private ContentPage? CreatePage(string navId)
    {
        return navId switch
        {
            "home" => new HomePage(),
            "desktop-widgets" => new WidgetPanelPage(_registry, _host, _createWidget),
            "file-classify" => new FileClassifyPage(_classifier),
            "automation-rules" => new AutomationPage(_automation),
            "global-search" => new GlobalSearchPage(_appIndex, _fileIndex),
            "settings" => new SettingsPage(),
            _ => null,
        };
    }

    // ============================================================
    //  导航项数据
    // ============================================================

    /// <summary>导航项数据结构</summary>
    private sealed class NavItem
    {
        /// <summary>导航标识</summary>
        public string Id { get; init; }

        /// <summary>显示标签</summary>
        public string Label { get; init; }

        /// <summary>图标字符</summary>
        public string Icon { get; init; }

        /// <summary>所属分组（null 表示顶级）</summary>
        public string? Group { get; init; }

        public NavItem(string id, string label, string icon, string? group)
        {
            Id = id;
            Label = label;
            Icon = icon;
            Group = group;
        }
    }

    /// <summary>导航项视图引用（用于切换高亮样式）</summary>
    private sealed class NavItemView
    {
        /// <summary>导航标识</summary>
        public string NavId { get; init; } = string.Empty;

        /// <summary>外层 Border</summary>
        public Border Container { get; init; } = null!;

        /// <summary>图标文本</summary>
        public TextBlock Icon { get; init; } = null!;

        /// <summary>标签文本</summary>
        public TextBlock Label { get; init; } = null!;
    }

    /// <summary>
    /// 导航项列表（设计案 v3.1 §3.2：设置窗口从产品主体降为配置面板，16 页砍到 6 页）
    ///
    /// 「分区」页随 M3 分区功能落地补入。
    /// </summary>
    private static readonly NavItem[] NavItems =
    {
        new("home", "概览", "🏠", null),
        new("desktop-widgets", "组件", "🧩", null),
        new("file-classify", "分类", "🗂", null),
        new("automation-rules", "规则", "⚡", null),
        new("global-search", "搜索", "🔍", null),
        new("settings", "设置", "🔧", null),
    };
}
