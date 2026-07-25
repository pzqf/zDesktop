using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using zDesktop.Shell.ControlCenter;
using zDesktop.Shell.Styles;

// 项目同时启用 WPF + WinForms + System.Drawing，Brush 在 System.Drawing 与 System.Windows.Media 间歧义，显式别名优先 WPF
using Brush = System.Windows.Media.Brush;

namespace zDesktop.App.Pages;

/// <summary>
/// 快捷开关磁贴的运行时引用（用于刷新状态与 hover 还原）
/// </summary>
internal sealed class ToggleTile
{
    /// <summary>磁贴外框</summary>
    public Border Border { get; }

    /// <summary>图标文本</summary>
    public TextBlock Icon { get; }

    /// <summary>状态文本</summary>
    public TextBlock State { get; }

    /// <summary>当前是否开启</summary>
    public bool IsOn { get; set; }

    public ToggleTile(Border border, TextBlock icon, TextBlock state)
    {
        Border = border;
        Icon = icon;
        State = state;
    }
}

/// <summary>
/// 系统控制中心内容页 — 系统状态概览 + 快捷开关 + 系统工具入口
///
/// 视觉分区：
/// - 上部：CPU / 内存 / 磁盘 / 网络 四张状态卡片（2×2）
/// - 中部：快捷开关磁贴网格（WiFi / 蓝牙 / 夜间模式 / 勿扰 / 专注模式 / 省电 / 性能）
/// - 下部：系统工具磁贴网格（控制面板 / 任务管理器 / 注册表 等）
///
/// 交互：
/// - DispatcherTimer 每 3 秒刷新系统状态与开关状态
/// - 点击开关磁贴切换状态并即时刷新
/// - 专注模式切换时触发 <see cref="FocusModeToggled"/> 事件，由 App 订阅隐藏/显示组件
///
/// 所有颜色 / 字体 / 圆角均引用 <see cref="Theme"/> 常量，不硬编码。
/// </summary>
public sealed class ControlCenterPage : ContentPage
{
    /// <summary>控制中心服务（由 App 注入）</summary>
    private readonly ControlCenterService _service;

    /// <summary>3 秒刷新定时器</summary>
    private DispatcherTimer? _timer;

    // ===== 状态卡片运行时引用 =====
    private Action<double>? _setCpuBar;
    private Action<double>? _setMemBar;
    private TextBlock? _cpuValue;
    private TextBlock? _memValue;
    private TextBlock? _memDetail;
    private Panel? _diskPanel;
    private TextBlock? _netValue;
    private TextBlock? _netDetail;

    /// <summary>开关磁贴映射（key → 磁贴引用）</summary>
    private readonly Dictionary<string, ToggleTile> _tiles = new();

    /// <summary>内容根面板（取代旧 ContentArea）</summary>
    private readonly StackPanel _root;

    /// <summary>
    /// 专注模式切换事件 — 参数为切换后的开启状态
    /// 由 App 订阅，用于隐藏/显示所有桌面组件
    /// </summary>
    public event Action<bool>? FocusModeToggled;

    /// <summary>
    /// 构造控制中心内容页
    /// </summary>
    /// <param name="service">控制中心服务实例（由 App 创建并注入）</param>
    public ControlCenterPage(ControlCenterService service)
    {
        _service = service;
        Title = "系统控制中心";
        NavId = "control-center";

        _root = new StackPanel { Margin = new Thickness(16) };

        BuildStatusSection();
        BuildToggleSection();
        BuildThemeSection();
        BuildToolsSection();

        Content = _root;

        // 首次刷新（CPU 首次采样为 0%，3 秒后定时器刷新出真实值）
        RefreshStatus();
        StartTimer();
    }

    // ============================================================
    //  状态概览区
    // ============================================================

    /// <summary>构建上部系统状态卡片网格（2×2）</summary>
    private void BuildStatusSection()
    {
        _root.Children.Add(CreateSectionHeader("系统状态"));

        var grid = new UniformGrid { Columns = 2, Rows = 2 };
        grid.Margin = new Thickness(0, 0, 0, 4);

        // CPU 卡
        var cpuCard = CreateStatusCard("CPU", out var cpuVal, out var cpuDetail, out var setCpu, Theme.PrimaryBrush);
        _cpuValue = cpuVal;
        _setCpuBar = setCpu;
        cpuVal.Text = "0.0%";
        cpuDetail.Text = "使用率";
        grid.Children.Add(cpuCard);

        // 内存卡
        var memCard = CreateStatusCard("内存", out var memVal, out var memDetail, out var setMem, Theme.SuccessBrush);
        _memValue = memVal;
        _memDetail = memDetail;
        _setMemBar = setMem;
        memVal.Text = "0.0%";
        memDetail.Text = "-- / -- GB";
        grid.Children.Add(memCard);

        // 磁盘卡
        var diskCard = CreateDiskCard(out var diskPanel);
        _diskPanel = diskPanel;
        grid.Children.Add(diskCard);

        // 网络卡
        var netCard = CreateNetworkCard(out var netVal, out var netDetail);
        _netValue = netVal;
        _netDetail = netDetail;
        netVal.Text = "离线";
        netDetail.Text = "检测中…";
        grid.Children.Add(netCard);

        _root.Children.Add(grid);
    }

    /// <summary>创建分区标题</summary>
    private static TextBlock CreateSectionHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(2, 10, 0, 8),
        };
    }

    /// <summary>创建带进度条的状态卡片（CPU / 内存通用）</summary>
    private Border CreateStatusCard(
        string title, out TextBlock valueText, out TextBlock detailText,
        out Action<double> setBar, Brush barBrush)
    {
        var titleTb = new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
        };
        valueText = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.MonoFont,
            Foreground = Theme.TextPrimary,
            Margin = new Thickness(0, 4, 0, 2),
        };
        detailText = new TextBlock
        {
            FontSize = 10,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextFaint,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        var bar = MakeProgressBar(barBrush, out setBar);

        var panel = new StackPanel();
        panel.Children.Add(titleTb);
        panel.Children.Add(valueText);
        panel.Children.Add(detailText);
        panel.Children.Add(bar);

        return WrapCard(panel);
    }

    /// <summary>创建磁盘卡片（动态渲染各盘符小条形图）</summary>
    private Border CreateDiskCard(out Panel diskPanel)
    {
        var titleTb = new TextBlock
        {
            Text = "磁盘",
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 8),
        };
        diskPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };

        var panel = new StackPanel();
        panel.Children.Add(titleTb);
        panel.Children.Add(diskPanel);

        return WrapCard(panel);
    }

    /// <summary>创建网络卡片（在线/离线 + 描述）</summary>
    private Border CreateNetworkCard(out TextBlock valueText, out TextBlock detailText)
    {
        var titleTb = new TextBlock
        {
            Text = "网络",
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
        };
        valueText = new TextBlock
        {
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextPrimary,
            Margin = new Thickness(0, 4, 0, 2),
        };
        detailText = new TextBlock
        {
            FontSize = 10,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextFaint,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };

        var panel = new StackPanel();
        panel.Children.Add(titleTb);
        panel.Children.Add(valueText);
        panel.Children.Add(detailText);

        return WrapCard(panel);
    }

    /// <summary>将内容包装为统一卡片外观（Theme 笔刷 + 圆角）</summary>
    private static Border WrapCard(UIElement content)
    {
        return new Border
        {
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(12),
            Margin = new Thickness(3),
            Child = content,
        };
    }

    /// <summary>
    /// 创建圆角进度条（外层 Border 裁剪圆角，内层 ProgressBar 控制填充）
    /// </summary>
    private static Border MakeProgressBar(Brush fillBrush, out Action<double> setValue)
    {
        var pb = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 6,
            Foreground = fillBrush,
            Background = Theme.InputBackground,
            BorderThickness = new Thickness(0),
        };
        var clip = new Border
        {
            CornerRadius = Theme.SmallRadius,
            ClipToBounds = true,
            Child = pb,
        };
        setValue = v => pb.Value = Math.Clamp(v, 0, 100);
        return clip;
    }

    // ============================================================
    //  快捷开关区
    // ============================================================

    /// <summary>构建中部快捷开关磁贴网格</summary>
    private void BuildToggleSection()
    {
        _root.Children.Add(CreateSectionHeader("快捷开关"));

        var grid = new UniformGrid { Columns = 4 };
        grid.Margin = new Thickness(0, 0, 0, 4);

        foreach (var desc in _service.GetToggles())
        {
            grid.Children.Add(CreateToggleTile(desc));
        }

        _root.Children.Add(grid);
    }

    /// <summary>创建单个开关磁贴</summary>
    private Border CreateToggleTile(ToggleDescriptor desc)
    {
        var icon = new TextBlock
        {
            Text = desc.Icon,
            FontSize = 22,
            FontFamily = Theme.UiFont,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var name = new TextBlock
        {
            Text = desc.Name,
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextRegular,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var state = new TextBlock
        {
            Text = "—",
            FontSize = 10,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextFaint,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(icon);
        panel.Children.Add(name);
        panel.Children.Add(state);

        var border = new Border
        {
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(10, 14, 10, 14),
            Margin = new Thickness(3),
            Cursor = Cursors.Hand,
            Child = panel,
        };

        var tile = new ToggleTile(border, icon, state);
        _tiles[desc.Key] = tile;

        // hover 高亮（仅未开启时加深底色）
        border.MouseEnter += (_, _) =>
        {
            if (!tile.IsOn) border.Background = Theme.InputBackground;
        };
        border.MouseLeave += (_, _) =>
        {
            border.Background = tile.IsOn ? Theme.PrimarySubtle : Theme.ListItemBackground;
        };
        border.MouseLeftButtonUp += (_, _) => OnToggleClicked(desc.Key);

        return border;
    }

    /// <summary>开关磁贴点击处理</summary>
    private void OnToggleClicked(string key)
    {
        try
        {
            if (key == "focus-mode")
            {
                var newOn = _service.Toggle(key);
                UpdateTileFromService(key);
                FocusModeToggled?.Invoke(newOn);
                return;
            }

            _service.Toggle(key);
            UpdateTileFromService(key);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ControlCenter] 开关 {key} 切换失败: {ex.Message}");
        }
    }

    /// <summary>根据服务返回的状态刷新单个磁贴外观</summary>
    private void UpdateTileFromService(string key)
    {
        if (!_tiles.TryGetValue(key, out var tile)) return;
        var s = _service.GetToggleStatus(key);
        tile.IsOn = s.IsOn;
        tile.Border.Background = s.IsOn ? Theme.PrimarySubtle : Theme.ListItemBackground;
        tile.Border.BorderBrush = s.IsOn ? Theme.PrimaryBrush : Theme.Divider;
        tile.Icon.Foreground = s.IsOn ? Theme.PrimaryBrush : Theme.TextRegular;
        tile.State.Foreground = s.IsOn ? Theme.PrimaryBrush : Theme.TextFaint;
        tile.State.Text = s.StatusText;
    }

    // ============================================================
    //  主题切换区
    // ============================================================

    /// <summary>主题卡片引用（用于切换选中态边框）</summary>
    private readonly Dictionary<ThemePreset, Border> _themeCards = new();

    /// <summary>构建主题切换区 — 墨韵 / 浅草 双卡片预览</summary>
    private void BuildThemeSection()
    {
        _root.Children.Add(CreateSectionHeader("主题风格"));

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4),
        };

        panel.Children.Add(CreateThemeCard(
            ThemePreset.MoYun, "墨韵", "深色水墨 · 靛蓝朱砂",
            Color.FromRgb(0x13, 0x11, 0x1A), Color.FromRgb(0x4A, 0x6F, 0xA5)));

        panel.Children.Add(CreateThemeCard(
            ThemePreset.QianCao, "浅草", "浅色宣纸 · 松绿青瓷",
            Color.FromRgb(0xF2, 0xEF, 0xE6), Color.FromRgb(0x4A, 0x7C, 0x59)));

        _root.Children.Add(panel);

        // 初始化选中态
        UpdateThemeSelection();
    }

    /// <summary>创建单个主题预览卡片</summary>
    private Border CreateThemeCard(ThemePreset preset, string name, string desc, Color bg, Color accent)
    {
        // 预览色块条（底色 + 强调色 + 次级色）
        var swatchPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
        };
        swatchPanel.Children.Add(new Border
        {
            Width = 40, Height = 20,
            Background = new SolidColorBrush(bg),
            CornerRadius = new CornerRadius(3, 0, 0, 3),
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1, 1, 0, 1),
        });
        swatchPanel.Children.Add(new Border
        {
            Width = 20, Height = 20,
            Background = new SolidColorBrush(accent),
        });
        swatchPanel.Children.Add(new Border
        {
            Width = 20, Height = 20,
            Background = new SolidColorBrush(Color.FromArgb(0x60, accent.R, accent.G, accent.B)),
            CornerRadius = new CornerRadius(0, 3, 3, 0),
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 1, 1, 1),
        });

        var nameTb = new TextBlock
        {
            Text = name,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.TitleFont,
            Foreground = Theme.TextPrimary,
        };
        var descTb = new TextBlock
        {
            Text = desc,
            FontSize = 10,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var panel = new StackPanel { Margin = new Thickness(4) };
        panel.Children.Add(swatchPanel);
        panel.Children.Add(nameTb);
        panel.Children.Add(descTb);

        var border = new Border
        {
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(3),
            Cursor = Cursors.Hand,
            Child = panel,
            Tag = preset,
        };

        _themeCards[preset] = border;

        border.MouseEnter += (_, _) =>
        {
            if (Theme.CurrentPreset != preset)
                border.Background = Theme.InputBackground;
        };
        border.MouseLeave += (_, _) =>
        {
            if (Theme.CurrentPreset != preset)
                border.Background = Theme.ListItemBackground;
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            Theme.ApplyPreset(preset);
            UpdateThemeSelection();
        };

        return border;
    }

    /// <summary>刷新主题卡片选中态边框</summary>
    private void UpdateThemeSelection()
    {
        foreach (var (preset, border) in _themeCards)
        {
            var selected = Theme.CurrentPreset == preset;
            border.BorderBrush = selected ? Theme.PrimaryBrush : Theme.Divider;
            border.BorderThickness = new Thickness(selected ? 2 : 1);
            border.Background = selected ? Theme.PrimarySubtle : Theme.ListItemBackground;
        }
    }

    // ============================================================
    //  系统工具区
    // ============================================================

    /// <summary>构建下部系统工具磁贴网格</summary>
    private void BuildToolsSection()
    {
        _root.Children.Add(CreateSectionHeader("系统工具"));

        var grid = new UniformGrid { Columns = 5 };
        grid.Margin = new Thickness(0, 0, 0, 4);

        foreach (var desc in _service.GetTools())
        {
            grid.Children.Add(CreateToolTile(desc));
        }

        _root.Children.Add(grid);
    }

    /// <summary>创建单个系统工具磁贴</summary>
    private Border CreateToolTile(ToolDescriptor desc)
    {
        var icon = new TextBlock
        {
            Text = desc.Icon,
            FontSize = 20,
            FontFamily = Theme.UiFont,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var name = new TextBlock
        {
            Text = desc.Name,
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(icon);
        panel.Children.Add(name);

        var border = new Border
        {
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.SmallRadius,
            Padding = new Thickness(8, 12, 8, 12),
            Margin = new Thickness(3),
            Cursor = Cursors.Hand,
            Child = panel,
        };

        border.MouseEnter += (_, _) => border.Background = Theme.PrimarySubtle;
        border.MouseLeave += (_, _) => border.Background = Theme.ListItemBackground;
        border.MouseLeftButtonUp += (_, _) =>
        {
            try { _service.LaunchTool(desc.Key); }
            catch (Exception ex) { Console.WriteLine($"[ControlCenter] 启动工具 {desc.Key} 失败: {ex.Message}"); }
        };

        return border;
    }

    // ============================================================
    //  定时刷新
    // ============================================================

    /// <summary>启动 3 秒刷新定时器</summary>
    private void StartTimer()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
    }

    /// <summary>刷新所有状态：CPU / 内存 / 磁盘 / 网络 + 全部开关</summary>
    private void RefreshStatus()
    {
        try
        {
            // CPU / 内存
            var sys = _service.GetSystemStatus();
            if (_cpuValue != null) _cpuValue.Text = $"{sys.CpuUsage:F1}%";
            _setCpuBar?.Invoke(sys.CpuUsage);

            if (_memValue != null) _memValue.Text = $"{sys.MemoryUsage:F1}%";
            _setMemBar?.Invoke(sys.MemoryUsage);

            if (_memDetail != null && sys.MemoryTotalBytes > 0)
            {
                var usedGb = (sys.MemoryTotalBytes - sys.MemoryAvailableBytes) / 1024.0 / 1024 / 1024;
                var totalGb = sys.MemoryTotalBytes / 1024.0 / 1024 / 1024;
                _memDetail.Text = $"{usedGb:F1} / {totalGb:F1} GB";
            }

            // 磁盘
            RefreshDisk(_service.GetDiskStatus());

            // 网络
            var net = _service.GetNetworkStatus();
            if (_netValue != null)
            {
                _netValue.Text = net.IsAvailable ? "在线" : "离线";
                _netValue.Foreground = net.IsAvailable ? Theme.SuccessBrush : Theme.TextFaint;
            }
            if (_netDetail != null) _netDetail.Text = net.Description;

            // 开关状态（外部可能变化，每次刷新）
            foreach (var key in _tiles.Keys.ToList())
            {
                UpdateTileFromService(key);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ControlCenter] 刷新状态失败: {ex.Message}");
        }
    }

    /// <summary>重建磁盘各盘符小条形图</summary>
    private void RefreshDisk(IReadOnlyList<DriveStatus> drives)
    {
        if (_diskPanel == null) return;
        _diskPanel.Children.Clear();

        foreach (var d in drives.Where(x => x.IsReady))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var letter = d.Name.Length >= 1 ? d.Name[..1] : d.Name;
            var lbl = new TextBlock
            {
                Text = letter,
                FontSize = 11,
                FontFamily = Theme.MonoFont,
                Foreground = Theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(lbl, 0);

            var bar = MakeProgressBar(Theme.PrimaryAccent, out var setFill);
            bar.Margin = new Thickness(6, 0, 6, 0);
            bar.VerticalAlignment = VerticalAlignment.Center;
            setFill(d.UsagePercent);
            Grid.SetColumn(bar, 1);

            var pct = new TextBlock
            {
                Text = $"{d.UsagePercent:F0}%",
                FontSize = 10,
                FontFamily = Theme.MonoFont,
                Foreground = Theme.TextFaint,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(pct, 2);

            row.Children.Add(lbl);
            row.Children.Add(bar);
            row.Children.Add(pct);
            _diskPanel.Children.Add(row);
        }

        if (_diskPanel.Children.Count == 0)
        {
            _diskPanel.Children.Add(new TextBlock
            {
                Text = "无可用磁盘",
                FontSize = 10,
                FontFamily = Theme.UiFont,
                Foreground = Theme.TextFaint,
            });
        }
    }
}
