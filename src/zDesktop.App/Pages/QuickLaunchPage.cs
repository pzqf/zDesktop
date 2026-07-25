using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using zDesktop.Shell.Styles;

// 项目同时启用 WPF + System.Drawing，Brush 在 System.Drawing 与 System.Windows.Media 间歧义，显式别名优先 WPF
using Brush = System.Windows.Media.Brush;

namespace zDesktop.App.Pages;

/// <summary>
/// 快速启动器配置内容页 — 全局热键 + 快捷命令列表 + 悬浮球设置 + 即时计算预览
///
/// 还原设计稿 quick-launch.html 的配置视图：顶部全局热键显示与测试呼出按钮，
/// 中部快捷命令列表（命令名 + 触发关键词 + 执行动作 + 启用开关 + 编辑 / 删除），
/// 中下部悬浮球设置（启用开关 / 位置 / 大小 / 透明度），
/// 底部即时计算预览（输入算式实时显示结果）。
/// 所有颜色 / 字体 / 圆角一律引用 <see cref="Theme"/> 常量，不硬编码。
/// </summary>
public sealed class QuickLaunchPage : ContentPage
{
    /// <summary>快捷命令列表（运行时可增删）</summary>
    private readonly List<QuickCommand> _commands = new();

    /// <summary>命令列表宿主</summary>
    private StackPanel? _commandHost;

    /// <summary>即时计算输入框</summary>
    private TextBox? _calcInput;

    /// <summary>即时计算结果文本</summary>
    private TextBlock? _calcResult;

    /// <summary>
    /// 构造快速启动器页（无参）
    /// </summary>
    public QuickLaunchPage()
    {
        Title = "快速启动器";
        NavId = "quick-launch";

        SeedCommands();

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20, 12, 20, 20),
        };

        var root = new StackPanel();
        root.Children.Add(BuildHotkeySection());
        root.Children.Add(BuildCommandSection());
        root.Children.Add(BuildFloatBallSection());
        root.Children.Add(BuildCalcPreview());

        scroll.Content = root;
        Content = scroll;
    }

    /// <summary>预置内置命令</summary>
    private void SeedCommands()
    {
        _commands.Add(new QuickCommand
        {
            Name = "计算器", Keyword = "calc", Action = "启动 Windows 计算器",
            Icon = "🧮", Enabled = true,
        });
        _commands.Add(new QuickCommand
        {
            Name = "翻译", Keyword = "tr", Action = "翻译选中文本（英→中）",
            Icon = "🌐", Enabled = true,
        });
        _commands.Add(new QuickCommand
        {
            Name = "打开浏览器", Keyword = "web", Action = "启动默认浏览器",
            Icon = "🔗", Enabled = true,
        });
        _commands.Add(new QuickCommand
        {
            Name = "截图", Keyword = "shot", Action = "区域截图到剪贴板",
            Icon = "📷", Enabled = false,
        });
        _commands.Add(new QuickCommand
        {
            Name = "终端", Keyword = "term", Action = "启动 Windows 终端",
            Icon = "⌨", Enabled = true,
        });
    }

    // ============================================================
    //  顶部：全局热键
    // ============================================================

    /// <summary>构建顶部全局热键区</summary>
    private Border BuildHotkeySection()
    {
        var kbd = new Border
        {
            Child = new TextBlock
            {
                Text = "Alt + Space",
                FontFamily = Theme.MonoFont,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.PrimaryBrush,
            },
            Background = Theme.PrimarySubtle,
            BorderBrush = Theme.PrimaryBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.SmallRadius,
            Padding = new Thickness(12, 6, 12, 6),
        };

        var testBtn = CreateSecondaryButton("测试呼出");
        testBtn.Click += (_, _) => Console.WriteLine("[QuickLaunch] 测试呼出快速启动器");

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(kbd);
        row.Children.Add(new TextBlock
        {
            Text = "全局热键 — 在任意位置按下可呼出快速启动器",
            FontFamily = Theme.UiFont,
            FontSize = 12,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        });
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(testBtn);

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(row, 0);
        Grid.SetColumn(right, 1);
        head.Children.Add(row);
        head.Children.Add(right);

        var panel = new StackPanel();
        panel.Children.Add(MakeSectionTitle("全局热键"));
        panel.Children.Add(head);

        return WrapCard(panel);
    }

    // ============================================================
    //  中部：快捷命令列表
    // ============================================================

    /// <summary>构建快捷命令列表区</summary>
    private Border BuildCommandSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(MakeSectionTitle("快捷命令"));
        panel.Children.Add(new TextBlock
        {
            Text = "输入关键词快速执行命令，支持自定义命令与内置命令",
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        });

        // 表头
        panel.Children.Add(BuildCommandHeader());

        _commandHost = new StackPanel();
        RenderCommands();
        panel.Children.Add(_commandHost);

        // 新建命令按钮
        var addBtn = new Border
        {
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "＋", FontSize = 13, Foreground = Theme.PrimaryBrush, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "新建命令", FontFamily = Theme.UiFont, FontSize = 12, Foreground = Theme.PrimaryBrush, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
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
        addBtn.MouseLeftButtonUp += (_, _) => AddCommand();
        panel.Children.Add(addBtn);

        return WrapCard(panel);
    }

    /// <summary>构建命令列表表头</summary>
    private static Border BuildCommandHeader()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40, GridUnitType.Pixel) });   // 图标
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });      // 名称
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80, GridUnitType.Pixel) });    // 关键词
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });      // 动作
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50, GridUnitType.Pixel) });    // 启用
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60, GridUnitType.Pixel) });    // 操作

        var headers = new[] { "", "命令名", "关键词", "执行动作", "启用", "操作" };
        for (var i = 0; i < headers.Length; i++)
        {
            var tb = new TextBlock
            {
                Text = headers[i],
                FontFamily = Theme.UiFont,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextFaint,
            };
            Grid.SetColumn(tb, i);
            grid.Children.Add(tb);
        }

        return new Border
        {
            Child = grid,
            Background = Theme.ListItemMuted,
            CornerRadius = Theme.SmallRadius,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 4),
        };
    }

    /// <summary>重新渲染所有命令行</summary>
    private void RenderCommands()
    {
        if (_commandHost == null) return;
        _commandHost.Children.Clear();
        foreach (var cmd in _commands)
        {
            _commandHost.Children.Add(BuildCommandRow(cmd));
        }
    }

    /// <summary>构建单个命令行</summary>
    private Border BuildCommandRow(QuickCommand cmd)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60, GridUnitType.Pixel) });

        // 图标
        var icon = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = Theme.SmallRadius,
            Background = Theme.InputBackground,
            Child = new TextBlock
            {
                Text = cmd.Icon,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        // 名称
        var name = new TextBlock
        {
            Text = cmd.Name,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        // 关键词
        var keyword = new Border
        {
            Child = new TextBlock
            {
                Text = cmd.Keyword,
                FontFamily = Theme.MonoFont,
                FontSize = 11,
                Foreground = Theme.PrimaryBrush,
            },
            Background = Theme.PrimarySubtle,
            CornerRadius = Theme.SmallRadius,
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(keyword, 2);
        grid.Children.Add(keyword);

        // 动作
        var action = new TextBlock
        {
            Text = cmd.Action,
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(action, 3);
        grid.Children.Add(action);

        // 启用开关
        var toggle = MakeMiniToggle(out var applyToggle, cmd.Enabled);
        toggle.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(toggle, 4);
        grid.Children.Add(toggle);

        // 操作按钮
        var opPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var editBtn = MakeIconButton("✎", "编辑");
        editBtn.MouseLeftButtonUp += (_, _) => EditCommand(cmd);
        var delBtn = MakeIconButton("✕", "删除");
        delBtn.MouseLeftButtonUp += (_, _) => DeleteCommand(cmd);
        opPanel.Children.Add(editBtn);
        opPanel.Children.Add(delBtn);
        Grid.SetColumn(opPanel, 5);
        grid.Children.Add(opPanel);

        return new Border
        {
            Child = grid,
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 0),
        };
    }

    /// <summary>新建命令</summary>
    private void AddCommand()
    {
        try
        {
            _commands.Add(new QuickCommand
            {
                Name = $"新命令 {_commands.Count + 1}",
                Keyword = $"cmd{_commands.Count + 1}",
                Action = "自定义动作",
                Icon = "⚡",
                Enabled = true,
            });
            RenderCommands();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QuickLaunch] 新建命令失败: {ex.Message}");
        }
    }

    /// <summary>编辑命令（循环切换图标作为编辑示意）</summary>
    private void EditCommand(QuickCommand cmd)
    {
        try
        {
            var icons = new[] { "🧮", "🌐", "🔗", "📷", "⌨", "⚡", "📂", "🎵" };
            var idx = Array.IndexOf(icons, cmd.Icon);
            cmd.Icon = icons[(idx + 1) % icons.Length];
            RenderCommands();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QuickLaunch] 编辑命令失败: {ex.Message}");
        }
    }

    /// <summary>删除命令</summary>
    private void DeleteCommand(QuickCommand cmd)
    {
        try
        {
            _commands.Remove(cmd);
            RenderCommands();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QuickLaunch] 删除命令失败: {ex.Message}");
        }
    }

    // ============================================================
    //  中下部：悬浮球设置
    // ============================================================

    /// <summary>构建悬浮球设置区</summary>
    private Border BuildFloatBallSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(MakeSectionTitle("悬浮球设置"));
        panel.Children.Add(new TextBlock
        {
            Text = "桌面常驻悬浮球，点击快速呼出启动器",
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(MakeSettingRow(
            "启用悬浮球", "在桌面显示常驻悬浮球",
            MakeToggle(out _)));

        // 位置单选
        panel.Children.Add(MakeSubLabel("悬浮球位置"));
        panel.Children.Add(MakeSegmented(
            new[] { "左上", "右上", "左下", "右下" }, 3,
            idx => Console.WriteLine($"[QuickLaunch] 悬浮球位置: {idx}")));

        panel.Children.Add(MakeSliderRow("悬浮球大小", 36, 80, 48, "px"));
        panel.Children.Add(MakeSliderRow("悬浮球透明度", 30, 100, 80, "%"));

        return WrapCard(panel);
    }

    // ============================================================
    //  底部：即时计算预览
    // ============================================================

    /// <summary>构建即时计算预览区</summary>
    private Border BuildCalcPreview()
    {
        _calcInput = new TextBox
        {
            FontFamily = Theme.MonoFont,
            FontSize = 18,
            Background = Theme.InputBackground,
            Foreground = Theme.TextPrimary,
            BorderBrush = Theme.InputBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            CaretBrush = Theme.PrimaryBrush,
        };
        _calcInput.TextChanged += (_, _) => UpdateCalcResult();

        _calcResult = new TextBlock
        {
            Text = "= —",
            FontFamily = Theme.MonoFont,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.PrimaryBrush,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var panel = new StackPanel();
        panel.Children.Add(MakeSectionTitle("即时计算预览"));
        panel.Children.Add(new TextBlock
        {
            Text = "输入算式实时计算结果（支持 + - * / % 与括号）",
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(_calcInput);
        panel.Children.Add(_calcResult);

        // 预填示例
        _calcInput.Text = "12*8+45";

        return WrapCard(panel);
    }

    /// <summary>根据输入框内容实时计算结果</summary>
    private void UpdateCalcResult()
    {
        if (_calcInput == null || _calcResult == null) return;
        try
        {
            var expr = _calcInput.Text.Trim();
            if (string.IsNullOrEmpty(expr))
            {
                _calcResult.Text = "= —";
                _calcResult.Foreground = Theme.TextFaint;
                return;
            }

            var result = EvaluateExpression(expr);
            _calcResult.Text = $"= {result}";
            _calcResult.Foreground = Theme.PrimaryBrush;
        }
        catch
        {
            _calcResult.Text = "= 表达式无效";
            _calcResult.Foreground = new SolidColorBrush(Theme.Error);
        }
    }

    /// <summary>安全计算算术表达式（仅支持数字与 + - * / % () ）</summary>
    private static string EvaluateExpression(string expr)
    {
        // 仅允许数字、运算符、括号、小数点、空格
        foreach (var c in expr)
        {
            if (!char.IsDigit(c) && c is not ('+' or '-' or '*' or '/' or '%' or '(' or ')' or '.' or ' '))
                throw new InvalidOperationException("包含非法字符");
        }

        var result = new DataTable().Compute(expr, null);
        if (result == null || result == DBNull.Value)
            throw new InvalidOperationException("计算无结果");

        // 整数结果不显示小数部分
        var d = Convert.ToDouble(result);
        return Math.Abs(d - Math.Round(d)) < 0.0000001
            ? Math.Round(d).ToString()
            : d.ToString("F4").TrimEnd('0').TrimEnd('.');
    }

    // ============================================================
    //  通用 UI 工具
    // ============================================================

    /// <summary>分区大标题</summary>
    private static TextBlock MakeSectionTitle(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = Theme.UiFont,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            Margin = new Thickness(2, 0, 0, 8),
        };
    }

    /// <summary>分区小标题</summary>
    private static TextBlock MakeSubLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = Theme.UiFont,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(2, 6, 0, 6),
        };
    }

    /// <summary>将内容包装为统一卡片外观</summary>
    private static Border WrapCard(UIElement content)
    {
        return new Border
        {
            Child = content,
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 14),
        };
    }

    /// <summary>创建设置行（标签 + 描述 + 控件）</summary>
    private static StackPanel MakeSettingRow(string label, string desc, UIElement control)
    {
        var title = new TextBlock
        {
            Text = label,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
        };
        var descTb = new TextBlock
        {
            Text = desc,
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        var left = new StackPanel();
        left.Children.Add(title);
        left.Children.Add(descTb);

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(control, 1);
        head.Children.Add(left);
        head.Children.Add(control);

        return new StackPanel
        {
            Children = { head },
            Margin = new Thickness(0, 6, 0, 6),
        };
    }

    /// <summary>创建分段单选胶囊组</summary>
    private static StackPanel MakeSegmented(string[] options, int selected, Action<int> onSelect)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 8),
        };
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

    /// <summary>创建滑块设置行（标签 + 滑块 + 当前值 + 单位）</summary>
    private static StackPanel MakeSliderRow(string label, int min, int max, int value, string unit)
    {
        var title = new TextBlock
        {
            Text = label,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var valueLabel = new TextBlock
        {
            Text = $"{value} {unit}",
            FontFamily = Theme.MonoFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 60,
            TextAlignment = TextAlignment.Right,
        };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Width = 220,
            Foreground = Theme.PrimaryBrush,
            Background = Theme.InputBackground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        slider.ValueChanged += (_, e) => valueLabel.Text = $"{(int)e.NewValue} {unit}";

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

    /// <summary>创建标准开关控件</summary>
    private static Border MakeToggle(out Action<bool> apply, bool initial = false)
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

    /// <summary>创建迷你开关控件（用于表格行内）</summary>
    private static Border MakeMiniToggle(out Action<bool> apply, bool initial = false)
    {
        var knob = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = Theme.TextPrimary,
            Margin = new Thickness(2),
            HorizontalAlignment = initial ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        var track = new Border
        {
            Width = 26,
            Height = 16,
            CornerRadius = new CornerRadius(8),
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

    /// <summary>创建小型图标按钮</summary>
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

    /// <summary>快捷命令数据</summary>
    private sealed class QuickCommand
    {
        /// <summary>命令名称</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>触发关键词</summary>
        public string Keyword { get; set; } = string.Empty;

        /// <summary>执行动作描述</summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>图标字符</summary>
        public string Icon { get; set; } = string.Empty;

        /// <summary>是否启用</summary>
        public bool Enabled { get; set; }
    }
}
