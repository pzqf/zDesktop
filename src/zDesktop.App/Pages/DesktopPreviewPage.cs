using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using zDesktop.Shell.Styles;
using zDesktop.Shell.Wallpaper;

// 项目同时启用 WPF + System.Drawing，Brush / Point 在 System.Drawing 与 System.Windows 间歧义，显式别名优先 WPF
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using TickPlacement = System.Windows.Controls.Primitives.TickPlacement;

namespace zDesktop.App.Pages;

/// <summary>
/// 桌面效果图内容页 — 全屏可视化预览 zDesktop 启用后的桌面效果
///
/// 还原设计稿 desktop-preview.html：上方操作栏（刷新预览 / 应用到桌面 / 缩放滑块）+
/// 主体预览舞台（壁纸背景 + 桌面图标分组 + 右侧组件卡片 + 底部任务栏示意）。
/// 壁纸背景优先读取注册表中当前桌面壁纸路径，加载失败回退到 Theme 背景色 + 品牌色渐变。
/// 所有颜色 / 字体 / 圆角一律引用 <see cref="Theme"/> 常量，不硬编码。
/// </summary>
public sealed class DesktopPreviewPage : ContentPage
{
    /// <summary>壁纸服务（用于「应用到桌面」时写入壁纸）</summary>
    private readonly WallpaperService _wallpaper = new();

    /// <summary>预览舞台（壁纸背景 + 桌面元素分层）</summary>
    private Border? _stage;

    /// <summary>缩放比例文本</summary>
    private TextBlock? _scaleLabel;

    /// <summary>当前解析到的壁纸路径（可能为 null）</summary>
    private string? _wallpaperPath;

    /// <summary>
    /// 构造桌面效果图页
    /// </summary>
    public DesktopPreviewPage()
    {
        Title = "桌面效果图";
        NavId = "desktop-preview";

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20, 12, 20, 20),
        };

        var root = new StackPanel();
        root.Children.Add(BuildActionBar());
        root.Children.Add(BuildPreviewStage());
        root.Children.Add(BuildLegend());

        scroll.Content = root;
        Content = scroll;
    }

    // ============================================================
    //  操作栏
    // ============================================================

    /// <summary>构建顶部操作栏：刷新预览 + 应用到桌面 + 缩放滑块</summary>
    private Border BuildActionBar()
    {
        var refresh = CreateSecondaryButton("刷新预览");
        refresh.Click += (_, _) => RefreshPreview();

        var apply = CreatePrimaryButton("应用到桌面");
        apply.Click += (_, _) => ApplyToDesktop();

        var scaleTitle = new TextBlock
        {
            Text = "缩放",
            FontFamily = Theme.UiFont,
            FontSize = 12,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        var slider = new Slider
        {
            Minimum = 60,
            Maximum = 100,
            Value = 100,
            Width = 140,
            TickFrequency = 10,
            TickPlacement = TickPlacement.BottomRight,
            Foreground = Theme.PrimaryBrush,
            Background = Theme.InputBackground,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _scaleLabel = new TextBlock
        {
            Text = "100%",
            FontFamily = Theme.MonoFont,
            FontSize = 11,
            Foreground = Theme.TextRegular,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 40,
        };

        slider.ValueChanged += (_, e) =>
        {
            var scale = e.NewValue / 100.0;
            if (_stage != null)
            {
                _stage.LayoutTransform = new ScaleTransform(scale, scale);
            }
            if (_scaleLabel != null)
            {
                _scaleLabel.Text = $"{e.NewValue:F0}%";
            }
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(refresh);
        panel.Children.Add(apply);
        apply.Margin = new Thickness(8, 0, 0, 0);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(scaleTitle);
        right.Children.Add(slider);
        right.Children.Add(_scaleLabel);

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(panel);

        return new Border
        {
            Child = dock,
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 14),
        };
    }

    // ============================================================
    //  预览舞台
    // ============================================================

    /// <summary>构建预览舞台（壁纸 + 图标 + 组件 + 任务栏）</summary>
    private Border BuildPreviewStage()
    {
        _stage = new Border
        {
            Height = 440,
            CornerRadius = Theme.ControlRadius,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Background = ResolveWallpaperBackground(),
        };

        var grid = new Grid();

        // 顶部右侧搜索胶囊
        var search = BuildSearchPill();
        search.HorizontalAlignment = HorizontalAlignment.Right;
        search.VerticalAlignment = VerticalAlignment.Top;
        search.Margin = new Thickness(0, 12, 12, 0);
        grid.Children.Add(search);

        // 左侧桌面图标分组
        var icons = BuildDesktopIcons();
        icons.HorizontalAlignment = HorizontalAlignment.Left;
        icons.VerticalAlignment = VerticalAlignment.Top;
        icons.Margin = new Thickness(12, 12, 0, 0);
        grid.Children.Add(icons);

        // 右侧组件卡片列
        var widgets = BuildWidgetColumn();
        widgets.HorizontalAlignment = HorizontalAlignment.Right;
        widgets.VerticalAlignment = VerticalAlignment.Top;
        widgets.Margin = new Thickness(0, 48, 12, 0);
        grid.Children.Add(widgets);

        // 右下版本提示
        var hint = BuildVersionHint();
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        hint.VerticalAlignment = VerticalAlignment.Bottom;
        hint.Margin = new Thickness(0, 0, 12, 52);
        grid.Children.Add(hint);

        // 底部任务栏
        var taskbar = BuildTaskbarMock();
        taskbar.HorizontalAlignment = HorizontalAlignment.Stretch;
        taskbar.VerticalAlignment = VerticalAlignment.Bottom;
        grid.Children.Add(taskbar);

        _stage.Child = grid;
        return _stage;
    }

    /// <summary>解析壁纸背景：优先当前桌面壁纸，回退渐变</summary>
    private Brush ResolveWallpaperBackground()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var path = key?.GetValue("Wallpaper") as string;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                _wallpaperPath = path;
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(path, UriKind.Absolute);
                img.EndInit();
                return new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            }
        }
        catch
        {
            // 读取失败回退渐变
        }
        return MakeBrandGradient();
    }

    /// <summary>品牌渐变背景（壁纸不可用时回退）</summary>
    private static Brush MakeBrandGradient()
    {
        var g = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new(Theme.Background, 0.0),
                new(Theme.Muted, 0.55),
                new(Theme.Primary, 1.0),
            },
        };
        return g;
    }

    /// <summary>顶部右侧搜索胶囊</summary>
    private Border BuildSearchPill()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(new TextBlock
        {
            Text = "🔍",
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "搜索…",
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextFaint,
            Margin = new Thickness(6, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var kbd = new Border
        {
            Background = Theme.Divider,
            CornerRadius = Theme.SmallRadius,
            Padding = new Thickness(4, 1, 4, 1),
            Child = new TextBlock
            {
                Text = "Ctrl+Space",
                FontFamily = Theme.MonoFont,
                FontSize = 9,
                Foreground = Theme.TextSecondary,
            },
        };
        panel.Children.Add(kbd);

        return new Border
        {
            Child = panel,
            Background = Theme.ContainerBackground,
            BorderBrush = Theme.ContainerBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(8, 4, 8, 4),
            Width = 200,
        };
    }

    /// <summary>左侧桌面图标分组（快捷方式 / 文档 / 工具）</summary>
    private StackPanel BuildDesktopIcons()
    {
        var col = new StackPanel { Width = 200 };

        col.Children.Add(BuildIconGroup("快捷方式", new[]
        {
            ("🖥", "此电脑", true),
            ("🗑", "回收站", false),
        }));
        col.Children.Add(BuildIconGroup("文档", new[]
        {
            ("📁", "项目源码", false),
            ("📁", "设计素材", false),
            ("📁", "备份文件", false),
        }));
        col.Children.Add(BuildIconGroup("工具", new[]
        {
            ("</>", "VS Code", false),
            ("🌐", "浏览器", false),
        }));

        return col;
    }

    /// <summary>构建单个图标分组（小标题 + 图标列）</summary>
    private StackPanel BuildIconGroup(string title, IReadOnlyList<(string Glyph, string Name, bool Selected)> icons)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = Theme.UiFont,
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(2, 0, 0, 4),
        });

        var row = new WrapPanel { ItemWidth = 64, ItemHeight = 60 };
        foreach (var (glyph, name, selected) in icons)
        {
            row.Children.Add(BuildDesktopIcon(glyph, name, selected));
        }
        panel.Children.Add(row);
        return panel;
    }

    /// <summary>单个桌面图标（图标 + 标签，可选选中态）</summary>
    private Border BuildDesktopIcon(string glyph, string name, bool selected)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontSize = 20,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextPrimary,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = name,
            FontFamily = Theme.UiFont,
            FontSize = 9,
            Foreground = Theme.TextPrimary,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(icon);
        stack.Children.Add(label);

        return new Border
        {
            Child = stack,
            Background = selected ? Theme.PrimarySubtle : Brushes.Transparent,
            BorderBrush = selected ? Theme.PrimaryBrush : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.SmallRadius,
            Padding = new Thickness(3, 4, 3, 4),
            Margin = new Thickness(1),
            Cursor = Cursors.Hand,
        };
    }

    /// <summary>右侧组件卡片列（时钟 / 日历 / 便签 / 系统监控）</summary>
    private StackPanel BuildWidgetColumn()
    {
        var col = new StackPanel { Width = 210 };

        // 时钟卡
        var clock = new StackPanel();
        clock.Children.Add(new TextBlock
        {
            Text = DateTime.Now.ToString("HH:mm:ss"),
            FontFamily = Theme.MonoFont,
            FontSize = 28,
            FontWeight = FontWeights.Light,
            Foreground = Theme.TextPrimary,
        });
        clock.Children.Add(new TextBlock
        {
            Text = DateTime.Now.ToString("M月d日 ddd"),
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 2, 0, 0),
        });
        clock.Children.Add(new TextBlock
        {
            Text = "多云 · 28° · 上海",
            FontFamily = Theme.UiFont,
            FontSize = 10,
            Foreground = Theme.TextFaint,
            Margin = new Thickness(0, 4, 0, 0),
        });
        col.Children.Add(MakeWidgetCard(clock));

        // 日历卡（当前周示意）
        var cal = new StackPanel();
        cal.Children.Add(new TextBlock
        {
            Text = DateTime.Now.ToString("M月 yyyy"),
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextRegular,
            Margin = new Thickness(0, 0, 0, 6),
        });
        cal.Children.Add(MakeCalendarWeekRow());
        col.Children.Add(MakeWidgetCard(cal));

        // 便签卡
        var notes = new StackPanel();
        notes.Children.Add(new TextBlock
        {
            Text = "便签",
            FontFamily = Theme.UiFont,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 6),
        });
        notes.Children.Add(MakeNote("周五提交 v2.0", new SolidColorBrush(Theme.Warning)));
        notes.Children.Add(MakeNote("记得备份设计稿", Theme.PrimaryBrush));
        col.Children.Add(MakeWidgetCard(notes));

        // 系统监控卡
        var mon = new StackPanel();
        mon.Children.Add(new TextBlock
        {
            Text = "系统监控",
            FontFamily = Theme.UiFont,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 6),
        });
        mon.Children.Add(MakeMonitorRow("CPU", 32, Theme.Success));
        mon.Children.Add(MakeMonitorRow("内存", 67, Theme.Warning));
        mon.Children.Add(MakeMonitorRow("磁盘", 62, Theme.Primary));
        col.Children.Add(MakeWidgetCard(mon));

        return col;
    }

    /// <summary>当前周示意行（高亮今日）</summary>
    private StackPanel MakeCalendarWeekRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var today = DateTime.Now.Day;
        for (var i = -3; i <= 3; i++)
        {
            var d = DateTime.Now.AddDays(i);
            var isToday = d.Day == today;
            var cell = new Border
            {
                Width = 24,
                Height = 22,
                CornerRadius = Theme.SmallRadius,
                Background = isToday ? Theme.PrimaryBrush : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = d.Day.ToString(),
                    FontFamily = Theme.MonoFont,
                    FontSize = 10,
                    Foreground = isToday ? Theme.TextPrimary : Theme.TextSecondary,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            row.Children.Add(cell);
        }
        return row;
    }

    /// <summary>便签条（左侧色条强调 + 透明底）</summary>
    private Border MakeNote(string text, Brush accent)
    {
        return new Border
        {
            Child = new TextBlock
            {
                Text = text,
                FontFamily = Theme.UiFont,
                FontSize = 11,
                Foreground = Theme.TextRegular,
                TextWrapping = TextWrapping.Wrap,
            },
            Background = Brushes.Transparent,
            BorderBrush = accent,
            BorderThickness = new Thickness(2, 0, 0, 0),
            CornerRadius = Theme.SmallRadius,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 0, 0, 4),
        };
    }

    /// <summary>监控行（标签 + 数值 + 进度条）</summary>
    private StackPanel MakeMonitorRow(string label, int value, Color color)
    {
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = new TextBlock
        {
            Text = label,
            FontFamily = Theme.UiFont,
            FontSize = 10,
            Foreground = Theme.TextSecondary,
        };
        Grid.SetColumn(l, 0);
        var v = new TextBlock
        {
            Text = $"{value}%",
            FontFamily = Theme.MonoFont,
            FontSize = 10,
            Foreground = Theme.TextRegular,
        };
        Grid.SetColumn(v, 1);
        head.Children.Add(l);
        head.Children.Add(v);

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = value,
            Height = 4,
            Foreground = new SolidColorBrush(color),
            Background = Theme.InputBackground,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 2, 0, 6),
        };

        var panel = new StackPanel();
        panel.Children.Add(head);
        panel.Children.Add(bar);
        return panel;
    }

    /// <summary>组件卡片容器（半透明玻璃拟态）</summary>
    private static Border MakeWidgetCard(UIElement content)
    {
        return new Border
        {
            Child = content,
            Background = Theme.ContainerBackground,
            BorderBrush = Theme.ContainerBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ContainerRadius,
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8),
        };
    }

    /// <summary>右下版本提示</summary>
    private StackPanel BuildVersionHint()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var badge = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = Theme.SmallRadius,
            Background = Theme.PrimaryBrush,
            Child = new TextBlock
            {
                Text = "🖥",
                FontSize = 8,
                Foreground = Theme.TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        panel.Children.Add(badge);
        panel.Children.Add(new TextBlock
        {
            Text = "zDesktop v1.0 · 右键打开菜单",
            FontFamily = Theme.UiFont,
            FontSize = 10,
            Foreground = Theme.TextFaint,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }

    /// <summary>底部任务栏示意（开始 + 应用图标 + 系统托盘）</summary>
    private Border BuildTaskbarMock()
    {
        var dock = new DockPanel { LastChildFill = true };

        // 右侧托盘
        var tray = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(tray, Dock.Right);
        tray.Children.Add(new TextBlock
        {
            Text = "📶 🔊 🔋",
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextRegular,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        tray.Children.Add(new TextBlock
        {
            Text = DateTime.Now.ToString("HH:mm"),
            FontFamily = Theme.MonoFont,
            FontSize = 11,
            Foreground = Theme.TextRegular,
            VerticalAlignment = VerticalAlignment.Center,
        });
        dock.Children.Add(tray);

        // 左侧开始 + 应用图标
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        left.Children.Add(MakeTaskbarIcon("⊞", true));
        left.Children.Add(MakeTaskbarIcon("📁", false));
        left.Children.Add(MakeTaskbarIcon("🌐", false));
        left.Children.Add(MakeTaskbarIcon("</>", true));
        left.Children.Add(MakeTaskbarIcon("🖥", true));
        dock.Children.Add(left);

        return new Border
        {
            Child = dock,
            Background = Theme.ContainerBackground,
            BorderBrush = Theme.ContainerBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(6, 0, 6, 6),
            Height = 40,
        };
    }

    /// <summary>任务栏图标（高亮表示运行中）</summary>
    private Border MakeTaskbarIcon(string glyph, bool active)
    {
        return new Border
        {
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = 14,
                FontFamily = Theme.UiFont,
                Foreground = active ? Theme.PrimaryBrush : Theme.TextRegular,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Background = active ? Theme.PrimarySubtle : Brushes.Transparent,
            CornerRadius = Theme.SmallRadius,
            Width = 32,
            Height = 32,
            Margin = new Thickness(1, 0, 1, 0),
            Cursor = Cursors.Hand,
        };
    }

    // ============================================================
    //  图例说明
    // ============================================================

    /// <summary>预览舞台下方的图例说明</summary>
    private Border BuildLegend()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "此为 zDesktop 启用后的桌面效果预览：桌面图标分组 + 浮动组件 + 自定义任务栏。",
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextRegular,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "提示：壁纸来自当前桌面设置，可点击「应用到桌面」重新写入。",
            FontFamily = Theme.UiFont,
            FontSize = 10,
            Foreground = Theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        return new Border
        {
            Child = panel,
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(14),
            Margin = new Thickness(0, 14, 0, 0),
        };
    }

    // ============================================================
    //  操作处理
    // ============================================================

    /// <summary>刷新预览：重新读取壁纸并重建舞台背景</summary>
    private void RefreshPreview()
    {
        if (_stage == null) return;
        try
        {
            _stage.Background = ResolveWallpaperBackground();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DesktopPreview] 刷新预览失败: {ex.Message}");
        }
    }

    /// <summary>应用到桌面：将当前壁纸路径写入系统</summary>
    private void ApplyToDesktop()
    {
        try
        {
            if (!string.IsNullOrEmpty(_wallpaperPath) && File.Exists(_wallpaperPath))
            {
                _wallpaper.SetWallpaper(_wallpaperPath);
                Console.WriteLine("[DesktopPreview] 已将壁纸应用到桌面");
            }
            else
            {
                Console.WriteLine("[DesktopPreview] 无可用壁纸路径，跳过应用");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DesktopPreview] 应用到桌面失败: {ex.Message}");
        }
    }
}
