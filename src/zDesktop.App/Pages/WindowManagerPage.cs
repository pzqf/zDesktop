using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using zDesktop.Shell.Styles;
using zDesktop.Shell.WindowManager;

// 本项目同时启用 WPF + WinForms（System.Drawing / System.Windows.Forms 为全局 using），
// csproj 已为 Brushes/Button/SolidColorBrush 等加 WPF 别名，但未覆盖 Brush 基类型，
// 故在此文件内显式别名，避免与 System.Drawing.Brush 歧义。
using Brush = System.Windows.Media.Brush;

namespace zDesktop.App.Pages;

/// <summary>
/// 窗口管理内容页 — 枚举/排列/置顶/透明度/托盘化顶级窗口
///
/// 顶部：预设布局按钮（对选中窗口或捕获的前台窗口应用）
/// 中部：活动窗口列表（标题/进程/状态徽章 + 行内操作：置顶/透明度/托盘/关闭）
/// 底部：刷新 + 还原所有托盘窗口
///
/// 嵌入主窗口右侧内容区，不再独立弹窗。
/// 通过 DispatcherTimer 每 2 秒刷新窗口列表（保留用户选中项）。
/// 所有颜色 / 字体 / 圆角一律引用 <see cref="Theme"/> 常量。
/// </summary>
public sealed class WindowManagerPage : ContentPage
{
    private readonly WindowManagerService _service;

    /// <summary>窗口列表容器</summary>
    private readonly StackPanel _listPanel;

    /// <summary>底部托盘计数文本</summary>
    private readonly TextBlock _hiddenCountText;

    /// <summary>当前选中的窗口句柄（Zero 表示未选中）</summary>
    private IntPtr _selectedHwnd = IntPtr.Zero;

    /// <summary>页面打开时捕获的前台窗口（应用布局的默认目标）</summary>
    private readonly IntPtr _capturedForeground;

    /// <summary>主窗口自身的窗口句柄（用于从列表中排除）</summary>
    private IntPtr _selfHwnd = IntPtr.Zero;

    /// <summary>每窗口最近设置的透明度（alpha），用于滑块回显</summary>
    private readonly Dictionary<IntPtr, byte> _alphaMap = new();

    /// <summary>定时刷新窗口列表</summary>
    private readonly DispatcherTimer _timer;

    /// <summary>
    /// 构造窗口管理内容页
    /// </summary>
    /// <param name="service">窗口管理服务</param>
    public WindowManagerPage(WindowManagerService service)
    {
        _service = service;
        Title = "窗口管理";
        NavId = "window-manager";

        // 在页面获得焦点前捕获前台窗口，作为布局默认目标
        _capturedForeground = service.GetForegroundWindow();

        var root = new DockPanel();
        root.LastChildFill = true;

        // ===== 顶部：布局按钮区 =====
        var topSection = BuildLayoutBar();
        DockPanel.SetDock(topSection, Dock.Top);
        root.Children.Add(topSection);

        // ===== 底部：操作栏 =====
        var bottomSection = BuildBottomBar(out _hiddenCountText);
        DockPanel.SetDock(bottomSection, Dock.Bottom);
        root.Children.Add(bottomSection);

        // ===== 中部：窗口列表（滚动，自动填充剩余空间）=====
        _listPanel = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = _listPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 8, 12, 8),
        };
        root.Children.Add(scroll);

        Content = root;

        // 获取主窗口句柄（用于从列表中排除），Loaded 时 HWND 已就绪
        Loaded += (_, _) =>
        {
            var src = PresentationSource.FromVisual(this) as HwndSource;
            _selfHwnd = src?.Handle ?? IntPtr.Zero;
            RefreshList();
        };

        // 定时刷新（保留选中项）
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshList();
        _timer.Start();

        // 页面卸载时停止定时器
        Unloaded += (_, _) => _timer.Stop();

        // 首次立即填充（Loaded 也会再触发一次）
        RefreshList();
    }

    // ===== 顶部布局栏 =====

    /// <summary>构建顶部预设布局按钮区</summary>
    private UIElement BuildLayoutBar()
    {
        var wrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 12, 12, 4),
        };

        AddLayoutButton(wrap, "左半", LayoutType.LeftHalf);
        AddLayoutButton(wrap, "右半", LayoutType.RightHalf);
        AddLayoutButton(wrap, "上半", LayoutType.TopHalf);
        AddLayoutButton(wrap, "下半", LayoutType.BottomHalf);
        AddLayoutButton(wrap, "左三", LayoutType.ThirdsLeft);
        AddLayoutButton(wrap, "中三", LayoutType.ThirdsCenter);
        AddLayoutButton(wrap, "右三", LayoutType.ThirdsRight);
        AddLayoutButton(wrap, "四宫格", LayoutType.Quadrants);

        var cascadeBtn = CreateSecondaryButton("层叠");
        cascadeBtn.Margin = new Thickness(0, 0, 8, 8);
        cascadeBtn.Click += (_, _) => { _service.CascadeWindows(); RefreshList(); };
        wrap.Children.Add(cascadeBtn);

        var restoreBtn = CreateSecondaryButton("还原");
        restoreBtn.Margin = new Thickness(0, 0, 0, 8);
        restoreBtn.Click += (_, _) =>
        {
            var target = ResolveTarget();
            if (target != IntPtr.Zero)
                _service.RestoreWindow(target);
            RefreshList();
        };
        wrap.Children.Add(restoreBtn);

        return new Border
        {
            Child = wrap,
            Background = Theme.HeaderBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    /// <summary>添加一个布局按钮到容器</summary>
    private void AddLayoutButton(WrapPanel wrap, string label, LayoutType layout)
    {
        var btn = CreateSecondaryButton(label);
        btn.Margin = new Thickness(0, 0, 8, 8);
        btn.Click += (_, _) =>
        {
            var target = ResolveTarget();
            if (target != IntPtr.Zero)
                _service.ApplyLayout(target, layout);
            RefreshList();
        };
        wrap.Children.Add(btn);
    }

    // ===== 底部操作栏 =====

    /// <summary>构建底部操作栏（托盘计数 + 还原所有 + 刷新）</summary>
    private UIElement BuildBottomBar(out TextBlock hiddenCount)
    {
        hiddenCount = new TextBlock
        {
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var bar = new DockPanel
        {
            Margin = new Thickness(12, 8, 12, 12),
            LastChildFill = true,
        };

        DockPanel.SetDock(hiddenCount, Dock.Left);
        bar.Children.Add(hiddenCount);

        var rightPanel = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(rightPanel, Dock.Right);

        var restoreAllBtn = CreateSecondaryButton("还原所有托盘窗口");
        restoreAllBtn.Margin = new Thickness(0, 0, 8, 0);
        restoreAllBtn.Click += (_, _) =>
        {
            _service.RestoreAllFromTray();
            RefreshList();
        };
        rightPanel.Children.Add(restoreAllBtn);

        var refreshBtn = CreateSecondaryButton("刷新");
        refreshBtn.Click += (_, _) => RefreshList();
        rightPanel.Children.Add(refreshBtn);

        bar.Children.Add(rightPanel);

        return new Border
        {
            Child = bar,
            Background = Theme.HeaderBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
    }

    /// <summary>更新底部托盘计数文本</summary>
    private void UpdateHiddenCount()
    {
        var n = _service.GetHiddenWindows().Count;
        _hiddenCountText.Text = n > 0 ? $"托盘中：{n} 个窗口" : string.Empty;
    }

    // ===== 目标解析 =====

    /// <summary>布局目标解析：选中窗口优先，否则用页面打开时捕获的前台窗口</summary>
    private IntPtr ResolveTarget()
    {
        var target = _selectedHwnd != IntPtr.Zero ? _selectedHwnd : _capturedForeground;
        if (target == _selfHwnd || target == IntPtr.Zero)
            return IntPtr.Zero;
        return target;
    }

    // ===== 列表刷新 =====

    /// <summary>刷新窗口列表（保留用户选中项）</summary>
    private void RefreshList()
    {
        _listPanel.Children.Clear();

        List<WindowInfo> windows;
        try
        {
            windows = _service.EnumerateWindows();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowManager] 刷新列表失败: {ex.Message}");
            return;
        }

        // 排除主窗口
        if (_selfHwnd != IntPtr.Zero)
            windows = windows.Where(w => w.Hwnd != _selfHwnd).ToList();

        // 选中项若已不存在则清空
        if (_selectedHwnd != IntPtr.Zero && windows.All(w => w.Hwnd != _selectedHwnd))
            _selectedHwnd = IntPtr.Zero;

        if (windows.Count == 0)
        {
            _listPanel.Children.Add(new TextBlock
            {
                Text = "未发现可见窗口",
                FontFamily = Theme.UiFont,
                FontSize = 12,
                Foreground = Theme.TextFaint,
                Margin = new Thickness(8, 16, 8, 8),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            UpdateHiddenCount();
            return;
        }

        foreach (var win in windows)
            _listPanel.Children.Add(CreateRow(win));

        UpdateHiddenCount();
    }

    // ===== 单行构建 =====

    /// <summary>构建一行窗口信息（图标 + 标题/进程/徽章 + 行内操作）</summary>
    private UIElement CreateRow(WindowInfo win)
    {
        var isSelected = win.Hwnd == _selectedHwnd;

        var row = new Border
        {
            Background = isSelected ? Theme.PrimarySubtle : Theme.ListItemBackground,
            BorderBrush = isSelected ? Theme.PrimaryAccent : Theme.InputBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand,
            Tag = win.Hwnd,
        };

        var dock = new DockPanel { LastChildFill = true };

        // --- 图标占位（进程名首字母）---
        var icon = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = Theme.SmallRadius,
            Background = Theme.PrimarySubtle,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = GetInitial(win.ProcessName),
                FontFamily = Theme.UiFont,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);

        // --- 右侧操作按钮 ---
        var actions = BuildRowActions(win);
        DockPanel.SetDock(actions, Dock.Right);
        dock.Children.Add(actions);

        // --- 中部：标题 + 进程 + 徽章 ---
        var titleRow = new DockPanel { LastChildFill = true };

        var badges = BuildBadges(win);
        DockPanel.SetDock(badges, Dock.Right);
        titleRow.Children.Add(badges);

        var title = new TextBlock
        {
            Text = win.Title,
            FontFamily = Theme.UiFont,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleRow.Children.Add(title);

        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(titleRow);
        center.Children.Add(new TextBlock
        {
            Text = win.ProcessName,
            FontFamily = Theme.MonoFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        });

        dock.Children.Add(center);
        row.Child = dock;

        // 行点击选中（点击按钮/滑块/弹出层不触发）
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (IsInInteractiveControl(e.OriginalSource))
                return;
            _selectedHwnd = win.Hwnd;
            RefreshList();
            e.Handled = true;
        };

        return row;
    }

    /// <summary>判断点击源是否落在按钮/滑块/弹出层内（避免误触发行选中）</summary>
    private static bool IsInInteractiveControl(object source)
    {
        if (source is not DependencyObject d)
            return false;
        while (d != null)
        {
            if (d is Button || d is Slider || d is Popup)
                return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>构建状态徽章行（置顶/最大化/最小化）</summary>
    private static UIElement BuildBadges(WindowInfo win)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (win.IsTopmost)
            panel.Children.Add(MakeBadge("置顶", new SolidColorBrush(Theme.Primary)));
        if (win.IsMaximized)
            panel.Children.Add(MakeBadge("最大化", new SolidColorBrush(Theme.Success)));
        if (win.IsMinimized)
            panel.Children.Add(MakeBadge("最小化", new SolidColorBrush(Theme.Warning)));

        return panel;
    }

    /// <summary>创建单个状态徽章</summary>
    private static UIElement MakeBadge(string text, Brush foreground)
    {
        return new Border
        {
            Background = Theme.ListItemBackground,
            CornerRadius = Theme.SmallRadius,
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontFamily = Theme.UiFont,
                FontSize = 10,
                Foreground = foreground,
            },
        };
    }

    /// <summary>构建行右侧操作按钮组（置顶/透明度/托盘/关闭）</summary>
    private UIElement BuildRowActions(WindowInfo win)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 置顶切换
        var topmostBtn = MakeIconButton(win.IsTopmost ? "取消置顶" : "置顶", new SolidColorBrush(Theme.Primary));
        topmostBtn.Click += (_, _) =>
        {
            _service.ToggleTopmost(win.Hwnd);
            RefreshList();
        };
        panel.Children.Add(topmostBtn);

        // 透明度（弹出滑块，实时调用 SetTransparency）
        var alphaBtn = MakeIconButton("透明", Theme.TextSecondary);
        var popup = new Popup
        {
            Placement = PlacementMode.Bottom,
            PlacementTarget = alphaBtn,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = AlphaToPercent(_alphaMap.TryGetValue(win.Hwnd, out var a) ? a : (byte)255),
            Width = 140,
            TickFrequency = 10,
        };
        var percentLabel = new TextBlock
        {
            Text = $"{(int)slider.Value}%",
            FontFamily = Theme.MonoFont,
            FontSize = 11,
            Foreground = Theme.TextPrimary,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        popup.Child = new Border
        {
            Background = Theme.ContainerBackground,
            BorderBrush = Theme.ContainerBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(12, 10, 12, 10),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "透明度",
                        FontFamily = Theme.UiFont,
                        FontSize = 11,
                        Foreground = Theme.TextSecondary,
                        Margin = new Thickness(0, 0, 0, 6),
                    },
                    slider,
                    percentLabel,
                },
            },
        };
        slider.ValueChanged += (_, e) =>
        {
            var alpha = PercentToAlpha(e.NewValue);
            _alphaMap[win.Hwnd] = alpha;
            _service.SetTransparency(win.Hwnd, alpha);
            percentLabel.Text = $"{(int)e.NewValue}%";
        };
        alphaBtn.Click += (_, _) => popup.IsOpen = !popup.IsOpen;
        panel.Children.Add(alphaBtn);
        panel.Children.Add(popup);

        // 最小化到托盘
        var trayBtn = MakeIconButton("托盘", Theme.TextSecondary);
        trayBtn.Click += (_, _) =>
        {
            _service.MinimizeToTray(win.Hwnd);
            _selectedHwnd = IntPtr.Zero;
            RefreshList();
        };
        panel.Children.Add(trayBtn);

        // 关闭窗口（投递 WM_CLOSE 后异步刷新）
        var closeBtn = MakeIconButton("关闭", new SolidColorBrush(Theme.Error));
        closeBtn.Click += (_, _) =>
        {
            _service.CloseWindow(win.Hwnd);
            _selectedHwnd = IntPtr.Zero;
            Dispatcher.BeginInvoke(new Action(RefreshList), DispatcherPriority.Background);
        };
        panel.Children.Add(closeBtn);

        return panel;
    }

    /// <summary>创建行内小按钮（紧凑样式）</summary>
    private static Button MakeIconButton(string text, Brush foreground)
    {
        return new Button
        {
            Content = text,
            Height = 26,
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Background = Theme.InputBackground,
            Foreground = foreground,
            BorderBrush = Theme.InputBorder,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
    }

    /// <summary>取进程名首字母（大写），用于图标占位</summary>
    private static string GetInitial(string processName)
    {
        return string.IsNullOrEmpty(processName)
            ? "?"
            : char.ToUpperInvariant(processName[0]).ToString();
    }

    /// <summary>alpha(0-255) → 百分比(0-100)</summary>
    private static double AlphaToPercent(byte alpha) => Math.Round(alpha / 255.0 * 100);

    /// <summary>百分比(0-100) → alpha(30-255)，最低 30 保证可见</summary>
    private static byte PercentToAlpha(double percent)
    {
        const double min = 30.0;
        const double max = 255.0;
        var alpha = min + percent / 100.0 * (max - min);
        return (byte)Math.Round(Math.Clamp(alpha, min, max));
    }
}
