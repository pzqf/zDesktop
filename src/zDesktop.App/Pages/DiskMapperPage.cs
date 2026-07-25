using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading;
using zDesktop.Shell.DiskMapper;
using zDesktop.Shell.Search;
using zDesktop.Shell.Styles;

// App 项目全局引用了 System.Drawing / System.Windows.Forms，以下类型与 WinForms 同名存在歧义，显式别名优先 WPF
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using ListView = System.Windows.Controls.ListView;
using ListViewItem = System.Windows.Controls.ListViewItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragDropKeyStates = System.Windows.DragDropKeyStates;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using Binding = System.Windows.Data.Binding;

namespace zDesktop.App.Pages;

/// <summary>
/// 磁盘映射内容页 — Q-Dir 风格多窗格文件管理器
///
/// 视觉与交互（参考 pages/disk-mapper.html，WPF 实现）：
/// - 顶部工具栏：布局切换（1×1 / 1×2 / 2×2 / 1×3）+ 大文件扫描按钮 + 书签按钮
/// - 主区域：Grid 动态分列分行承载多个 <see cref="FilePane"/>
/// - 每个窗格：面包屑路径栏（可点击）+ 后退/前进 + 文件列表（名称/大小/修改时间）
///   + 底部磁盘用量进度条（>80% 警告色，>95% 错误色）+ 文件类型分布迷你堆叠条
/// - 双击打开文件夹 / 右键菜单（打开/复制路径/删除）/ 跨窗格拖拽复制移动
/// - 默认 2×2 布局，4 个窗格分别指向 C:\ / D:\ / 用户文档 / 用户桌面
///
/// 嵌入主窗口右侧内容区，不再独立弹窗。大文件扫描仍以独立子窗口弹出。
/// 所有颜色 / 字体 / 圆角一律引用 <see cref="Theme"/> 常量。
/// </summary>
public sealed class DiskMapperPage : ContentPage
{
    /// <summary>磁盘映射服务（由 App 注入）</summary>
    private readonly DiskMapperService _service;

    /// <summary>磁盘列表缓存（构造时采样一次，用于默认路径）</summary>
    private readonly List<DriveInfoLite> _drives;

    /// <summary>窗格容器网格</summary>
    private Grid _paneGrid = null!;

    /// <summary>当前布局</summary>
    private PaneLayout _layout = PaneLayout.Grid2x2;

    /// <summary>所有窗格</summary>
    private readonly List<FilePane> _panes = new();

    /// <summary>布局切换按钮（用于高亮当前激活布局）</summary>
    private readonly Dictionary<PaneLayout, Button> _layoutButtons = new();

    /// <summary>最近激活的窗格（书签导航 / 拖放目标推断使用）</summary>
    private FilePane? _activePane;

    /// <summary>
    /// 构造磁盘映射内容页
    /// </summary>
    /// <param name="service">磁盘映射服务（由 App 注入）</param>
    public DiskMapperPage(DiskMapperService service)
    {
        _service = service;
        Title = "磁盘映射";
        NavId = "disk-mapper";
        _drives = _service.GetDrives();

        var root = new DockPanel();
        root.LastChildFill = true;

        // 顶部工具栏
        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        // 主区域：窗格网格
        _paneGrid = new Grid
        {
            Margin = new Thickness(6),
        };
        root.Children.Add(_paneGrid);

        Content = root;

        // 默认 2×2 布局
        ApplyLayout(PaneLayout.Grid2x2);
    }

    // ===== 工具栏 =====

    /// <summary>构建顶部工具栏（布局切换 + 大文件扫描 + 书签）</summary>
    private Border BuildToolbar()
    {
        var bar = new DockPanel
        {
            Margin = new Thickness(12, 10, 12, 8),
            LastChildFill = true,
        };

        // 右侧操作区
        var actions = new StackPanel { Orientation = Orientation.Horizontal };

        var scanBtn = CreatePrimaryButton("大文件扫描");
        scanBtn.Click += (_, _) => OnScanLargeFiles();
        actions.Children.Add(scanBtn);

        var bookmarkBtn = CreateSecondaryButton("书签");
        bookmarkBtn.Margin = new Thickness(8, 0, 0, 0);
        bookmarkBtn.Click += (_, _) => ShowBookmarksMenu();
        actions.Children.Add(bookmarkBtn);

        DockPanel.SetDock(actions, Dock.Right);
        bar.Children.Add(actions);

        // 左侧布局切换
        var layouts = new StackPanel { Orientation = Orientation.Horizontal };
        var defs = new[]
        {
            (PaneLayout.Single, "1×1"),
            (PaneLayout.Horizontal2, "1×2"),
            (PaneLayout.Grid2x2, "2×2"),
            (PaneLayout.Horizontal3, "1×3"),
        };
        foreach (var (mode, label) in defs)
        {
            var btn = CreateSecondaryButton(label);
            btn.Margin = new Thickness(0, 0, 8, 0);
            btn.Tag = mode;
            btn.Click += (_, _) => ApplyLayout(mode);
            _layoutButtons[mode] = btn;
            layouts.Children.Add(btn);
        }
        bar.Children.Add(layouts);

        return new Border
        {
            Child = bar,
            Background = Theme.HeaderBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    /// <summary>更新布局切换按钮的激活高亮</summary>
    private void UpdateLayoutButtonStyles()
    {
        foreach (var (mode, btn) in _layoutButtons)
        {
            if (mode == _layout)
            {
                btn.Background = Theme.PrimarySubtle;
                btn.Foreground = Theme.PrimaryBrush;
                btn.BorderBrush = Theme.PrimaryAccent;
                btn.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                btn.Background = Theme.InputBackground;
                btn.Foreground = Theme.TextRegular;
                btn.BorderBrush = Theme.InputBorder;
                btn.FontWeight = FontWeights.Normal;
            }
        }
    }

    // ===== 布局管理 =====

    /// <summary>应用窗格布局 — 重建网格行列定义与窗格实例</summary>
    private void ApplyLayout(PaneLayout mode)
    {
        _layout = mode;
        _paneGrid.Children.Clear();
        _paneGrid.RowDefinitions.Clear();
        _paneGrid.ColumnDefinitions.Clear();
        _panes.Clear();
        _activePane = null;

        var (rows, cols) = LayoutDims(mode);
        for (var r = 0; r < rows; r++)
            _paneGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (var c = 0; c < cols; c++)
            _paneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var count = rows * cols;
        for (var i = 0; i < count; i++)
        {
            var pane = new FilePane(_service, i, GetDefaultPath(i));
            var r = i / cols;
            var c = i % cols;
            Grid.SetRow(pane, r);
            Grid.SetColumn(pane, c);
            pane.Margin = new Thickness(4);
            pane.MouseEnter += (_, _) => _activePane = pane;
            _paneGrid.Children.Add(pane);
            _panes.Add(pane);
        }

        if (_panes.Count > 0) _activePane = _panes[0];
        UpdateLayoutButtonStyles();
    }

    /// <summary>布局维度（行, 列）</summary>
    private static (int Rows, int Cols) LayoutDims(PaneLayout mode) => mode switch
    {
        PaneLayout.Single => (1, 1),
        PaneLayout.Horizontal2 => (1, 2),
        PaneLayout.Grid2x2 => (2, 2),
        PaneLayout.Horizontal3 => (1, 3),
        _ => (2, 2),
    };

    /// <summary>按窗格索引返回默认路径（C:\ / D:\ / 用户文档 / 用户桌面，缺失时回退）</summary>
    private string GetDefaultPath(int index)
    {
        var cDrive = _drives.Count > 0 ? _drives[0].Name : @"C:\";
        var dDrive = _drives.Count > 1 ? _drives[1].Name : cDrive;

        var path = index switch
        {
            0 => cDrive,
            1 => dDrive,
            2 => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            3 => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            _ => cDrive,
        };

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            path = cDrive;
        return path;
    }

    // ===== 大文件扫描 =====

    /// <summary>对当前激活窗格所在盘根执行大文件扫描，弹出结果窗口</summary>
    private void OnScanLargeFiles()
    {
        var pane = _activePane ?? (_panes.Count > 0 ? _panes[0] : null);
        if (pane == null) return;
        var root = pane.CurrentPath;
        try { root = Path.GetPathRoot(root) ?? pane.CurrentPath; }
        catch { /* 保留原路径 */ }

        try
        {
            var win = new LargeFileScanWindow(_service, root);
            win.Show();
        }
        catch
        {
            // 弹出失败忽略
        }
    }

    // ===== 书签 =====

    /// <summary>弹出书签菜单 — 点击项导航激活窗格，末项收藏当前窗格路径</summary>
    private void ShowBookmarksMenu()
    {
        var menu = new ContextMenu();
        if (_service.Bookmarks.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "（暂无书签）", IsEnabled = false });
        }
        else
        {
            foreach (var b in _service.Bookmarks)
            {
                var path = b;
                var mi = new MenuItem { Header = path };
                mi.Click += (_, _) => NavigateActivePane(path);
                menu.Items.Add(mi);
            }
        }

        var addMi = new MenuItem { Header = "收藏当前窗格路径" };
        addMi.Click += (_, _) =>
        {
            var p = _activePane ?? (_panes.Count > 0 ? _panes[0] : null);
            if (p != null && !string.IsNullOrEmpty(p.CurrentPath))
            {
                _service.AddBookmark(p.CurrentPath);
                ShowBookmarksMenu();
            }
        };
        menu.Items.Add(addMi);

        menu.IsOpen = true;
    }

    /// <summary>导航激活窗格到指定路径</summary>
    private void NavigateActivePane(string path)
    {
        var p = _activePane ?? (_panes.Count > 0 ? _panes[0] : null);
        p?.Navigate(path);
    }
}

/// <summary>窗格布局模式</summary>
internal enum PaneLayout
{
    /// <summary>单窗格</summary>
    Single,
    /// <summary>横向 2 窗格</summary>
    Horizontal2,
    /// <summary>2×2 四窗格</summary>
    Grid2x2,
    /// <summary>横向 3 窗格</summary>
    Horizontal3,
}

/// <summary>
/// 单个文件浏览窗格 — 面包屑 + 文件列表 + 磁盘用量条 + 类型分布堆叠条
///
/// 每个 FilePane 持有独立的 paneId，通过 <see cref="DiskMapperService"/> 维护后退/前进历史。
/// </summary>
internal sealed class FilePane : Border
{
    /// <summary>跨窗格拖放数据格式名</summary>
    private const string DragDataFormat = "zDesktopFileDrop";

    private readonly DiskMapperService _service;
    private readonly int _paneId;
    private readonly string _defaultPath;

    private ListView _listView = null!;
    private StackPanel _breadcrumb = null!;
    private Button _backBtn = null!;
    private Button _fwdBtn = null!;
    private Grid _usageBarContainer = null!;
    private TextBlock _usageLabel = null!;
    private Grid _stackBarContainer = null!;

    /// <summary>当前路径</summary>
    private string _currentPath = string.Empty;

    /// <summary>拖放起始点（用于判断是否进入拖拽）</summary>
    private Point? _dragStart;

    /// <summary>当前路径（只读）</summary>
    public string CurrentPath => _currentPath;

    internal FilePane(DiskMapperService service, int paneId, string defaultPath)
    {
        _service = service;
        _paneId = paneId;
        _defaultPath = defaultPath;

        Background = Theme.ListItemBackground;
        BorderBrush = Theme.ContainerBorder;
        BorderThickness = new Thickness(1);
        CornerRadius = Theme.ControlRadius;
        Padding = new Thickness(0);

        var dock = new DockPanel { LastChildFill = true };

        // ===== 顶部地址栏 =====
        var addressBar = BuildAddressBar();
        DockPanel.SetDock(addressBar, Dock.Top);
        dock.Children.Add(addressBar);

        // ===== 底部状态区（用量条 + 堆叠条）=====
        var statusBar = BuildStatusBar();
        DockPanel.SetDock(statusBar, Dock.Bottom);
        dock.Children.Add(statusBar);

        // ===== 中部文件列表 =====
        _listView = BuildListView();
        dock.Children.Add(_listView);

        Child = dock;

        // 初始导航：有历史则恢复，否则进入默认路径
        var hist = _service.GetHistory(_paneId);
        if (!string.IsNullOrEmpty(hist.Current))
        {
            _currentPath = hist.Current!;
            Refresh();
        }
        else
        {
            Navigate(_defaultPath);
        }
    }

    // ===== UI 构建 =====

    /// <summary>构建地址栏：后退/前进 + 面包屑</summary>
    private Border BuildAddressBar()
    {
        var row = new DockPanel
        {
            Margin = new Thickness(8, 6, 8, 6),
            LastChildFill = true,
        };

        _backBtn = new Button
        {
            Content = "‹",
            Width = 26,
            Height = 26,
            FontSize = 14,
            FontFamily = Theme.UiFont,
            Background = Theme.InputBackground,
            Foreground = Theme.TextRegular,
            BorderBrush = Theme.InputBorder,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Padding = new Thickness(0),
            IsEnabled = false,
        };
        _backBtn.Click += (_, _) => GoBack();
        DockPanel.SetDock(_backBtn, Dock.Left);
        row.Children.Add(_backBtn);

        _fwdBtn = new Button
        {
            Content = "›",
            Width = 26,
            Height = 26,
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 14,
            FontFamily = Theme.UiFont,
            Background = Theme.InputBackground,
            Foreground = Theme.TextRegular,
            BorderBrush = Theme.InputBorder,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Padding = new Thickness(0),
            IsEnabled = false,
        };
        _fwdBtn.Click += (_, _) => GoForward();
        DockPanel.SetDock(_fwdBtn, Dock.Left);
        row.Children.Add(_fwdBtn);

        // 面包屑容器（横向滚动）
        _breadcrumb = new StackPanel { Orientation = Orientation.Horizontal };
        var crumbScroll = new ScrollViewer
        {
            Content = _breadcrumb,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(6, 0, 0, 0),
        };
        row.Children.Add(crumbScroll);

        return new Border
        {
            Child = row,
            Background = Theme.HeaderBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    /// <summary>构建底部状态区：磁盘用量条 + 文件类型分布堆叠条</summary>
    private StackPanel BuildStatusBar()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(8, 6, 8, 6),
        };

        _usageLabel = new TextBlock
        {
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 4),
        };
        panel.Children.Add(_usageLabel);

        _usageBarContainer = new Grid { Height = 8 };
        panel.Children.Add(_usageBarContainer);

        _stackBarContainer = new Grid
        {
            Height = 6,
            Margin = new Thickness(0, 4, 0, 0),
        };
        panel.Children.Add(_stackBarContainer);

        return panel;
    }

    /// <summary>构建文件列表（GridView：名称/大小/修改时间）+ 上下文菜单 + 拖放</summary>
    private ListView BuildListView()
    {
        var lv = new ListView
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.TextRegular,
            FontFamily = Theme.UiFont,
            FontSize = 13,
            Padding = new Thickness(0),
            Margin = new Thickness(2),
            AllowDrop = true,
        };

        // 行容器样式（选中高亮 + 分隔线）
        var itemStyle = new Style(typeof(ListViewItem));
        itemStyle.Setters.Add(new Setter(ListViewItem.BackgroundProperty, Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(ListViewItem.ForegroundProperty, Theme.TextRegular));
        itemStyle.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(8, 5, 8, 5)));
        itemStyle.Setters.Add(new Setter(ListViewItem.BorderBrushProperty, Theme.Divider));
        itemStyle.Setters.Add(new Setter(ListViewItem.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        itemStyle.Triggers.Add(new Trigger
        {
            Property = ListViewItem.IsSelectedProperty,
            Value = true,
            Setters =
            {
                new Setter(ListViewItem.BackgroundProperty, Theme.PrimarySubtle),
                new Setter(ListViewItem.ForegroundProperty, Theme.TextPrimary),
            },
        });
        lv.Resources[typeof(ListViewItem)] = itemStyle;

        // 表头样式
        var headerStyle = new Style(typeof(GridViewColumnHeader));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.BackgroundProperty, Theme.HeaderBackground));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.ForegroundProperty, Theme.TextSecondary));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.BorderBrushProperty, Theme.Divider));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.FontFamilyProperty, Theme.UiFont));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.FontSizeProperty, 11.0));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.PaddingProperty, new Thickness(8, 4, 8, 4)));
        lv.Resources[typeof(GridViewColumnHeader)] = headerStyle;

        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "名称", Width = 240, DisplayMemberBinding = new Binding(nameof(FileRow.NameDisplay)) });
        gv.Columns.Add(new GridViewColumn { Header = "大小", Width = 90, DisplayMemberBinding = new Binding(nameof(FileRow.SizeDisplay)) });
        gv.Columns.Add(new GridViewColumn { Header = "修改时间", Width = 140, DisplayMemberBinding = new Binding(nameof(FileRow.DateDisplay)) });
        lv.View = gv;

        // 双击：目录则进入，文件则 shell 打开
        lv.MouseDoubleClick += (_, _) =>
        {
            if (lv.SelectedItem is FileRow row)
            {
                if (row.Entry.IsDirectory) Navigate(row.Entry.FullPath);
                else OpenShell(row.Entry.FullPath);
            }
        };

        // 右键菜单
        lv.ContextMenu = BuildListContextMenu();

        // 拖放：源（MouseMove 启动）+ 目标（DragOver/Drop）
        lv.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(lv);
        lv.MouseMove += OnListViewMouseMove;
        lv.DragOver += OnListViewDragOver;
        lv.Drop += OnListViewDrop;

        return lv;
    }

    /// <summary>构建右键菜单（打开 / 复制路径 / 删除）</summary>
    private ContextMenu BuildListContextMenu()
    {
        var menu = new ContextMenu();

        var mOpen = new MenuItem { Header = "打开" };
        mOpen.Click += (_, _) =>
        {
            if (_listView.SelectedItem is FileRow row)
            {
                if (row.Entry.IsDirectory) Navigate(row.Entry.FullPath);
                else OpenShell(row.Entry.FullPath);
            }
        };
        menu.Items.Add(mOpen);

        var mCopy = new MenuItem { Header = "复制路径" };
        mCopy.Click += (_, _) =>
        {
            if (_listView.SelectedItem is FileRow row)
            {
                try { Clipboard.SetText(row.Entry.FullPath); } catch { /* 剪贴板失败忽略 */ }
            }
        };
        menu.Items.Add(mCopy);

        var mDel = new MenuItem { Header = "删除" };
        mDel.Click += (_, _) =>
        {
            if (_listView.SelectedItem is FileRow row) DeleteEntry(row.Entry);
        };
        menu.Items.Add(mDel);

        return menu;
    }

    // ===== 导航 =====

    /// <summary>导航到指定路径（记入历史并刷新）</summary>
    internal void Navigate(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (!Directory.Exists(path)) return;
        }
        catch { return; }

        _service.Navigate(_paneId, path);
        _currentPath = path;
        Refresh();
    }

    /// <summary>后退一步</summary>
    private void GoBack()
    {
        var path = _service.Back(_paneId);
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
        {
            _currentPath = path!;
            Refresh();
        }
    }

    /// <summary>前进一步</summary>
    private void GoForward()
    {
        var path = _service.Forward(_paneId);
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
        {
            _currentPath = path!;
            Refresh();
        }
    }

    /// <summary>刷新窗格内容（列表 + 面包屑 + 用量条 + 堆叠条 + 导航按钮）</summary>
    private void Refresh()
    {
        var entries = _service.GetDirectoryContents(_currentPath);
        _listView.ItemsSource = entries.Select(e => new FileRow(e)).ToList();
        UpdateBreadcrumb(_currentPath);
        UpdateUsageBar(_currentPath);
        UpdateStackedBar(_currentPath);
        UpdateNavButtons();
    }

    /// <summary>更新后退/前进按钮可用态</summary>
    private void UpdateNavButtons()
    {
        var hist = _service.GetHistory(_paneId);
        _backBtn.IsEnabled = hist.CanGoBack;
        _fwdBtn.IsEnabled = hist.CanGoForward;
    }

    /// <summary>重建面包屑（可点击的路径分段）</summary>
    private void UpdateBreadcrumb(string path)
    {
        _breadcrumb.Children.Clear();
        foreach (var (name, full) in SplitPath(path))
        {
            if (_breadcrumb.Children.Count > 0)
            {
                _breadcrumb.Children.Add(new TextBlock
                {
                    Text = "›",
                    FontSize = 12,
                    FontFamily = Theme.UiFont,
                    Foreground = Theme.TextFaint,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0),
                });
            }

            var seg = new Button
            {
                Content = name,
                FontSize = 12,
                FontFamily = Theme.UiFont,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Theme.TextRegular,
                Cursor = Cursors.Hand,
                Padding = new Thickness(2, 0, 2, 0),
                Height = 24,
                Tag = full,
            };
            seg.Click += (_, _) => Navigate(full);
            _breadcrumb.Children.Add(seg);
        }
    }

    /// <summary>更新磁盘用量条与标签（>80% 警告，>95% 错误）</summary>
    private void UpdateUsageBar(string path)
    {
        _usageBarContainer.Children.Clear();
        _usageBarContainer.ColumnDefinitions.Clear();

        double pct = 0;
        string label;
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) root = path;
            var di = new DriveInfo(root);
            if (!di.IsReady)
            {
                _usageLabel.Text = $"{root} 未就绪";
                return;
            }
            var total = di.TotalSize;
            var free = di.TotalFreeSpace;
            var used = total - free;
            pct = total > 0 ? (double)used / total * 100 : 0;
            label = $"{root.TrimEnd('\\')}  {pct:F0}% 已用 · {FormatSize(free)} 可用 / {FormatSize(total)}";
        }
        catch
        {
            label = string.IsNullOrEmpty(path) ? "未知路径" : path;
        }
        _usageLabel.Text = label;

        Brush fillBrush;
        if (pct > 95) fillBrush = new SolidColorBrush(Theme.Error);
        else if (pct > 80) fillBrush = new SolidColorBrush(Theme.Warning);
        else fillBrush = new SolidColorBrush(Theme.Success);

        var filled = Math.Max(0, Math.Min(100, pct));
        _usageBarContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(filled, GridUnitType.Star) });
        _usageBarContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 100 - filled), GridUnitType.Star) });

        var fill = new Border { Background = fillBrush };
        Grid.SetColumn(fill, 0);
        _usageBarContainer.Children.Add(fill);

        var empty = new Border { Background = Theme.ListItemMuted };
        Grid.SetColumn(empty, 1);
        _usageBarContainer.Children.Add(empty);
    }

    /// <summary>异步更新文件类型分布堆叠条（文档/图片/视频/其他）</summary>
    private void UpdateStackedBar(string path)
    {
        string root;
        try { root = Path.GetPathRoot(path) ?? path; }
        catch { root = path; }

        System.Threading.Tasks.Task.Run(() => _service.GetTypeDistribution(root))
            .ContinueWith(t =>
            {
                var data = t.IsCompletedSuccessfully ? t.Result : new List<TypeDistributionStat>();
                Dispatcher.BeginInvoke(new Action(() => RenderStackedBar(data)));
            });
    }

    /// <summary>渲染堆叠条（按文件数比例分配 4 段宽度）</summary>
    private void RenderStackedBar(List<TypeDistributionStat> stats)
    {
        _stackBarContainer.Children.Clear();
        _stackBarContainer.ColumnDefinitions.Clear();

        int doc = stats.FirstOrDefault(s => s.Category == FileCategory.Document)?.FileCount ?? 0;
        int img = stats.FirstOrDefault(s => s.Category == FileCategory.Image)?.FileCount ?? 0;
        int vid = stats.FirstOrDefault(s => s.Category == FileCategory.Video)?.FileCount ?? 0;
        int other = stats
            .Where(s => s.Category != FileCategory.Document &&
                        s.Category != FileCategory.Image &&
                        s.Category != FileCategory.Video)
            .Sum(s => s.FileCount);
        int total = doc + img + vid + other;

        if (total == 0)
        {
            _stackBarContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var empty = new Border { Background = Theme.ListItemMuted };
            Grid.SetColumn(empty, 0);
            _stackBarContainer.Children.Add(empty);
            return;
        }

        var segs = new (int Count, Brush Fill, string Name)[]
        {
            (doc, new SolidColorBrush(Theme.Info), "文档"),
            (img, new SolidColorBrush(Theme.Success), "图片"),
            (vid, new SolidColorBrush(Theme.Primary), "视频"),
            (other, new SolidColorBrush(Theme.MutedForeground), "其他"),
        };

        foreach (var (count, fill, name) in segs)
        {
            if (count <= 0) continue;
            var idx = _stackBarContainer.ColumnDefinitions.Count;
            _stackBarContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(count, GridUnitType.Star) });
            var seg = new Border { Background = fill, ToolTip = $"{name}: {count}" };
            Grid.SetColumn(seg, idx);
            _stackBarContainer.Children.Add(seg);
        }
    }

    // ===== 拖放 =====

    /// <summary>鼠标移动 — 超过阈值则启动拖放</summary>
    private void OnListViewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _dragStart = null; return; }

        var pos = e.GetPosition(_listView);
        if (Math.Abs(pos.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var row = _listView.SelectedItem as FileRow;
        _dragStart = null;
        if (row == null) return;

        try
        {
            var data = new DataObject();
            data.SetData(DragDataFormat, new List<string> { row.Entry.FullPath });
            DragDrop.DoDragDrop(_listView, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch
        {
            // 拖放启动失败忽略
        }
    }

    /// <summary>拖放悬停 — Ctrl 复制，否则移动</summary>
    private void OnListViewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DragDataFormat))
        {
            e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) != 0
                ? DragDropEffects.Copy
                : DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    /// <summary>接收拖放 — 将文件/目录复制或移动到当前窗格目录</summary>
    private void OnListViewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragDataFormat)) return;
        var paths = e.Data.GetData(DragDataFormat) as List<string>;
        if (paths == null || paths.Count == 0) return;

        var move = (e.KeyStates & DragDropKeyStates.ControlKey) == 0;
        e.Handled = true;

        foreach (var src in paths)
        {
            try
            {
                var name = Path.GetFileName(src);
                var dst = Path.Combine(_currentPath, name);
                if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)) continue;

                if (Directory.Exists(src))
                {
                    if (move) MoveDirectorySafe(src, dst);
                    else CopyDirectoryRecursive(src, dst);
                }
                else if (File.Exists(src))
                {
                    if (move) MoveFileSafe(src, dst);
                    else CopyFileSafe(src, dst);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DiskMapper] 拖放失败 ({src}): {ex.Message}");
            }
        }

        Refresh();
    }

    // ===== 文件操作 =====

    /// <summary>用系统默认程序打开文件</summary>
    private static void OpenShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DiskMapper] 打开失败: {ex.Message}");
        }
    }

    /// <summary>删除文件 / 目录（含确认对话框）</summary>
    private void DeleteEntry(FileEntryLite entry)
    {
        var confirm = MessageBox.Show(
            $"确定删除「{entry.Name}」吗？\n此操作不可撤销。",
            "确认删除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            if (entry.IsDirectory) Directory.Delete(entry.FullPath, true);
            else File.Delete(entry.FullPath);
            Refresh();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DiskMapper] 删除失败: {ex.Message}");
            MessageBox.Show($"删除失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>安全复制文件（目标已存在则跳过）</summary>
    private static void CopyFileSafe(string src, string dst)
    {
        if (!File.Exists(dst)) File.Copy(src, dst);
    }

    /// <summary>安全移动文件（跨卷回退为复制 + 删除）</summary>
    private static void MoveFileSafe(string src, string dst)
    {
        try { File.Move(src, dst); }
        catch
        {
            CopyFileSafe(src, dst);
            try { File.Delete(src); } catch { /* 删除失败忽略 */ }
        }
    }

    /// <summary>递归复制目录</summary>
    private static void CopyDirectoryRecursive(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
        {
            try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: false); }
            catch { /* 单文件失败跳过 */ }
        }
        foreach (var d in Directory.EnumerateDirectories(src))
        {
            try { CopyDirectoryRecursive(d, Path.Combine(dst, Path.GetFileName(d))); }
            catch { /* 子目录失败跳过 */ }
        }
    }

    /// <summary>安全移动目录（跨卷回退为递归复制 + 删除）</summary>
    private static void MoveDirectorySafe(string src, string dst)
    {
        try { Directory.Move(src, dst); }
        catch
        {
            CopyDirectoryRecursive(src, dst);
            try { Directory.Delete(src, true); } catch { /* 删除失败忽略 */ }
        }
    }

    // ===== 辅助 =====

    /// <summary>拆分路径为可点击分段（名称 + 累积完整路径）</summary>
    private static List<(string Name, string Full)> SplitPath(string path)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrEmpty(path)) return result;
        try
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root))
            {
                var driveName = root.TrimEnd('\\');
                if (string.IsNullOrEmpty(driveName)) driveName = root;
                result.Add((driveName, root));

                var rest = path.Substring(root.Length).Trim('\\');
                if (!string.IsNullOrEmpty(rest))
                {
                    var acc = root.TrimEnd('\\');
                    foreach (var part in rest.Split('\\'))
                    {
                        if (string.IsNullOrEmpty(part)) continue;
                        acc = Path.Combine(acc, part);
                        result.Add((part, acc));
                    }
                }
            }
            else
            {
                result.Add((path, path));
            }
        }
        catch
        {
            result.Add((path, path));
        }
        return result;
    }

    /// <summary>格式化文件大小为可读字符串（B / KB / MB / GB）</summary>
    internal static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

/// <summary>
/// 文件列表行视图模型 — 包装 <see cref="FileEntryLite"/>，提供列绑定用的显示字符串
/// </summary>
internal sealed class FileRow
{
    /// <summary>原始目录条目</summary>
    public FileEntryLite Entry { get; }

    /// <summary>名称列显示文本（图标 + 名称）</summary>
    public string NameDisplay { get; }

    /// <summary>大小列显示文本（目录为空，文件为可读大小）</summary>
    public string SizeDisplay { get; }

    /// <summary>修改时间列显示文本</summary>
    public string DateDisplay { get; }

    internal FileRow(FileEntryLite entry)
    {
        Entry = entry;
        NameDisplay = $"{GetIcon(entry)}  {entry.Name}";
        SizeDisplay = entry.IsDirectory ? string.Empty : FilePane.FormatSize(entry.Size);
        DateDisplay = entry.LastModified.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>按条目类型 / 扩展名返回图标字形</summary>
    private static string GetIcon(FileEntryLite entry)
    {
        if (entry.IsDirectory) return "📁";
        return FileIndexService.GetCategory(entry.Extension) switch
        {
            FileCategory.Image => "🖼",
            FileCategory.Video => "🎬",
            FileCategory.Music => "🎵",
            FileCategory.Document => "📄",
            FileCategory.Application => "📦",
            FileCategory.Archive => "🗜",
            FileCategory.Code => "📝",
            _ => "📄",
        };
    }
}

/// <summary>
/// 大文件扫描结果窗口 — 异步扫描指定盘根，列出大文件并支持就地删除
///
/// 继承 <see cref="Panels.PanelWindowBase"/> 复用玻璃拟态视觉；扫描在后台线程运行，可取消。
/// 作为磁盘映射页的独立子窗口弹出。
/// </summary>
internal sealed class LargeFileScanWindow : zDesktop.App.Panels.PanelWindowBase
{
    private readonly DiskMapperService _service;
    private readonly string _driveRoot;
    private CancellationTokenSource? _cts;
    private readonly StackPanel _listPanel;
    private readonly TextBlock _statusText;
    private readonly Button _cancelBtn;
    private List<FileInfoLite> _items = new();

    /// <summary>
    /// 构造大文件扫描窗口
    /// </summary>
    /// <param name="service">磁盘映射服务</param>
    /// <param name="driveRoot">要扫描的盘根路径</param>
    internal LargeFileScanWindow(DiskMapperService service, string driveRoot)
        : base($"大文件扫描 — {driveRoot}", 680, 560, new DockPanel())
    {
        _service = service;
        _driveRoot = driveRoot;
        CloseOnDeactivate = false;

        var root = (DockPanel)ContentArea;
        root.Margin = new Thickness(12);
        root.LastChildFill = true;

        // 顶部状态栏
        var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
        _statusText = new TextBlock
        {
            Text = "正在扫描…",
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        topBar.Children.Add(_statusText);

        _cancelBtn = CreateSecondaryButton("取消");
        _cancelBtn.Click += (_, _) => CancelScan();
        DockPanel.SetDock(_cancelBtn, Dock.Right);
        topBar.Children.Add(_cancelBtn);

        DockPanel.SetDock(topBar, Dock.Top);
        root.Children.Add(topBar);

        // 结果列表（滚动）
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _listPanel = new StackPanel();
        scroll.Content = _listPanel;
        root.Children.Add(scroll);

        StartScan();
    }

    /// <summary>启动后台扫描</summary>
    private async void StartScan()
    {
        _cts = new CancellationTokenSource();
        _statusText.Text = $"正在扫描 {_driveRoot} …";
        _cancelBtn.Content = "取消";

        try
        {
            _items = await _service.ScanLargeFilesAsync(_driveRoot, 100, _cts.Token);
        }
        catch
        {
            _items = new();
        }

        _statusText.Text = _items.Count > 0
            ? $"扫描完成 · 共 {_items.Count} 个大文件（≥100MB）"
            : "未发现大文件";
        _cancelBtn.Content = "关闭";
        RenderList();
    }

    /// <summary>取消扫描</summary>
    private void CancelScan()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            _statusText.Text = "正在取消…";
        }
        else
        {
            Close();
        }
    }

    /// <summary>渲染结果列表（路径 + 大小 + 删除按钮）</summary>
    private void RenderList()
    {
        _listPanel.Children.Clear();

        if (_items.Count == 0)
        {
            _listPanel.Children.Add(new TextBlock
            {
                Text = "无大文件",
                FontSize = 13,
                FontFamily = Theme.UiFont,
                Foreground = Theme.TextFaint,
                Margin = new Thickness(4, 16, 4, 4),
            });
            return;
        }

        foreach (var item in _items)
        {
            _listPanel.Children.Add(CreateResultRow(item));
        }
    }

    /// <summary>创建单行扫描结果（路径 + 大小 + 删除按钮）</summary>
    private Border CreateResultRow(FileInfoLite item)
    {
        var row = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 4),
            CornerRadius = Theme.ControlRadius,
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.ContainerBorder,
            BorderThickness = new Thickness(1),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel();
        info.Children.Add(new TextBlock
        {
            Text = item.Path,
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(new TextBlock
        {
            Text = item.LastModified.ToString("yyyy-MM-dd HH:mm"),
            FontSize = 10,
            FontFamily = Theme.MonoFont,
            Foreground = Theme.TextFaint,
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var size = new TextBlock
        {
            Text = FilePane.FormatSize(item.Size),
            FontSize = 12,
            FontFamily = Theme.MonoFont,
            Foreground = new SolidColorBrush(Theme.Warning),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 8, 0),
        };
        Grid.SetColumn(size, 1);
        grid.Children.Add(size);

        var delBtn = new Button
        {
            Content = "删除",
            Height = 26,
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Background = Theme.InputBackground,
            Foreground = new SolidColorBrush(Theme.Error),
            BorderBrush = Theme.InputBorder,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Padding = new Thickness(10, 0, 10, 0),
            Tag = item,
        };
        delBtn.Click += (_, _) => DeleteItem(item, row);
        Grid.SetColumn(delBtn, 2);
        grid.Children.Add(delBtn);

        row.Child = grid;
        return row;
    }

    /// <summary>删除单个大文件并从列表移除</summary>
    private void DeleteItem(FileInfoLite item, Border row)
    {
        var confirm = MessageBox.Show(
            $"确定删除「{item.Path}」吗？\n此操作不可撤销。",
            "确认删除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            File.Delete(item.Path);
            _items.Remove(item);
            _listPanel.Children.Remove(row);
            _statusText.Text = _items.Count > 0
                ? $"已删除 · 剩余 {_items.Count} 个大文件"
                : "已全部删除";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DiskMapper] 大文件删除失败: {ex.Message}");
            MessageBox.Show($"删除失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>窗口关闭时取消后台扫描</summary>
    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        base.OnClosed(e);
    }
}
