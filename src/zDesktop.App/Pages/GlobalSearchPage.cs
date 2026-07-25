using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using zDesktop.Shell.Launcher;
using zDesktop.Shell.Search;
using zDesktop.Shell.Styles;

namespace zDesktop.App.Pages;

/// <summary>
/// 全局搜索内容页 — 聚合文件 / 应用 / 网页搜索
///
/// 视觉与交互（参考 pages/global-search.html 设计稿，WPF 实现）：
/// - 顶部大搜索框（输入即搜，防抖 150ms）
/// - 分类标签栏（全部/文件/应用/网页），带结果计数
/// - 中部结果列表（图标 + 文件名 + 路径 + 匹配高亮）+ 右侧预览面板
/// - 底部状态栏：搜索引擎状态（Everything / 内置索引 / 搜索中）
/// - 键盘：↑↓ 导航、Enter 打开、Ctrl+Enter 在文件夹显示、Shift+Enter 管理员运行
/// - 空查询显示最近搜索历史（持久化到 search-history.json，最多 20 条）
/// - 文件搜索优先 EverythingBridge，不可用降级 FileIndexService；应用搜索复用 AppIndex
///
/// 嵌入主窗口右侧内容区，不再独立弹窗。
/// </summary>
public sealed class GlobalSearchPage : ContentPage
{
    // ===== 依赖（由 App 注入）=====
    private readonly AppIndex _appIndex;
    private readonly FileIndexService _fileIndex;
    private readonly EverythingBridge _everything;

    // ===== UI 元素 =====
    private TextBox _searchBox = null!;
    private readonly WrapPanel _tabsPanel;
    private readonly ScrollViewer _resultsScroll;
    private readonly StackPanel _resultsPanel;
    private readonly StackPanel _previewPanel;
    private TextBlock _engineHint = null!;
    private TextBlock _statusText = null!;

    // ===== 状态 =====
    private List<SearchResultItem> _allItems = new();
    private List<SearchResultItem> _filteredItems = new();
    private int _selectedIndex = -1;
    private SearchTab _currentTab = SearchTab.All;
    private string _lastQuery = string.Empty;
    private string _engineStatusText = string.Empty;
    private DispatcherTimer? _debounce;
    private List<string> _history = new();

    private readonly Dictionary<SearchTab, Button> _tabButtons = new();
    private readonly Dictionary<SearchTab, TextBlock> _tabCounts = new();

    // ===== 历史持久化 =====
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");
    private static readonly string HistoryFile = Path.Combine(AppDataDir, "search-history.json");
    private const int MaxHistory = 20;

    /// <summary>
    /// 构造全局搜索内容页
    /// </summary>
    /// <param name="appIndex">应用索引（由 App 注入）</param>
    /// <param name="fileIndex">文件索引服务（由 App 注入）</param>
    public GlobalSearchPage(AppIndex appIndex, FileIndexService fileIndex)
    {
        _appIndex = appIndex;
        _fileIndex = fileIndex;
        _everything = new EverythingBridge();

        Title = "全局搜索";
        NavId = "global-search";

        var root = new DockPanel();
        root.Margin = new Thickness(16);
        root.LastChildFill = true;

        // ===== 搜索框区 =====
        var searchArea = BuildSearchArea();
        DockPanel.SetDock(searchArea, Dock.Top);
        root.Children.Add(searchArea);

        // ===== 分类标签栏 =====
        _tabsPanel = BuildTabs();
        DockPanel.SetDock(_tabsPanel, Dock.Top);
        root.Children.Add(_tabsPanel);

        // ===== 状态栏 =====
        var statusBar = BuildStatusBar();
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(statusBar);

        // ===== 中间：结果列表 + 预览面板 =====
        var body = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(284) });

        _resultsPanel = new StackPanel();
        _resultsScroll = new ScrollViewer
        {
            Content = _resultsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(_resultsScroll, 0);
        body.Children.Add(_resultsScroll);

        _previewPanel = new StackPanel();
        var previewScroll = new ScrollViewer
        {
            Content = _previewPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var previewBorder = new Border
        {
            Child = previewScroll,
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.ContainerBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(12),
        };
        Grid.SetColumn(previewBorder, 1);
        body.Children.Add(previewBorder);

        root.Children.Add(body);

        // ===== 事件 =====
        _searchBox.TextChanged += OnSearchChanged;
        _searchBox.PreviewKeyDown += OnSearchKeyDown;

        Content = root;

        // ===== 初始化 =====
        LoadHistory();
        UpdateEngineStatus();
        ShowHistory();

        // 页面加载后聚焦搜索框
        Loaded += (_, _) => _searchBox.Focus();
    }

    /// <summary>
    /// 设置搜索词并立即执行搜索 — 供桌面搜索框等外部入口调用
    /// </summary>
    /// <param name="query">搜索关键词</param>
    public void SetSearchQuery(string query)
    {
        _searchBox.Text = query;
        _searchBox.Focus();
        _searchBox.CaretIndex = query.Length;
        // TextChanged 事件会自动触发 DoSearch
    }

    // ===== UI 构建 =====

    /// <summary>构建顶部搜索框区（搜索框 + 键盘提示 + 引擎状态）</summary>
    private StackPanel BuildSearchArea()
    {
        var area = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        _searchBox = new TextBox
        {
            FontSize = 16,
            FontFamily = Theme.UiFont,
            Background = Theme.InputBackground,
            Foreground = Theme.TextRegular,
            BorderBrush = Theme.PrimaryAccent,
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(14, 10, 14, 10),
            Cursor = Cursors.IBeam,
            CaretBrush = Theme.TextPrimary,
        };
        area.Children.Add(_searchBox);

        var hintRow = new DockPanel { Margin = new Thickness(2, 8, 2, 0), LastChildFill = true };

        _engineHint = new TextBlock
        {
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.SuccessBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(_engineHint, Dock.Right);
        hintRow.Children.Add(_engineHint);

        var navHint = new TextBlock
        {
            Text = "↑↓ 导航   Enter 打开   Ctrl+Enter 在文件夹显示   Shift+Enter 管理员运行",
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextFaint,
            VerticalAlignment = VerticalAlignment.Center,
        };
        hintRow.Children.Add(navHint);

        area.Children.Add(hintRow);
        return area;
    }

    /// <summary>构建分类标签栏（全部/文件/应用/网页）</summary>
    private WrapPanel BuildTabs()
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var tabs = new[]
        {
            (SearchTab.All, "全部"),
            (SearchTab.File, "文件"),
            (SearchTab.App, "应用"),
            (SearchTab.Web, "网页"),
        };
        foreach (var (tab, label) in tabs)
        {
            var btn = CreateTabButton(tab, label);
            _tabButtons[tab] = btn;
            panel.Children.Add(btn);
        }
        UpdateTabStyles();
        return panel;
    }

    /// <summary>创建单个分类标签按钮（含计数 badge）</summary>
    private Button CreateTabButton(SearchTab tab, string label)
    {
        var btn = new Button
        {
            Height = 30,
            FontSize = 12,
            FontFamily = Theme.UiFont,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 0, 8, 0),
            Tag = tab,
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var count = new TextBlock
        {
            Text = "0",
            FontSize = 10,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(count);

        btn.Content = stack;
        btn.Click += (_, _) => SelectTab(tab);
        _tabCounts[tab] = count;
        return btn;
    }

    /// <summary>构建底部状态栏</summary>
    private Border BuildStatusBar()
    {
        _statusText = new TextBlock
        {
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border
        {
            Child = _statusText,
            Background = Theme.ListItemMuted,
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 8, 0, 0),
        };
    }

    // ===== 搜索逻辑 =====

    /// <summary>搜索框文本变化 — 防抖 150ms 后触发搜索</summary>
    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _debounce?.Stop();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            DoSearch();
        };
        _debounce = timer;
        timer.Start();
    }

    /// <summary>执行搜索（后台线程聚合结果，UI 线程渲染）</summary>
    private void DoSearch()
    {
        var query = _searchBox.Text.Trim();
        _lastQuery = query;

        if (string.IsNullOrEmpty(query))
        {
            ShowHistory();
            return;
        }

        _statusText.Text = "搜索中…";

        ThreadPool.QueueUserWorkItem(_ =>
        {
            var items = CollectResults(query);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _allItems = items;
                ApplyTabFilter();
                _statusText.Text = BuildStatusText();
            }));
        });
    }

    /// <summary>聚合文件 / 应用 / 网页搜索结果（后台线程调用）</summary>
    private List<SearchResultItem> CollectResults(string query)
    {
        var items = new List<SearchResultItem>();

        // 文件搜索：优先 Everything，降级 FileIndexService
        try
        {
            List<FileEntry> files;
            if (_everything.IsAvailable)
            {
                files = _everything.Search(query, 20);
                if (files.Count == 0) files = _fileIndex.Search(query, 20).ToList();
            }
            else
            {
                files = _fileIndex.Search(query, 20).ToList();
            }
            foreach (var f in files)
            {
                items.Add(new SearchResultItem
                {
                    Kind = ResultKind.File,
                    Title = f.Name,
                    Subtitle = FormatPath(f.Path),
                    Path = f.Path,
                    File = f,
                });
            }
        }
        catch
        {
            // 文件搜索失败不影响其他类别
        }

        // 应用搜索
        try
        {
            foreach (var a in _appIndex.Search(query, 5).ToList())
            {
                items.Add(new SearchResultItem
                {
                    Kind = ResultKind.App,
                    Title = a.Name,
                    Subtitle = "应用",
                    Path = a.ShortcutPath,
                    App = a,
                });
            }
        }
        catch
        {
            // 应用搜索失败不影响其他类别
        }

        // 网页搜索（非文件路径时追加浏览器搜索项）
        try
        {
            if (IsLikelyWebQuery(query))
            {
                items.Add(new SearchResultItem
                {
                    Kind = ResultKind.Web,
                    Title = $"在浏览器搜索: {query}",
                    Subtitle = "网页",
                    Path = BuildSearchUrl(query),
                });
            }
        }
        catch
        {
            // 网页项构建失败忽略
        }

        return items;
    }

    /// <summary>判断查询是否适合作为网页搜索词（非文件路径）</summary>
    private static bool IsLikelyWebQuery(string query)
    {
        return !query.Contains('\\') && !query.Contains(':');
    }

    /// <summary>构造浏览器搜索 URL（必应）</summary>
    private static string BuildSearchUrl(string query)
    {
        return $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}";
    }

    /// <summary>格式化文件路径为父目录显示</summary>
    private static string FormatPath(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(dir) ? path : dir;
        }
        catch { return path; }
    }

    // ===== 标签过滤与结果渲染 =====

    /// <summary>切换分类标签</summary>
    private void SelectTab(SearchTab tab)
    {
        _currentTab = tab;
        ApplyTabFilter();
    }

    /// <summary>按当前标签过滤结果并渲染</summary>
    private void ApplyTabFilter()
    {
        _filteredItems = _currentTab switch
        {
            SearchTab.File => _allItems.Where(i => i.Kind == ResultKind.File).ToList(),
            SearchTab.App => _allItems.Where(i => i.Kind == ResultKind.App).ToList(),
            SearchTab.Web => _allItems.Where(i => i.Kind == ResultKind.Web).ToList(),
            _ => _allItems.ToList(),
        };
        RenderResults(_filteredItems);
        UpdateTabCounts();
        UpdateTabStyles();
    }

    /// <summary>更新各标签的计数 badge</summary>
    private void UpdateTabCounts()
    {
        _tabCounts[SearchTab.All].Text = _allItems.Count.ToString();
        _tabCounts[SearchTab.File].Text = _allItems.Count(i => i.Kind == ResultKind.File).ToString();
        _tabCounts[SearchTab.App].Text = _allItems.Count(i => i.Kind == ResultKind.App).ToString();
        _tabCounts[SearchTab.Web].Text = _allItems.Count(i => i.Kind == ResultKind.Web).ToString();
    }

    /// <summary>更新标签按钮样式（当前激活态高亮）</summary>
    private void UpdateTabStyles()
    {
        foreach (var (tab, btn) in _tabButtons)
        {
            if (tab == _currentTab)
            {
                btn.Background = Theme.PrimarySubtle;
                btn.Foreground = Theme.PrimaryBrush;
                btn.BorderBrush = Theme.PrimaryAccent;
                btn.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                btn.Background = Theme.InputBackground;
                btn.Foreground = Theme.TextSecondary;
                btn.BorderBrush = Theme.InputBorder;
                btn.FontWeight = FontWeights.Normal;
            }
        }
    }

    /// <summary>渲染结果列表</summary>
    private void RenderResults(List<SearchResultItem> items)
    {
        _resultsPanel.Children.Clear();
        _filteredItems = items;
        _selectedIndex = items.Count > 0 ? 0 : -1;

        if (items.Count == 0)
        {
            _resultsPanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(_lastQuery)
                    ? "输入关键词开始搜索"
                    : $"未找到与「{_lastQuery}」相关的结果",
                FontSize = 13,
                FontFamily = Theme.UiFont,
                Foreground = Theme.TextFaint,
                Margin = new Thickness(4, 12, 4, 4),
            });
            UpdatePreview();
            return;
        }

        for (var i = 0; i < items.Count; i++)
        {
            _resultsPanel.Children.Add(CreateResultRow(items[i], i));
        }
        UpdateSelection();
        UpdatePreview();
    }

    /// <summary>创建单行结果（图标 + 高亮标题 + 路径 + 元信息）</summary>
    private Border CreateResultRow(SearchResultItem item, int index)
    {
        var row = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 4),
            CornerRadius = Theme.ControlRadius,
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.ContainerBorder,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Tag = index,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBox = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = Theme.SmallRadius,
            Background = Theme.PrimarySubtle,
            Child = new TextBlock
            {
                Text = GetIconGlyph(item),
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(iconBox, 0);
        grid.Children.Add(iconBox);

        var info = new StackPanel { Margin = new Thickness(10, 0, 10, 0) };
        var title = CreateHighlightedText(item.Title, _lastQuery);
        title.FontSize = 13;
        title.FontFamily = Theme.UiFont;
        title.Foreground = Theme.TextPrimary;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        info.Children.Add(title);

        info.Children.Add(new TextBlock
        {
            Text = item.Subtitle,
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var meta = new TextBlock
        {
            Text = GetMetaText(item),
            FontSize = 10,
            FontFamily = Theme.MonoFont,
            Foreground = Theme.TextFaint,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(meta, 2);
        grid.Children.Add(meta);

        row.Child = grid;

        row.MouseLeftButtonUp += (_, _) =>
        {
            _selectedIndex = index;
            UpdateSelection();
            UpdatePreview();
            ExecuteItem(item);
        };
        row.MouseEnter += (_, _) =>
        {
            _selectedIndex = index;
            UpdateSelection();
            UpdatePreview();
        };

        return row;
    }

    /// <summary>创建带匹配高亮的 TextBlock（用 Run 拼接 Inlines）</summary>
    private static TextBlock CreateHighlightedText(string text, string query)
    {
        var tb = new TextBlock();
        if (string.IsNullOrEmpty(text)) return tb;
        if (string.IsNullOrEmpty(query))
        {
            tb.Inlines.Add(new Run(text));
            return tb;
        }

        var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            tb.Inlines.Add(new Run(text));
            return tb;
        }

        if (idx > 0) tb.Inlines.Add(new Run(text.Substring(0, idx)));
        tb.Inlines.Add(new Run(text.Substring(idx, query.Length))
        {
            Background = Theme.PrimarySubtle,
            Foreground = Theme.PrimaryBrush,
            FontWeight = FontWeights.SemiBold,
        });
        if (idx + query.Length < text.Length)
            tb.Inlines.Add(new Run(text.Substring(idx + query.Length)));

        return tb;
    }

    /// <summary>更新选中行高亮并滚动到可视区</summary>
    private void UpdateSelection()
    {
        for (var i = 0; i < _resultsPanel.Children.Count; i++)
        {
            if (_resultsPanel.Children[i] is Border row)
            {
                if (i == _selectedIndex)
                {
                    row.Background = Theme.PrimarySubtle;
                    row.BorderBrush = Theme.PrimaryAccent;
                }
                else
                {
                    row.Background = Theme.ListItemBackground;
                    row.BorderBrush = Theme.ContainerBorder;
                }
            }
        }
        if (_selectedIndex >= 0 && _selectedIndex < _resultsPanel.Children.Count)
        {
            (_resultsPanel.Children[_selectedIndex] as FrameworkElement)?.BringIntoView();
        }
    }

    /// <summary>获取结果项的图标字形（按类别）</summary>
    private static string GetIconGlyph(SearchResultItem item)
    {
        if (item.Kind == ResultKind.App) return "🚀";
        if (item.Kind == ResultKind.Web) return "🌐";
        if (item.Kind == ResultKind.File && item.File != null)
        {
            return item.File.Category switch
            {
                FileCategory.Image => "🖼",
                FileCategory.Video => "🎬",
                FileCategory.Music => "🎵",
                FileCategory.Document => "📄",
                FileCategory.Application => "📦",
                FileCategory.Archive => "🗜",
                FileCategory.Code => "📝",
                _ => "📁",
            };
        }
        return "📁";
    }

    /// <summary>获取结果项右侧元信息文本</summary>
    private static string GetMetaText(SearchResultItem item)
    {
        if (item.Kind == ResultKind.File && item.File != null)
            return FormatSize(item.File.Size);
        if (item.Kind == ResultKind.App) return "应用";
        if (item.Kind == ResultKind.Web) return "网页";
        return string.Empty;
    }

    /// <summary>格式化文件大小为可读字符串</summary>
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    // ===== 预览面板 =====

    /// <summary>更新右侧预览面板（显示选中项详情）</summary>
    private void UpdatePreview()
    {
        _previewPanel.Children.Clear();
        if (_selectedIndex < 0 || _selectedIndex >= _filteredItems.Count)
        {
            _previewPanel.Children.Add(new TextBlock
            {
                Text = "选择一项查看预览",
                FontSize = 12,
                FontFamily = Theme.UiFont,
                Foreground = Theme.TextFaint,
                Margin = new Thickness(0, 16, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            return;
        }
        _previewPanel.Children.Add(BuildPreviewContent(_filteredItems[_selectedIndex]));
    }

    /// <summary>构建预览面板内容（图标 + 标题 + 信息列表 + 操作按钮）</summary>
    private StackPanel BuildPreviewContent(SearchResultItem item)
    {
        var panel = new StackPanel();

        var iconBox = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = Theme.ControlRadius,
            Background = Theme.PrimarySubtle,
            Child = new TextBlock
            {
                Text = GetIconGlyph(item),
                FontSize = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        panel.Children.Add(iconBox);

        panel.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextPrimary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new TextBlock
        {
            Text = item.Subtitle,
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        foreach (var (label, value) in GetPreviewInfo(item))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontFamily = Theme.UiFont,
                Foreground = Theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var val = new TextBlock
            {
                Text = value,
                FontSize = 11,
                FontFamily = Theme.UiFont,
                Foreground = Theme.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(val, Dock.Right);
            row.Children.Add(val);
            panel.Children.Add(row);
        }

        panel.Children.Add(new Border
        {
            Height = 1,
            Background = Theme.Divider,
            Margin = new Thickness(0, 8, 0, 12),
        });

        var openBtn = CreatePrimaryButton("打开");
        openBtn.Margin = new Thickness(0, 0, 0, 6);
        openBtn.Click += (_, _) => ExecuteItem(item);
        panel.Children.Add(openBtn);

        if (item.Kind == ResultKind.File)
        {
            var folderBtn = CreateSecondaryButton("在文件夹中显示");
            folderBtn.Margin = new Thickness(0, 0, 0, 6);
            folderBtn.Click += (_, _) => OpenInFolder(item);
            panel.Children.Add(folderBtn);

            var adminBtn = CreateSecondaryButton("以管理员运行");
            adminBtn.Click += (_, _) => RunAsAdmin(item);
            panel.Children.Add(adminBtn);
        }

        return panel;
    }

    /// <summary>获取预览面板的信息键值对</summary>
    private static IEnumerable<(string Label, string Value)> GetPreviewInfo(SearchResultItem item)
    {
        if (item.Kind == ResultKind.File && item.File != null)
        {
            var f = item.File;
            yield return ("类型", f.Category.ToString());
            yield return ("大小", FormatSize(f.Size));
            yield return ("修改时间", f.LastModified.ToString("yyyy-MM-dd HH:mm"));
            yield return ("位置", FormatPath(f.Path));
        }
        else if (item.Kind == ResultKind.App && item.App != null)
        {
            yield return ("类型", "应用程序");
            yield return ("目标", item.App.TargetPath);
        }
        else if (item.Kind == ResultKind.Web)
        {
            yield return ("类型", "网页搜索");
            yield return ("URL", item.Path);
        }
    }

    // ===== 键盘导航与执行 =====

    /// <summary>搜索框键盘事件：↑↓ 导航、Enter 打开（含 Ctrl/Shift 修饰键）</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (_filteredItems.Count == 0 && e.Key != Key.Enter) return;

        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                _selectedIndex = Math.Min(_selectedIndex + 1, _filteredItems.Count - 1);
                UpdateSelection();
                UpdatePreview();
                break;

            case Key.Up:
                e.Handled = true;
                _selectedIndex = Math.Max(_selectedIndex - 1, 0);
                UpdateSelection();
                UpdatePreview();
                break;

            case Key.Enter:
                e.Handled = true;
                var item = (_selectedIndex >= 0 && _selectedIndex < _filteredItems.Count)
                    ? _filteredItems[_selectedIndex]
                    : _filteredItems.FirstOrDefault();
                if (item != null)
                {
                    var mod = Keyboard.Modifiers;
                    if ((mod & ModifierKeys.Control) != 0) OpenInFolder(item);
                    else if ((mod & ModifierKeys.Shift) != 0) RunAsAdmin(item);
                    else ExecuteItem(item);
                }
                break;
        }
    }

    /// <summary>打开选中项（文件/应用/网页），并记录搜索历史</summary>
    private void ExecuteItem(SearchResultItem item)
    {
        try
        {
            switch (item.Kind)
            {
                case ResultKind.File:
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = item.Path,
                        UseShellExecute = true,
                    });
                    break;
                case ResultKind.App:
                    _appIndex.Launch(item.App!);
                    break;
                case ResultKind.Web:
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = item.Path,
                        UseShellExecute = true,
                    });
                    break;
            }
            AddHistory(_lastQuery);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GlobalSearch] 打开失败: {ex.Message}");
        }
    }

    /// <summary>在资源管理器中定位选中文件</summary>
    private void OpenInFolder(SearchResultItem item)
    {
        if (item.Kind != ResultKind.File || string.IsNullOrEmpty(item.Path)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{item.Path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GlobalSearch] 在文件夹中显示失败: {ex.Message}");
        }
    }

    /// <summary>以管理员权限运行选中项</summary>
    private void RunAsAdmin(SearchResultItem item)
    {
        if (string.IsNullOrEmpty(item.Path)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = item.Path,
                Verb = "runas",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GlobalSearch] 管理员运行失败: {ex.Message}");
        }
    }

    // ===== 搜索引擎状态 =====

    /// <summary>更新搜索引擎状态提示（Everything 已连接 / 内置索引）</summary>
    private void UpdateEngineStatus()
    {
        try
        {
            if (_everything.IsAvailable)
            {
                var count = _everything.IndexedFileCount;
                _engineStatusText = count >= 0
                    ? $"Everything 已连接 · 索引 {count:N0} 文件"
                    : "Everything 已连接";
                _engineHint.Foreground = Theme.SuccessBrush;
            }
            else
            {
                _engineStatusText = $"内置索引 · {_fileIndex.Count:N0} 文件";
                _engineHint.Foreground = Theme.TextSecondary;
            }
        }
        catch
        {
            _engineStatusText = "搜索引擎状态未知";
        }
        _engineHint.Text = _engineStatusText;
    }

    /// <summary>构建状态栏文本（结果数分类统计 + 引擎状态）</summary>
    private string BuildStatusText()
    {
        if (_allItems.Count == 0) return $"未找到结果 — {_engineStatusText}";
        var fileCount = _allItems.Count(i => i.Kind == ResultKind.File);
        var appCount = _allItems.Count(i => i.Kind == ResultKind.App);
        var webCount = _allItems.Count(i => i.Kind == ResultKind.Web);
        return $"共 {_allItems.Count} 项（文件 {fileCount} · 应用 {appCount} · 网页 {webCount}）— {_engineStatusText}";
    }

    // ===== 搜索历史 =====

    /// <summary>从 search-history.json 加载最近搜索历史</summary>
    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryFile)) return;
            var json = File.ReadAllText(HistoryFile);
            _history = JsonSerializer.Deserialize<List<string>>(json) ?? new();
        }
        catch
        {
            _history = new();
        }
    }

    /// <summary>保存搜索历史到 search-history.json</summary>
    private void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var json = JsonSerializer.Serialize(_history, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(HistoryFile, json);
        }
        catch
        {
            // 持久化失败不影响功能
        }
    }

    /// <summary>记录一次搜索历史（去重，最多保留 MaxHistory 条）</summary>
    private void AddHistory(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        try
        {
            _history.Remove(query);
            _history.Insert(0, query);
            while (_history.Count > MaxHistory) _history.RemoveAt(_history.Count - 1);
            SaveHistory();
        }
        catch
        {
            // 历史记录失败不影响功能
        }
    }

    /// <summary>空查询时显示最近搜索历史</summary>
    private void ShowHistory()
    {
        _allItems.Clear();
        _filteredItems.Clear();
        _resultsPanel.Children.Clear();
        _selectedIndex = -1;

        if (_history.Count == 0)
        {
            _resultsPanel.Children.Add(new TextBlock
            {
                Text = "输入关键词开始搜索 · 文件 / 应用 / 网页",
                FontSize = 13,
                FontFamily = Theme.UiFont,
                Foreground = Theme.TextFaint,
                Margin = new Thickness(4, 16, 4, 4),
            });
        }
        else
        {
            _resultsPanel.Children.Add(new TextBlock
            {
                Text = "最近搜索",
                FontSize = 11,
                FontFamily = Theme.UiFont,
                Foreground = Theme.TextSecondary,
                Margin = new Thickness(4, 4, 4, 8),
            });
            for (var i = 0; i < _history.Count; i++)
            {
                _resultsPanel.Children.Add(CreateHistoryRow(_history[i], i));
            }
        }

        UpdateTabCounts();
        _statusText.Text = _engineStatusText;
        UpdatePreview();
    }

    /// <summary>创建历史记录行（点击回填到搜索框）</summary>
    private Border CreateHistoryRow(string query, int index)
    {
        var row = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 4),
            CornerRadius = Theme.ControlRadius,
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.ContainerBorder,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Tag = index,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new TextBlock
        {
            Text = "🕘",
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var text = new TextBlock
        {
            Text = query,
            FontSize = 13,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextRegular,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        row.Child = grid;
        row.MouseLeftButtonUp += (_, _) =>
        {
            _searchBox.Text = query;
            _searchBox.CaretIndex = query.Length;
            _searchBox.Focus();
        };
        return row;
    }
}

/// <summary>搜索结果分类标签</summary>
internal enum SearchTab
{
    /// <summary>全部</summary>
    All,
    /// <summary>文件</summary>
    File,
    /// <summary>应用</summary>
    App,
    /// <summary>网页</summary>
    Web,
}

/// <summary>搜索结果项类型</summary>
internal enum ResultKind
{
    /// <summary>文件</summary>
    File,
    /// <summary>应用</summary>
    App,
    /// <summary>网页</summary>
    Web,
}

/// <summary>
/// 统一的搜索结果项 — 聚合文件 / 应用 / 网页三种结果
/// </summary>
internal sealed class SearchResultItem
{
    /// <summary>结果类型</summary>
    public ResultKind Kind { get; set; }

    /// <summary>主标题（文件名 / 应用名 / 搜索词）</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>副标题（父目录 / 类别）</summary>
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>路径（文件路径 / 快捷方式 / URL）</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>关联的文件条目（仅 Kind=File 时有效）</summary>
    public FileEntry? File { get; set; }

    /// <summary>关联的应用条目（仅 Kind=App 时有效）</summary>
    public AppEntry? App { get; set; }
}
