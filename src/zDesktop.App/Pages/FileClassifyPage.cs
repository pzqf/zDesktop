using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using zDesktop.Shell.Classifier;
using zDesktop.Shell.Search;
using zDesktop.Shell.Styles;

// App 项目同时启用 WPF + WinForms + System.Drawing，Brush 在 System.Drawing 与 System.Windows.Media 间歧义，
// 此处显式别名到 WPF 画笔（与 csproj 已有的 Color/Brushes/SolidColorBrush 别名一致）
using Brush = System.Windows.Media.Brush;

namespace zDesktop.App.Pages;

/// <summary>
/// 文件分类内容页 — 桌面文件智能分类与一键整理
///
/// 视觉分区（参考 pages/file-classify.html，WPF 实现）：
/// - 顶部：操作栏（扫描桌面 / 一键整理 / 自定义分区）
/// - 中上部：存储分布可视化（水平堆叠条 + 图例，按类别占比）
/// - 中部：分类列表（可折叠区块，展开显示文件明细，点击在资源管理器定位）
/// - 中下部：自定义分区管理（添加表单 + 分区列表，可删除）
/// - 底部：整理结果摘要
///
/// 交互：
/// - 扫描用 Task.Run 异步执行，UI 通过 Dispatcher.BeginInvoke 刷新
/// - 一键整理前弹确认提示（MessageBox）
/// - 类别语义色来自 FileClassifierService.CategoryColors（数据语义色，非 UI token）
/// - 其余颜色/字体/圆角一律引用 Theme 常量
///
/// 嵌入主窗口右侧内容区，不再独立弹窗。
/// </summary>
public sealed class FileClassifyPage : ContentPage
{
    /// <summary>文件分类服务（由 App 注入）</summary>
    private readonly FileClassifierService _service;

    /// <summary>当前扫描结果（UI 线程访问）</summary>
    private List<ClassifiedFile> _files = new();

    // ===== UI 元素引用 =====

    /// <summary>堆叠条段容器（Grid 列）</summary>
    private Grid _barGrid = new();

    /// <summary>图例容器</summary>
    private WrapPanel _legendPanel = new();

    /// <summary>分布标题右侧总计文本</summary>
    private readonly TextBlock _totalText;

    /// <summary>分类列表容器</summary>
    private readonly StackPanel _categoryListPanel;

    /// <summary>空状态提示</summary>
    private readonly TextBlock _emptyHint;

    /// <summary>自定义分区添加表单（默认折叠）</summary>
    private readonly Border _addPartitionForm;

    /// <summary>分区列表容器</summary>
    private readonly StackPanel _partitionListPanel;

    /// <summary>分区名称输入框</summary>
    private readonly TextBox _partitionNameBox;

    /// <summary>扩展名输入框（逗号/空格分隔）</summary>
    private readonly TextBox _partitionExtBox;

    /// <summary>文件名正则输入框（可选）</summary>
    private readonly TextBox _partitionPatternBox;

    /// <summary>当前选中的分区颜色</summary>
    private string _selectedPartitionColor = "#6c5ce7";

    /// <summary>底部结果摘要文本</summary>
    private readonly TextBlock _resultText;

    /// <summary>扫描按钮（整理时禁用）</summary>
    private readonly Button _scanButton;

    /// <summary>整理按钮（扫描中禁用）</summary>
    private readonly Button _organizeButton;

    /// <summary>是否正在异步操作中（避免重入）</summary>
    private bool _busy;

    /// <summary>
    /// 构造文件分类内容页
    /// </summary>
    /// <param name="service">文件分类服务实例（由 App 创建并注入）</param>
    public FileClassifyPage(FileClassifierService service)
    {
        _service = service;
        Title = "文件分类与整理";
        NavId = "file-classify";

        var root = new DockPanel();
        root.LastChildFill = true;

        // ===== 顶部：操作栏 =====
        var actionBar = BuildActionBar(out _scanButton, out _organizeButton);
        DockPanel.SetDock(actionBar, Dock.Top);
        root.Children.Add(actionBar);

        // ===== 中上部：存储分布可视化 =====
        var distSection = BuildDistributionSection(out _totalText);
        DockPanel.SetDock(distSection, Dock.Top);
        root.Children.Add(distSection);

        // ===== 底部：结果摘要 =====
        var resultBar = BuildResultBar(out _resultText);
        DockPanel.SetDock(resultBar, Dock.Bottom);
        root.Children.Add(resultBar);

        // ===== 中部：可滚动内容（分类列表 + 自定义分区）=====
        _categoryListPanel = new StackPanel();
        _emptyHint = new TextBlock
        {
            Text = "点击「扫描桌面」开始分析当前桌面文件",
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextFaint,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 40, 0, 0),
        };
        _categoryListPanel.Children.Add(_emptyHint);

        _addPartitionForm = BuildAddPartitionForm(
            out _partitionNameBox, out _partitionExtBox, out _partitionPatternBox);
        _addPartitionForm.Visibility = Visibility.Collapsed;

        _partitionListPanel = new StackPanel();

        var contentStack = new StackPanel();
        contentStack.Children.Add(BuildSectionHeader("文件分类"));
        contentStack.Children.Add(_categoryListPanel);
        contentStack.Children.Add(BuildSectionHeader("自定义分区"));
        contentStack.Children.Add(_addPartitionForm);
        contentStack.Children.Add(_partitionListPanel);

        var scroll = new ScrollViewer
        {
            Content = contentStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(16, 8, 16, 16),
        };
        root.Children.Add(scroll);

        Content = root;

        // 首次渲染分区列表（即使未扫描也可管理分区）
        RenderPartitions();

        // 自动触发一次扫描
        Loaded += (_, _) => BeginScan();
    }

    // ============================================================
    //  顶部操作栏
    // ============================================================

    /// <summary>构建顶部操作栏：扫描桌面 + 一键整理 + 自定义分区</summary>
    private Border BuildActionBar(out Button scanButton, out Button organizeButton)
    {
        scanButton = CreateSecondaryButton("扫描桌面");
        scanButton.Click += (_, _) => BeginScan();

        organizeButton = CreatePrimaryButton("一键整理");
        organizeButton.Click += (_, _) => OnOrganizeClicked();

        var partitionBtn = CreateSecondaryButton("自定义分区");
        partitionBtn.Click += (_, _) =>
        {
            _addPartitionForm.Visibility = _addPartitionForm.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        panel.Children.Add(scanButton);
        panel.Children.Add(partitionBtn);
        panel.Children.Add(organizeButton);

        return new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = panel,
        };
    }

    // ============================================================
    //  存储分布可视化
    // ============================================================

    /// <summary>构建存储分布区：标题 + 水平堆叠条 + 图例</summary>
    private Border BuildDistributionSection(out TextBlock totalText)
    {
        var headerLeft = new TextBlock
        {
            Text = "存储分布",
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
        };
        totalText = new TextBlock
        {
            Text = "总计 0 B",
            FontSize = 12,
            FontFamily = Theme.MonoFont,
            Foreground = Theme.TextFaint,
        };

        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(headerLeft, Dock.Left);
        header.Children.Add(headerLeft);
        header.Children.Add(totalText); // 右对齐

        // 堆叠条 — 外层圆角裁剪，内层 Grid 按比例分列
        _barGrid.Height = 8;
        _barGrid.Margin = new Thickness(0, 8, 0, 8);
        var barClip = new Border
        {
            CornerRadius = Theme.SmallRadius,
            ClipToBounds = true,
            Background = Theme.ChartBackground,
            Child = _barGrid,
        };

        _legendPanel = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(barClip);
        stack.Children.Add(_legendPanel);

        return new Border
        {
            Padding = new Thickness(16, 10, 16, 12),
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = stack,
        };
    }

    /// <summary>渲染存储分布堆叠条与图例</summary>
    private void RenderDistribution(List<ClassifiedFile> files)
    {
        _barGrid.Children.Clear();
        _barGrid.ColumnDefinitions.Clear();
        _legendPanel.Children.Clear();

        var distribution = _service.GetDistribution(files);
        var totalSize = FileClassifierService.GetTotalSize(files);
        _totalText.Text = $"总计 {FormatSize(totalSize)}";

        if (distribution.Count == 0 || totalSize <= 0)
        {
            // 空堆叠条 — 仅显示底色
            _barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return;
        }

        foreach (var entry in distribution)
        {
            // 按总大小比例分配列宽
            _barGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(entry.TotalSize, 1), GridUnitType.Star),
            });

            var color = FileClassifierService.CategoryColors.TryGetValue(entry.Category, out var c)
                ? c
                : Theme.MutedForeground;
            var pct = totalSize > 0 ? entry.TotalSize * 100.0 / totalSize : 0;
            var folderName = FileClassifierService.CategoryFolderNames.TryGetValue(entry.Category, out var fn)
                ? fn
                : entry.Category.ToString();

            var seg = new Border
            {
                Background = new SolidColorBrush(color),
                ToolTip = $"{folderName} | {entry.Count} 个文件 | {FormatSize(entry.TotalSize)} ({pct:F1}%)",
            };
            Grid.SetColumn(seg, _barGrid.ColumnDefinitions.Count - 1);
            _barGrid.Children.Add(seg);

            _legendPanel.Children.Add(BuildLegendItem(folderName, color, entry.Count, entry.TotalSize));
        }
    }

    /// <summary>构建单个图例项（色块 + 名称 + 数量）</summary>
    private UIElement BuildLegendItem(string name, Color color, int count, long size)
    {
        var dot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = $"{name} {count}个",
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 14, 4),
        };
        panel.Children.Add(dot);
        panel.Children.Add(label);
        return panel;
    }

    // ============================================================
    //  分类列表（可折叠）
    // ============================================================

    /// <summary>渲染分类列表 — 每个类别一个可折叠区块</summary>
    private void RenderCategoryList(List<ClassifiedFile> files)
    {
        _categoryListPanel.Children.Clear();

        if (files.Count == 0)
        {
            _categoryListPanel.Children.Add(_emptyHint);
            return;
        }

        var byCategory = files
            .GroupBy(f => f.Category)
            .OrderByDescending(g => g.Sum(f => f.Size));

        foreach (var group in byCategory)
        {
            _categoryListPanel.Children.Add(BuildCategoryBlock(group.Key, group.ToList()));
        }
    }

    /// <summary>构建单个类别可折叠区块</summary>
    private Border BuildCategoryBlock(FileCategory category, List<ClassifiedFile> files)
    {
        var color = FileClassifierService.CategoryColors.TryGetValue(category, out var c)
            ? c
            : Theme.MutedForeground;
        var folderName = FileClassifierService.CategoryFolderNames.TryGetValue(category, out var fn)
            ? fn
            : category.ToString();
        var totalSize = files.Sum(f => f.Size);

        // 色点
        var dot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var nameText = new TextBlock
        {
            Text = folderName,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };

        var countText = new TextBlock
        {
            Text = $"{files.Count} 个",
            FontSize = 11,
            FontFamily = Theme.MonoFont,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var sizeText = new TextBlock
        {
            Text = FormatSize(totalSize),
            FontSize = 11,
            FontFamily = Theme.MonoFont,
            Foreground = Theme.TextFaint,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var arrow = new TextBlock
        {
            Text = "\u25B8", // ▸ 右指三角
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var header = new DockPanel { LastChildFill = true, Height = 20 };
        DockPanel.SetDock(arrow, Dock.Right);
        DockPanel.SetDock(sizeText, Dock.Right);
        DockPanel.SetDock(countText, Dock.Right);
        header.Children.Add(arrow);
        header.Children.Add(sizeText);
        header.Children.Add(countText);
        header.Children.Add(dot);
        header.Children.Add(nameText);

        // 文件明细容器（默认折叠）
        var detailPanel = new StackPanel
        {
            Margin = new Thickness(20, 6, 0, 4),
            Visibility = Visibility.Collapsed,
        };
        foreach (var f in files)
        {
            detailPanel.Children.Add(BuildFileRow(f));
        }

        var card = new Border
        {
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = Cursors.Hand,
            Child = new StackPanel(),
        };

        var content = (StackPanel)card.Child;
        content.Children.Add(header);
        content.Children.Add(detailPanel);

        // 点击头部切换展开/折叠
        header.MouseLeftButtonUp += (_, _) =>
        {
            var expanded = detailPanel.Visibility == Visibility.Visible;
            detailPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            arrow.Text = expanded ? "\u25B8" : "\u25BE"; // ▸ / ▾
        };

        return card;
    }

    /// <summary>构建单个文件明细行 — 点击在资源管理器定位</summary>
    private UIElement BuildFileRow(ClassifiedFile file)
    {
        var timeTag = FileClassifierService.GetTimeTag(file.LastModified);
        var tagText = timeTag switch
        {
            TimeTag.Today => "今天",
            TimeTag.ThisWeek => "本周",
            TimeTag.ThisMonth => "本月",
            _ => "更早",
        };

        var nameText = new TextBlock
        {
            Text = file.Name,
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextRegular,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var pathText = new TextBlock
        {
            Text = file.Path,
            FontSize = 10,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextFaint,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var info = new StackPanel();
        info.Children.Add(nameText);
        info.Children.Add(pathText);

        var sizeText = new TextBlock
        {
            Text = FormatSize(file.Size),
            FontSize = 10,
            FontFamily = Theme.MonoFont,
            Foreground = Theme.TextFaint,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var dateText = new TextBlock
        {
            Text = $"{file.LastModified:yyyy-MM-dd}",
            FontSize = 10,
            FontFamily = Theme.MonoFont,
            Foreground = Theme.TextFaint,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };

        var tag = new Border
        {
            Background = Theme.PrimarySubtle,
            CornerRadius = Theme.SmallRadius,
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = tagText,
                FontSize = 10,
                FontFamily = Theme.UiFont,
                Foreground = Theme.PrimaryBrush,
            },
        };

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(tag, Dock.Right);
        DockPanel.SetDock(dateText, Dock.Right);
        DockPanel.SetDock(sizeText, Dock.Right);
        row.Children.Add(tag);
        row.Children.Add(dateText);
        row.Children.Add(sizeText);
        row.Children.Add(info);

        var border = new Border
        {
            Padding = new Thickness(8, 4, 8, 4),
            Cursor = Cursors.Hand,
            Child = row,
        };
        border.MouseEnter += (_, _) => border.Background = Theme.PrimarySubtle;
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonUp += (_, _) => RevealInExplorer(file.Path);

        return border;
    }

    // ============================================================
    //  自定义分区管理
    // ============================================================

    /// <summary>构建自定义分区添加表单（默认折叠）</summary>
    private Border BuildAddPartitionForm(out TextBox nameBox, out TextBox extBox, out TextBox patternBox)
    {
        nameBox = new TextBox
        {
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Background = Theme.InputBackground,
            BorderBrush = Theme.InputBorder,
            Foreground = Theme.TextPrimary,
            Padding = new Thickness(8, 6, 8, 6),
        };
        var nameRow = LabeledRow("分区名称", nameBox);

        extBox = new TextBox
        {
            FontSize = 12,
            FontFamily = Theme.MonoFont,
            Background = Theme.InputBackground,
            BorderBrush = Theme.InputBorder,
            Foreground = Theme.TextPrimary,
            Padding = new Thickness(8, 6, 8, 6),
            ToolTip = "多个扩展名用逗号或空格分隔，如 .png,.jpg",
        };
        var extRow = LabeledRow("扩展名", extBox);

        patternBox = new TextBox
        {
            FontSize = 12,
            FontFamily = Theme.MonoFont,
            Background = Theme.InputBackground,
            BorderBrush = Theme.InputBorder,
            Foreground = Theme.TextPrimary,
            Padding = new Thickness(8, 6, 8, 6),
            ToolTip = "可选，正则表达式匹配文件名，如 ^截图_\\d+",
        };
        var patternRow = LabeledRow("文件名正则（可选）", patternBox);

        // 颜色预设选择
        var colorPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var colorTitle = new TextBlock
        {
            Text = "颜色",
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        colorPanel.Children.Add(colorTitle);

        var presetColors = new[] { "#6c5ce7", "#3b82f6", "#10b981", "#f59e0b", "#ef4444", "#14b8a6", "#f97316", "#ec4899" };
        var swatchBorders = new List<Border>();
        foreach (var hex in presetColors)
        {
            var swatch = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(4),
                Background = ParseBrush(hex),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 6, 0),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
            };
            var hexCapture = hex;
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                _selectedPartitionColor = hexCapture;
                foreach (var b in swatchBorders)
                {
                    b.BorderBrush = Brushes.Transparent;
                }
                swatch.BorderBrush = Theme.TextPrimary;
            };
            swatchBorders.Add(swatch);
            colorPanel.Children.Add(swatch);
        }
        // 默认选中第一个
        swatchBorders[0].BorderBrush = Theme.TextPrimary;

        var addBtn = CreatePrimaryButton("添加分区");
        addBtn.Click += (_, _) => OnAddPartition();

        var form = new StackPanel();
        form.Children.Add(nameRow);
        form.Children.Add(extRow);
        form.Children.Add(patternRow);
        form.Children.Add(colorPanel);
        form.Children.Add(new Border
        {
            Child = addBtn,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        });

        return new Border
        {
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(12, 10, 12, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = form,
        };
    }

    /// <summary>添加分区处理</summary>
    private void OnAddPartition()
    {
        var name = _partitionNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            System.Windows.MessageBox.Show("请输入分区名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var exts = _partitionExtBox.Text
            .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        var pattern = string.IsNullOrWhiteSpace(_partitionPatternBox.Text) ? null : _partitionPatternBox.Text.Trim();

        try
        {
            _service.AddPartition(new PartitionConfig
            {
                Name = name,
                Color = _selectedPartitionColor,
                Extensions = exts,
                NamePattern = pattern,
            });

            // 清空表单
            _partitionNameBox.Text = string.Empty;
            _partitionExtBox.Text = string.Empty;
            _partitionPatternBox.Text = string.Empty;

            RenderPartitions();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"添加分区失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>渲染自定义分区列表</summary>
    private void RenderPartitions()
    {
        _partitionListPanel.Children.Clear();

        var partitions = _service.GetPartitions();
        if (partitions.Count == 0)
        {
            _partitionListPanel.Children.Add(new TextBlock
            {
                Text = "暂无自定义分区 — 点击「自定义分区」按钮添加",
                FontSize = 11,
                FontFamily = Theme.UiFont,
                Foreground = Theme.TextFaint,
                Margin = new Thickness(0, 4, 0, 8),
            });
            return;
        }

        foreach (var p in partitions)
        {
            _partitionListPanel.Children.Add(BuildPartitionRow(p));
        }
    }

    /// <summary>构建单个分区行 — 色块 + 名称 + 规则 + 删除按钮</summary>
    private Border BuildPartitionRow(PartitionConfig partition)
    {
        var dot = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(3),
            Background = ParseBrush(partition.Color),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var nameText = new TextBlock
        {
            Text = partition.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };

        var ruleParts = new List<string>();
        if (partition.Extensions.Count > 0)
            ruleParts.Add(string.Join(" ", partition.Extensions));
        if (!string.IsNullOrWhiteSpace(partition.NamePattern))
            ruleParts.Add($"正则:{partition.NamePattern}");
        var ruleText = new TextBlock
        {
            Text = ruleParts.Count > 0 ? string.Join(" | ", ruleParts) : "无规则",
            FontSize = 10,
            FontFamily = Theme.MonoFont,
            Foreground = Theme.TextFaint,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(10, 0, 0, 0),
        };

        var deleteBtn = new Button
        {
            Content = "删除",
            Height = 24,
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Background = Theme.InputBackground,
            Foreground = new SolidColorBrush(Theme.Error),
            BorderBrush = Theme.InputBorder,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var nameCapture = partition.Name;
        deleteBtn.Click += (_, _) =>
        {
            if (System.Windows.MessageBox.Show($"确认删除分区「{nameCapture}」？", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _service.RemovePartition(nameCapture);
                RenderPartitions();
            }
        };

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(deleteBtn, Dock.Right);
        row.Children.Add(deleteBtn);
        row.Children.Add(dot);
        row.Children.Add(nameText);
        row.Children.Add(ruleText);

        return new Border
        {
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = row,
        };
    }

    // ============================================================
    //  底部结果摘要
    // ============================================================

    /// <summary>构建底部结果摘要栏</summary>
    private Border BuildResultBar(out TextBlock resultText)
    {
        resultText = new TextBlock
        {
            Text = "尚未整理",
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var panel = new DockPanel
        {
            LastChildFill = true,
        };
        DockPanel.SetDock(resultText, Dock.Right);
        panel.Children.Add(resultText);
        panel.Children.Add(new TextBlock
        {
            Text = "整理结果",
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextFaint,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Border
        {
            Padding = new Thickness(16, 10, 16, 12),
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = Theme.HeaderBackground,
            Child = panel,
        };
    }

    /// <summary>更新整理结果摘要文本</summary>
    private void ShowResult(OrganizeResult result)
    {
        var msg = $"成功移动 {result.MovedCount} 个  |  跳过 {result.SkippedCount} 个  |  失败 {result.FailedCount} 个";
        _resultText.Text = msg;
        _resultText.Foreground = result.FailedCount > 0 ? new SolidColorBrush(Theme.Error) : Theme.SuccessBrush;
    }

    // ============================================================
    //  异步扫描与整理
    // ============================================================

    /// <summary>异步扫描桌面文件</summary>
    private void BeginScan()
    {
        if (_busy) return;
        _busy = true;
        _scanButton.IsEnabled = false;
        _organizeButton.IsEnabled = false;
        _resultText.Text = "正在扫描桌面…";
        _resultText.Foreground = Theme.TextFaint;

        Task.Run(() =>
        {
            List<ClassifiedFile> scanned;
            try
            {
                scanned = _service.ScanDesktop();
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _resultText.Text = $"扫描失败：{ex.Message}";
                    _resultText.Foreground = new SolidColorBrush(Theme.Error);
                    _busy = false;
                    _scanButton.IsEnabled = true;
                    _organizeButton.IsEnabled = true;
                }), DispatcherPriority.Normal);
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _files = scanned;
                RenderDistribution(_files);
                RenderCategoryList(_files);
                _resultText.Text = $"扫描完成 — 共 {_files.Count} 个文件";
                _resultText.Foreground = Theme.TextSecondary;
                _busy = false;
                _scanButton.IsEnabled = true;
                _organizeButton.IsEnabled = true;
            }), DispatcherPriority.Normal);
        });
    }

    /// <summary>一键整理 — 弹确认后异步执行</summary>
    private void OnOrganizeClicked()
    {
        if (_busy) return;
        if (_files.Count == 0)
        {
            System.Windows.MessageBox.Show("请先扫描桌面文件", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"即将把桌面 {_files.Count} 个文件按类别移入分类文件夹（文档/图片/视频/音乐/应用/压缩包/代码/其他）。\n同名文件将自动重命名，原文件不会删除。\n\n确认开始整理？",
            "一键整理确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        _busy = true;
        _scanButton.IsEnabled = false;
        _organizeButton.IsEnabled = false;
        _resultText.Text = "正在整理…";
        _resultText.Foreground = Theme.TextFaint;

        var snapshot = _files.ToList();
        Task.Run(() =>
        {
            OrganizeResult result;
            try
            {
                result = _service.OrganizeDesktop(snapshot);
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _resultText.Text = $"整理失败：{ex.Message}";
                    _resultText.Foreground = new SolidColorBrush(Theme.Error);
                    _busy = false;
                    _scanButton.IsEnabled = true;
                    _organizeButton.IsEnabled = true;
                }), DispatcherPriority.Normal);
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                ShowResult(result);
                _busy = false;
                _scanButton.IsEnabled = true;
                _organizeButton.IsEnabled = true;
                // 整理后重新扫描刷新列表
                BeginScan();
            }), DispatcherPriority.Normal);
        });
    }

    // ============================================================
    //  辅助方法
    // ============================================================

    /// <summary>创建分区标题（小标题）</summary>
    private static TextBlock BuildSectionHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(2, 12, 0, 8),
        };
    }

    /// <summary>构建带标签的输入行</summary>
    private static StackPanel LabeledRow(string label, TextBox box)
    {
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(lbl);
        panel.Children.Add(box);
        return panel;
    }

    /// <summary>格式化文件大小为人类可读字符串（B/KB/MB/GB）</summary>
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double size = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        var unit = -1;
        do
        {
            size /= 1024;
            unit++;
        } while (size >= 1024 && unit < units.Length - 1);
        return $"{size:F1} {units[unit]}";
    }

    /// <summary>解析十六进制颜色字符串为 Brush（容错：非法则回退到 MutedForeground）</summary>
    private static Brush ParseBrush(string hex)
    {
        try
        {
            var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            return new SolidColorBrush(color);
        }
        catch
        {
            return new SolidColorBrush(Theme.MutedForeground);
        }
    }

    /// <summary>在资源管理器中定位并选中指定文件</summary>
    private static void RevealInExplorer(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var psi = new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileClassify] 在资源管理器定位失败: {ex.Message}");
        }
    }
}
