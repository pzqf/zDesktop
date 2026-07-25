using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using zDesktop.Core.Widgets;
using zDesktop.Shell.Launcher;
using zDesktop.Shell.Styles;
using zDesktop.Shell.Widgets;

namespace zDesktop.Widgets.Launcher;

/// <summary>
/// 快捷启动器组件 — 桌面搜索框，输入即搜应用/文件/网页
///
/// 功能：
/// 1. 输入关键词 → 实时搜索已安装应用
/// 2. 上下键导航结果，回车启动选中项
/// 3. 输入 URL 或含 .com/.cn → 直接打开网页
/// 4. 输入路径 → 打开文件资源管理器
/// </summary>
public class QuickLauncherWidget : WidgetBase
{
    private readonly AppIndex _appIndex;
    private readonly TextBox _searchBox;
    private readonly StackPanel _resultsPanel;
    private readonly ScrollViewer _scroll;
    private List<AppEntry> _currentResults = new();
    private int _selectedIndex = -1;

    public override WidgetDescriptor Descriptor { get; } = new()
    {
        Id = "quick-launcher",
        Name = "快捷启动器",
        Description = "搜索并启动应用、打开网页",
        DefaultWidth = 360,
        DefaultHeight = 320,
        AllowResize = false,
        ConfigSchema = new()
        {
            new WidgetConfigField
            {
                Key = "searchEngine",
                Label = "网页搜索引擎",
                FieldType = WidgetConfigFieldType.Choice,
                DefaultValue = "bing",
                Description = "输入非应用名称时使用的搜索引擎",
                Choices = new()
                {
                    new ConfigChoice { Value = "bing", Label = "必应" },
                    new ConfigChoice { Value = "google", Label = "Google" },
                    new ConfigChoice { Value = "baidu", Label = "百度" },
                },
            },
            new WidgetConfigField
            {
                Key = "maxResults",
                Label = "最大结果数",
                FieldType = WidgetConfigFieldType.Number,
                DefaultValue = 8,
                Min = 3,
                Max = 20,
                Step = 1,
                Description = "搜索结果最大显示条数",
            },
        },
    };

    public QuickLauncherWidget()
    {
        _appIndex = new AppIndex();

        var panel = new StackPanel { Margin = new Thickness(8) };

        // ===== 搜索框 =====
        _searchBox = new TextBox
        {
            FontSize = 16,
            FontFamily = Theme.UiFont,
            Background = Theme.InputBackground,
            Foreground = Theme.TextRegular,
            BorderBrush = Theme.PrimaryAccent,
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(12, 8, 12, 8),
            Cursor = Cursors.IBeam,
            CaretBrush = Brushes.White,
        };
        _searchBox.TextChanged += OnSearchChanged;
        _searchBox.PreviewKeyDown += OnSearchKeyDown;
        panel.Children.Add(_searchBox);

        // ===== 搜索提示 =====
        var hint = new TextBlock
        {
            Text = "输入应用名搜索 · URL 直接打开网页",
            FontSize = 10,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextFaint,
            Margin = new Thickness(4, 6, 4, 4),
        };
        panel.Children.Add(hint);

        // ===== 结果列表 =====
        _resultsPanel = new StackPanel();
        _scroll = new ScrollViewer
        {
            Content = _resultsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 4, 0, 0),
        };
        panel.Children.Add(_scroll);

        Content = new Grid
        {
            Background = Brushes.Transparent,
            Children = { panel },
        };
    }

    public override void OnInitialize()
    {
        // 异步加载应用索引，不阻塞 UI
        System.Threading.Tasks.Task.Run(() =>
        {
            _appIndex.Load();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (string.IsNullOrEmpty(_searchBox.Text))
                    ShowResults(_appIndex.Entries.Take(GetMaxResults()).ToList());
            }));
        });
    }

    public override void OnConfigChanged()
    {
        // 配置变更后重新搜索
        DoSearch();
    }

    // ===== 搜索逻辑 =====

    private void DoSearch()
    {
        var query = _searchBox.Text.Trim();

        if (string.IsNullOrEmpty(query))
        {
            // 空搜索 → 显示常用应用
            ShowResults(_appIndex.Entries.Take(GetMaxResults()).ToList());
            return;
        }

        // 判断是否是 URL / 文件路径
        if (IsUrl(query))
        {
            ShowWebResult(query);
            return;
        }

        if (IsFilePath(query))
        {
            ShowFileResult(query);
            return;
        }

        // 应用搜索
        var results = _appIndex.Search(query, GetMaxResults()).ToList();
        ShowResults(results);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        DoSearch();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (_currentResults.Count == 0) return;

        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                _selectedIndex = Math.Min(_selectedIndex + 1, _currentResults.Count - 1);
                UpdateSelection();
                break;

            case Key.Up:
                e.Handled = true;
                _selectedIndex = Math.Max(_selectedIndex - 1, 0);
                UpdateSelection();
                break;

            case Key.Enter:
                e.Handled = true;
                if (_selectedIndex >= 0 && _selectedIndex < _currentResults.Count)
                {
                    ExecuteResult(_currentResults[_selectedIndex]);
                }
                else if (_currentResults.Count > 0)
                {
                    ExecuteResult(_currentResults[0]);
                }
                break;

            case Key.Escape:
                e.Handled = true;
                _searchBox.Clear();
                break;
        }
    }

    // ===== 结果展示 =====

    private void ShowResults(List<AppEntry> results)
    {
        _currentResults = results;
        _selectedIndex = results.Count > 0 ? 0 : -1;

        _resultsPanel.Children.Clear();

        for (var i = 0; i < results.Count; i++)
        {
            _resultsPanel.Children.Add(CreateResultItem(results[i], i));
        }

        UpdateSelection();
    }

    private void ShowWebResult(string url)
    {
        _currentResults = new List<AppEntry>
        {
            new() { Name = $"打开网页: {url}", TargetPath = BuildSearchUrl(url) },
        };
        _selectedIndex = 0;

        _resultsPanel.Children.Clear();
        var item = CreateResultItem(_currentResults[0], 0, isWeb: true);
        _resultsPanel.Children.Add(item);
        UpdateSelection();
    }

    private void ShowFileResult(string path)
    {
        _currentResults = new List<AppEntry>
        {
            new() { Name = $"打开: {path}", TargetPath = path },
        };
        _selectedIndex = 0;

        _resultsPanel.Children.Clear();
        var item = CreateResultItem(_currentResults[0], 0, isFile: true);
        _resultsPanel.Children.Add(item);
        UpdateSelection();
    }

    private UIElement CreateResultItem(AppEntry entry, int index, bool isWeb = false, bool isFile = false)
    {
        var item = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 2),
            CornerRadius = Theme.ControlRadius,
            Cursor = Cursors.Hand,
            Tag = index,
        };

        var iconText = isWeb ? "🌐" : isFile ? "📁" : "📦";

        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        panel.Children.Add(new TextBlock
        {
            Text = iconText,
            FontSize = 16,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });

        panel.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = 13,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextRegular,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 280,
        });

        item.Child = panel;

        // 点击启动
        item.MouseLeftButtonUp += (_, _) =>
        {
            _selectedIndex = index;
            ExecuteResult(entry);
        };

        // 悬停高亮
        item.MouseEnter += (_, _) =>
        {
            _selectedIndex = index;
            UpdateSelection();
        };

        return item;
    }

    /// <summary>更新选中项高亮</summary>
    private void UpdateSelection()
    {
        for (var i = 0; i < _resultsPanel.Children.Count; i++)
        {
            if (_resultsPanel.Children[i] is Border item)
            {
                if (i == _selectedIndex)
                {
                    item.Background = Theme.PrimarySubtle;
                }
                else
                {
                    item.Background = Brushes.Transparent;
                }
            }
        }

        // 滚动到选中项
        if (_selectedIndex >= 0 && _selectedIndex < _resultsPanel.Children.Count)
        {
            var item = _resultsPanel.Children[_selectedIndex] as FrameworkElement;
            item?.BringIntoView();
        }
    }

    /// <summary>执行搜索结果 — 启动应用或打开网页</summary>
    private void ExecuteResult(AppEntry entry)
    {
        try
        {
            if (entry.TargetPath.StartsWith("http"))
            {
                // 网页
                Process.Start(new ProcessStartInfo
                {
                    FileName = entry.TargetPath,
                    UseShellExecute = true,
                });
            }
            else if (File.Exists(entry.ShortcutPath))
            {
                // 应用（快捷方式）
                _appIndex.Launch(entry);
            }
            else if (File.Exists(entry.TargetPath) || Directory.Exists(entry.TargetPath))
            {
                // 文件/文件夹
                Process.Start(new ProcessStartInfo
                {
                    FileName = entry.TargetPath,
                    UseShellExecute = true,
                });
            }
            else
            {
                // 默认用搜索引擎搜索
                Process.Start(new ProcessStartInfo
                {
                    FileName = BuildSearchUrl(entry.Name),
                    UseShellExecute = true,
                });
            }

            // 启动后清空搜索框
            _searchBox.Clear();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QuickLauncher] 启动失败: {ex.Message}");
        }
    }

    // ===== 工具方法 =====

    private int GetMaxResults()
    {
        return GetConfig("maxResults", 8);
    }

    private string BuildSearchUrl(string query)
    {
        var engine = GetConfig("searchEngine", "bing");
        var encoded = Uri.EscapeDataString(query);
        return engine switch
        {
            "google" => $"https://www.google.com/search?q={encoded}",
            "baidu" => $"https://www.baidu.com/s?wd={encoded}",
            _ => $"https://www.bing.com/search?q={encoded}",
        };
    }

    private static bool IsUrl(string text)
    {
        return text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               (text.Contains('.') && !text.Contains(' ') && !text.Contains('\\'));
    }

    private static bool IsFilePath(string text)
    {
        return text.Contains('\\') && (text.Contains(':') || text.StartsWith("\\\\"));
    }
}
