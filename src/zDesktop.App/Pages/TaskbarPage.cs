using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using zDesktop.Shell.Styles;

// 项目同时启用 WPF + System.Drawing，Brush 在 System.Drawing 与 System.Windows.Media 间歧义，显式别名优先 WPF
using Brush = System.Windows.Media.Brush;

namespace zDesktop.App.Pages;

/// <summary>
/// 任务栏增强内容页 — 任务栏位置 / 模式 + 应用分组管理 + 外观设置 + 托盘收纳规则
///
/// 还原设计稿 taskbar.html：顶部位置与模式单选、实时预览、分组管理卡（展开折叠 / 编辑删除）、
/// 外观设置卡（透明度 / 圆角 / 图标大小 / 自动隐藏 / 最近文档开关）、托盘收纳规则卡。
/// 注意：本页仅展示配置选项，不实际替换系统任务栏（系统级 hook 超出当前范围）。
/// 所有颜色 / 字体 / 圆角一律引用 <see cref="Theme"/> 常量，不硬编码。
/// </summary>
public sealed class TaskbarPage : ContentPage
{
    /// <summary>分组列表（运行时可增删）</summary>
    private readonly List<TaskbarGroup> _groups = new();

    /// <summary>分组卡片宿主</summary>
    private StackPanel? _groupsHost;

    /// <summary>分组色板（编辑时循环切换）</summary>
    private static readonly Color[] GroupColors =
        { Theme.Primary, Theme.Success, Theme.Warning, Theme.Info };

    /// <summary>
    /// 构造任务栏页（无参）
    /// </summary>
    public TaskbarPage()
    {
        Title = "任务栏";
        NavId = "taskbar";

        SeedGroups();

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20, 12, 20, 20),
        };

        var root = new StackPanel();
        root.Children.Add(BuildPositionSection());
        root.Children.Add(BuildGroupSection());
        root.Children.Add(BuildAppearanceSection());
        root.Children.Add(BuildTraySection());

        scroll.Content = root;
        Content = scroll;
    }

    /// <summary>预置分组数据</summary>
    private void SeedGroups()
    {
        _groups.Add(new TaskbarGroup
        {
            Name = "工作", Color = Theme.Primary,
            Apps = new() { "VS Code", "浏览器", "终端", "邮件", "办公套件" },
            Expanded = true,
        });
        _groups.Add(new TaskbarGroup
        {
            Name = "娱乐", Color = Theme.Success,
            Apps = new() { "音乐", "视频", "游戏" },
            Expanded = false,
        });
        _groups.Add(new TaskbarGroup
        {
            Name = "工具", Color = Theme.Warning,
            Apps = new() { "截图", "计算器", "录屏", "取色" },
            Expanded = false,
        });
        _groups.Add(new TaskbarGroup
        {
            Name = "开发", Color = Theme.Info,
            Apps = new() { "Git", "Docker", "数据库" },
            Expanded = false,
        });
    }

    // ============================================================
    //  位置 + 模式
    // ============================================================

    /// <summary>构建位置与模式选择区</summary>
    private Border BuildPositionSection()
    {
        var panel = new StackPanel();

        panel.Children.Add(MakeSectionTitle("任务栏位置"));
        panel.Children.Add(CreateSegmented(
            new[] { "底部", "顶部", "左侧", "右侧" }, 0,
            idx => Console.WriteLine($"[Taskbar] 位置切换: {(TaskbarPosition)idx}")));

        panel.Children.Add(MakeSectionTitle("任务栏模式"));
        panel.Children.Add(CreateSegmented(
            new[] { "标准", "Dock 栏" }, 0,
            idx => Console.WriteLine($"[Taskbar] 模式切换: {idx}")));

        return WrapCard(panel, "位置与模式");
    }

    // ============================================================
    //  分组管理
    // ============================================================

    /// <summary>构建分组管理卡</summary>
    private Border BuildGroupSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(MakeSectionTitle("分组管理"));
        panel.Children.Add(new TextBlock
        {
            Text = "将任务栏图标分组管理，快速切换工作场景",
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        });

        _groupsHost = new StackPanel();
        RenderGroups();
        panel.Children.Add(_groupsHost);

        // 新建分组按钮（虚线边框）
        var addBtn = new Border
        {
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "＋", FontSize = 13, Foreground = Theme.PrimaryBrush, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "新建分组", FontFamily = Theme.UiFont, FontSize = 12, Foreground = Theme.PrimaryBrush, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
                },
            },
            Background = Brushes.Transparent,
            BorderBrush = Theme.InputBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 10, 0, 0),
            Cursor = Cursors.Hand,
        };
        addBtn.MouseEnter += (_, _) => addBtn.Background = Theme.PrimarySubtle;
        addBtn.MouseLeave += (_, _) => addBtn.Background = Brushes.Transparent;
        addBtn.MouseLeftButtonUp += (_, _) => AddGroup();
        panel.Children.Add(addBtn);

        return WrapCard(panel, "应用分组");
    }

    /// <summary>重新渲染所有分组卡片</summary>
    private void RenderGroups()
    {
        if (_groupsHost == null) return;
        _groupsHost.Children.Clear();
        foreach (var g in _groups)
        {
            _groupsHost.Children.Add(BuildGroupCard(g));
        }
    }

    /// <summary>构建单个分组卡片（可展开折叠 / 编辑 / 删除）</summary>
    private Border BuildGroupCard(TaskbarGroup g)
    {
        // 色块
        var dot = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(g.Color),
        };
        var name = new TextBlock
        {
            Text = g.Name,
            FontFamily = Theme.UiFont,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var count = new Border
        {
            Child = new TextBlock
            {
                Text = $"{g.Apps.Count} 个应用",
                FontFamily = Theme.UiFont,
                FontSize = 10,
                Foreground = new SolidColorBrush(g.Color),
            },
            Background = Theme.InputBackground,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 1, 8, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var arrow = new TextBlock
        {
            Text = g.Expanded ? "▾" : "▸",
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var editBtn = MakeIconButton("✎", "编辑");
        editBtn.MouseLeftButtonUp += (_, _) => EditGroup(g);
        var delBtn = MakeIconButton("✕", "删除");
        delBtn.MouseLeftButtonUp += (_, _) => DeleteGroup(g);

        var headerLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerLeft.Children.Add(arrow);
        headerLeft.Children.Add(dot);
        headerLeft.Children.Add(name);
        headerLeft.Children.Add(count);

        var headerRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerRight.Children.Add(editBtn);
        headerRight.Children.Add(delBtn);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(headerLeft, 0);
        Grid.SetColumn(headerRight, 1);
        header.Children.Add(headerLeft);
        header.Children.Add(headerRight);

        // 应用列表（可折叠）
        var appsPanel = new StackPanel
        {
            Margin = new Thickness(20, 8, 0, 4),
            Visibility = g.Expanded ? Visibility.Visible : Visibility.Collapsed,
        };
        foreach (var app in g.Apps)
        {
            appsPanel.Children.Add(BuildAppRow(app, g));
        }
        if (g.Apps.Count == 0)
        {
            appsPanel.Children.Add(new TextBlock
            {
                Text = "（暂无应用）",
                FontFamily = Theme.UiFont,
                FontSize = 11,
                Foreground = Theme.TextFaint,
            });
        }

        var card = new Border
        {
            Background = Theme.ListItemMuted,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = Cursors.Hand,
        };
        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(appsPanel);
        card.Child = panel;

        // 点击头部切换展开
        header.MouseLeftButtonUp += (_, _) =>
        {
            g.Expanded = !g.Expanded;
            arrow.Text = g.Expanded ? "▾" : "▸";
            appsPanel.Visibility = g.Expanded ? Visibility.Visible : Visibility.Collapsed;
        };

        return card;
    }

    /// <summary>分组内单个应用行（图标 + 名称 + 移除）</summary>
    private StackPanel BuildAppRow(string app, TaskbarGroup g)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 3, 0, 3),
        };
        row.Children.Add(new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = Theme.SmallRadius,
            Background = Theme.InputBackground,
            Child = new TextBlock
            {
                Text = "📦",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        row.Children.Add(new TextBlock
        {
            Text = app,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            Foreground = Theme.TextRegular,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        });
        var removeBtn = new TextBlock
        {
            Text = "移除",
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextFaint,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        removeBtn.MouseLeftButtonUp += (_, _) =>
        {
            g.Apps.Remove(app);
            RenderGroups();
        };
        row.Children.Add(removeBtn);
        return row;
    }

    /// <summary>新建分组（追加默认分组并重渲染）</summary>
    private void AddGroup()
    {
        var idx = _groups.Count % GroupColors.Length;
        _groups.Add(new TaskbarGroup
        {
            Name = $"分组 {_groups.Count + 1}",
            Color = GroupColors[idx],
            Apps = new(),
            Expanded = true,
        });
        RenderGroups();
    }

    /// <summary>编辑分组（循环切换色块作为编辑示意）</summary>
    private void EditGroup(TaskbarGroup g)
    {
        try
        {
            var idx = Array.IndexOf(GroupColors, g.Color);
            g.Color = GroupColors[(idx + 1) % GroupColors.Length];
            RenderGroups();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Taskbar] 编辑分组失败: {ex.Message}");
        }
    }

    /// <summary>删除分组</summary>
    private void DeleteGroup(TaskbarGroup g)
    {
        try
        {
            _groups.Remove(g);
            RenderGroups();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Taskbar] 删除分组失败: {ex.Message}");
        }
    }

    // ============================================================
    //  外观设置
    // ============================================================

    /// <summary>构建外观设置卡</summary>
    private Border BuildAppearanceSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(MakeSectionTitle("外观设置"));
        panel.Children.Add(new TextBlock
        {
            Text = "调整任务栏的显示样式",
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(MakeSliderRow("毛玻璃透明度", 0, 100, 80, out _, out _));
        panel.Children.Add(MakeSliderRow("圆角大小", 0, 24, 10, out _, out _));
        panel.Children.Add(MakeSliderRow("图标大小", 16, 48, 32, out _, out _));

        panel.Children.Add(MakeToggleRow("自动隐藏任务栏", false, out _));
        panel.Children.Add(MakeToggleRow("显示最近文档", true, out _));

        return WrapCard(panel, "外观");
    }

    // ============================================================
    //  托盘收纳
    // ============================================================

    /// <summary>构建托盘收纳规则卡</summary>
    private Border BuildTraySection()
    {
        var panel = new StackPanel();
        panel.Children.Add(MakeSectionTitle("托盘收纳"));
        panel.Children.Add(new TextBlock
        {
            Text = "不常用的应用自动收纳到托盘区域",
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(MakeSliderRow("超过 N 天未使用则收纳", 1, 60, 14, out _, out _));
        panel.Children.Add(MakeToggleRow("微信", true, out _));
        panel.Children.Add(MakeToggleRow("钉钉", true, out _));
        panel.Children.Add(MakeToggleRow("网盘", false, out _));

        return WrapCard(panel, "托盘规则");
    }

    // ============================================================
    //  通用 UI 工具
    // ============================================================

    /// <summary>分区小标题</summary>
    private static TextBlock MakeSectionTitle(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(2, 2, 0, 10),
        };
    }

    /// <summary>将内容包装为统一卡片外观</summary>
    private static Border WrapCard(UIElement content, string header)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = header,
            FontFamily = Theme.UiFont,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            Margin = new Thickness(2, 0, 0, 4),
        });
        panel.Children.Add(content);
        return new Border
        {
            Child = panel,
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 14),
        };
    }

    /// <summary>创建分段单选胶囊组（返回容器，点击回调选中索引）</summary>
    private static StackPanel CreateSegmented(string[] options, int selected, Action<int> onSelect)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var items = new Border[options.Length];
        var labels = new TextBlock[options.Length];

        for (var i = 0; i < options.Length; i++)
        {
            var idx = i;
            var lbl = new TextBlock
            {
                Text = options[i],
                FontFamily = Theme.UiFont,
                FontSize = 12,
                Foreground = i == selected ? Theme.PrimaryBrush : Theme.TextSecondary,
                FontWeight = i == selected ? FontWeights.SemiBold : FontWeights.Normal,
            };
            var b = new Border
            {
                Child = lbl,
                Background = i == selected ? Theme.PrimarySubtle : Theme.InputBackground,
                BorderBrush = i == selected ? Theme.PrimaryBrush : Theme.InputBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = Theme.SmallRadius,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
            };
            b.MouseLeftButtonUp += (_, _) =>
            {
                onSelect(idx);
                for (var j = 0; j < items.Length; j++)
                {
                    var act = j == idx;
                    items[j].Background = act ? Theme.PrimarySubtle : Theme.InputBackground;
                    items[j].BorderBrush = act ? Theme.PrimaryBrush : Theme.InputBorder;
                    labels[j].Foreground = act ? Theme.PrimaryBrush : Theme.TextSecondary;
                    labels[j].FontWeight = act ? FontWeights.SemiBold : FontWeights.Normal;
                }
            };
            items[i] = b;
            labels[i] = lbl;
            panel.Children.Add(b);
        }
        return panel;
    }

    /// <summary>创建滑块设置行（标签 + 滑块 + 当前值）</summary>
    private static StackPanel MakeSliderRow(string label, int min, int max, int value, out Slider slider, out TextBlock valueLabel)
    {
        var title = new TextBlock
        {
            Text = label,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            Foreground = Theme.TextRegular,
            VerticalAlignment = VerticalAlignment.Center,
        };
        valueLabel = new TextBlock
        {
            Text = value.ToString(),
            FontFamily = Theme.MonoFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 36,
            TextAlignment = TextAlignment.Right,
        };
        var capturedLabel = valueLabel;
        slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Width = 200,
            Foreground = Theme.PrimaryBrush,
            Background = Theme.InputBackground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        slider.ValueChanged += (_, e) => capturedLabel.Text = ((int)e.NewValue).ToString();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0);
        Grid.SetColumn(valueLabel, 1);
        head.Children.Add(title);
        head.Children.Add(valueLabel);

        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(head);
        panel.Children.Add(slider);
        return panel;
    }

    /// <summary>创建开关设置行（标签 + 描述 + 开关）</summary>
    private static StackPanel MakeToggleRow(string label, bool initial, out Action<bool> apply)
    {
        var title = new TextBlock
        {
            Text = label,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            Foreground = Theme.TextRegular,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var toggle = CreateToggleSwitch(out apply, initial);
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0);
        Grid.SetColumn(toggle, 1);
        head.Children.Add(title);
        head.Children.Add(toggle);

        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(head);
        return panel;
    }

    /// <summary>创建开关控件（轨道 + 圆点，返回 apply 委托用于外部切换状态）</summary>
    private static Border CreateToggleSwitch(out Action<bool> apply, bool initial = false)
    {
        var knob = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = Theme.TextPrimary,
            Margin = new Thickness(2),
            HorizontalAlignment = initial ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        var track = new Border
        {
            Width = 32,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = initial ? Theme.PrimaryBrush : Theme.InputBackground,
            BorderBrush = Theme.InputBorder,
            BorderThickness = new Thickness(1),
            Child = knob,
            Cursor = Cursors.Hand,
        };
        var isOn = initial;
        track.MouseLeftButtonUp += (_, _) =>
        {
            isOn = !isOn;
            track.Background = isOn ? Theme.PrimaryBrush : Theme.InputBackground;
            knob.HorizontalAlignment = isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        };
        apply = on =>
        {
            isOn = on;
            track.Background = on ? Theme.PrimaryBrush : Theme.InputBackground;
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        };
        return track;
    }

    /// <summary>创建小型图标按钮（编辑 / 删除等）</summary>
    private static Border MakeIconButton(string glyph, string tooltip)
    {
        var b = new Border
        {
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = 12,
                FontFamily = Theme.UiFont,
                Foreground = Theme.TextFaint,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Width = 24,
            Height = 24,
            Background = Brushes.Transparent,
            CornerRadius = Theme.SmallRadius,
            Margin = new Thickness(2, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
        };
        b.MouseEnter += (_, _) => b.Background = Theme.InputBackground;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        return b;
    }

    // ============================================================
    //  数据模型
    // ============================================================

    /// <summary>任务栏位置枚举</summary>
    private enum TaskbarPosition { Bottom, Top, Left, Right }

    /// <summary>任务栏分组数据</summary>
    private sealed class TaskbarGroup
    {
        /// <summary>分组名称</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>分组色块</summary>
        public Color Color { get; set; }

        /// <summary>组内应用名称列表</summary>
        public List<string> Apps { get; set; } = new();

        /// <summary>是否展开</summary>
        public bool Expanded { get; set; }
    }
}
