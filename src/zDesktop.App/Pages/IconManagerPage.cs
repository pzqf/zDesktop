using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using zDesktop.Shell.IconManager;
using zDesktop.Shell.Styles;

// App 项目同时启用 WPF + WinForms + System.Drawing，Brush 基类型在 System.Drawing 与
// System.Windows.Media 间歧义（csproj 已别名 SolidColorBrush/Brushes/Color 等但未覆盖 Brush 基类型），
// 此处显式别名到 WPF 画笔，与其它页面一致。
using Brush = System.Windows.Media.Brush;
// RadioButton 在 WPF(System.Windows.Controls) 与 WinForms(System.Windows.Forms) 间歧义，别名到 WPF。
using RadioButton = System.Windows.Controls.RadioButton;

namespace zDesktop.App.Pages;

/// <summary>
/// 图标管理内容页 — 图标配色 / 图标包 / 图标替换 三合一
///
/// 顶部标签栏切换三个分区：
/// - 图标配色：模式 / 目标颜色 / 强度 / 范围 + Before/After 预览 + 应用/恢复
/// - 图标包：内置包列表（需下载）+ 已安装包 + 导入 ZIP
/// - 图标替换：桌面快捷方式列表，逐个更换/恢复图标
///
/// 嵌入主窗口右侧内容区，不再独立弹窗。
/// 服务由 App 通过构造函数注入，所有颜色 / 字体 / 圆角均引用 <see cref="Theme"/> 常量。
/// </summary>
public sealed class IconManagerPage : ContentPage
{
    /// <summary>图标管理服务（由 App 注入）</summary>
    private readonly IconManagerService _service;

    // ===== 预设色板（用户数据，十六进制字符串存储，渲染时转 Brush）=====
    private static readonly string[] PresetColors =
    {
        "#FFFFFF", "#6C5CE7", "#3B82F6", "#10B981",
        "#F59E0B", "#EF4444", "#EC4899", "#06B6D4",
    };

    // ===== 标签页内容容器 =====
    private readonly FrameworkElement _colorizePanel;
    private readonly FrameworkElement _packsPanel;
    private readonly FrameworkElement _replacePanel;

    /// <summary>当前选中的标签索引</summary>
    private int _currentTab;

    /// <summary>标签栏按钮容器（用于切换高亮）</summary>
    private Panel _tabBar = null!;

    // ===== 配色区控件引用 =====
    private readonly RadioButton[] _modeRadios = new RadioButton[3];
    private readonly RadioButton[] _scopeRadios = new RadioButton[4];
    private readonly List<Border> _swatchBorders = new();
    private Slider _strengthSlider = null!;
    private TextBlock _strengthLabel = null!;
    private Panel _beforePanel = null!;
    private Panel _afterPanel = null!;
    private TextBlock _statusText = null!;

    /// <summary>当前选中的目标颜色（十六进制）</summary>
    private string _selectedColor = "#6C5CE7";

    /// <summary>预览样例图标</summary>
    private readonly List<SampleIcon> _samples = new();

    // ===== 图标包区 / 替换区容器 =====
    private Panel _packsListPanel = null!;
    private Panel _replaceListPanel = null!;

    /// <summary>
    /// 构造图标管理内容页
    /// </summary>
    /// <param name="service">图标管理服务（由 App 创建并注入）</param>
    public IconManagerPage(IconManagerService service)
    {
        _service = service;
        Title = "图标管理";
        NavId = "icon-manager";

        var root = new DockPanel();
        root.LastChildFill = true;

        // ===== 顶部标签栏 =====
        var tabBar = BuildTabBar();
        DockPanel.SetDock(tabBar, Dock.Top);
        root.Children.Add(tabBar);

        // ===== 内容区（三个面板叠放，按选中标签切换可见性）=====
        var contentHost = new Grid();
        _colorizePanel = BuildColorizePanel();
        _packsPanel = BuildPacksPanel();
        _replacePanel = BuildReplacePanel();
        contentHost.Children.Add(_colorizePanel);
        contentHost.Children.Add(_packsPanel);
        contentHost.Children.Add(_replacePanel);
        root.Children.Add(contentHost);

        Content = root;

        // 加载样例图标与已保存配置
        try
        {
            _samples.AddRange(_service.GetSampleIconsForPreview(3));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 加载样例图标失败: {ex.Message}");
        }

        LoadConfigToUi();
        SelectTab(0);

        Loaded += (_, _) => RefreshPreview();
    }

    // ============================================================
    //  标签栏
    // ============================================================

    /// <summary>构建顶部三标签栏</summary>
    private UIElement BuildTabBar()
    {
        _tabBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Background = Theme.HeaderBackground,
        };

        AddTabButton(_tabBar, "图标配色", 0);
        AddTabButton(_tabBar, "图标包", 1);
        AddTabButton(_tabBar, "图标替换", 2);

        return new Border
        {
            Child = _tabBar,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    /// <summary>添加一个标签按钮</summary>
    private void AddTabButton(Panel container, string label, int index)
    {
        var tab = new Border
        {
            Padding = new Thickness(18, 12, 18, 12),
            Cursor = Cursors.Hand,
            Tag = index,
            Background = Brushes.Transparent,
            Child = new TextBlock
            {
                Text = label,
                FontFamily = Theme.UiFont,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                Foreground = Theme.TextSecondary,
            },
        };

        tab.MouseLeftButtonUp += (_, _) => SelectTab(index);
        tab.MouseEnter += (_, _) =>
        {
            if (_currentTab != index) tab.Background = Theme.InputBackground;
        };
        tab.MouseLeave += (_, _) =>
        {
            if (_currentTab != index) tab.Background = Brushes.Transparent;
        };

        container.Children.Add(tab);
    }

    /// <summary>切换到指定标签页</summary>
    private void SelectTab(int index)
    {
        _currentTab = index;

        // 切换内容面板可见性
        _colorizePanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        _packsPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        _replacePanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        // 更新标签栏高亮状态
        for (var i = 0; i < _tabBar.Children.Count; i++)
        {
            if (_tabBar.Children[i] is not Border tab) continue;
            var selected = i == index;
            tab.Background = selected ? Theme.PrimarySubtle : Brushes.Transparent;
            tab.BorderBrush = selected ? Theme.PrimaryBrush : Brushes.Transparent;
            tab.BorderThickness = new Thickness(0, 0, 0, selected ? 2 : 0);
            if (tab.Child is TextBlock tb)
            {
                tb.Foreground = selected ? Theme.TextPrimary : Theme.TextSecondary;
                tb.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Medium;
            }
        }

        // 进入图标包/替换区时刷新数据
        if (index == 1) RefreshPacks();
        if (index == 2) RefreshReplaceList();
    }

    // ============================================================
    //  配色面板
    // ============================================================

    /// <summary>构建图标配色面板</summary>
    private FrameworkElement BuildColorizePanel()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(16),
        };

        var stack = new StackPanel();

        // --- 模式选择 ---
        stack.Children.Add(MakeSectionLabel("着色模式"));
        var modePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        _modeRadios[0] = MakeRadioButton("关闭", "mode", ColorizeMode.Off);
        _modeRadios[1] = MakeRadioButton("单色着色", "mode", ColorizeMode.SingleColor);
        _modeRadios[2] = MakeRadioButton("匹配壁纸", "mode", ColorizeMode.MatchWallpaper);
        foreach (var rb in _modeRadios)
        {
            rb.Margin = new Thickness(0, 0, 16, 0);
            rb.Checked += (_, _) => OnSettingsChanged();
            modePanel.Children.Add(rb);
        }
        stack.Children.Add(WrapCard(modePanel));

        // --- 目标颜色 ---
        stack.Children.Add(MakeSectionLabel("目标颜色"));
        var colorPanel = new StackPanel();

        var swatchWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var hex in PresetColors)
        {
            swatchWrap.Children.Add(MakeSwatch(hex));
        }
        colorPanel.Children.Add(swatchWrap);

        var customBtn = CreateSecondaryButton("自定义颜色…");
        customBtn.Click += OnCustomColor;
        colorPanel.Children.Add(customBtn);

        stack.Children.Add(WrapCard(colorPanel));

        // --- 强度 ---
        stack.Children.Add(MakeSectionLabel("着色强度"));
        var strengthPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        _strengthLabel = new TextBlock
        {
            Text = "60%",
            FontFamily = Theme.MonoFont,
            FontSize = 12,
            Foreground = Theme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 44,
            TextAlignment = TextAlignment.Right,
        };
        DockPanel.SetDock(_strengthLabel, Dock.Right);
        strengthPanel.Children.Add(_strengthLabel);

        _strengthSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 60,
            TickFrequency = 5,
            IsSnapToTickEnabled = true,
            Cursor = Cursors.Hand,
        };
        _strengthSlider.ValueChanged += (_, e) =>
        {
            _strengthLabel.Text = $"{(int)e.NewValue}%";
        };
        _strengthSlider.MouseLeftButtonUp += (_, _) => RefreshPreview();
        strengthPanel.Children.Add(_strengthSlider);
        stack.Children.Add(WrapCard(strengthPanel));

        // --- 范围 ---
        stack.Children.Add(MakeSectionLabel("着色范围"));
        var scopePanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        _scopeRadios[0] = MakeRadioButton("全部", "scope", ColorizeScope.All);
        _scopeRadios[1] = MakeRadioButton("仅文件夹", "scope", ColorizeScope.Folders);
        _scopeRadios[2] = MakeRadioButton("仅应用", "scope", ColorizeScope.Apps);
        _scopeRadios[3] = MakeRadioButton("仅系统", "scope", ColorizeScope.System);
        foreach (var rb in _scopeRadios)
        {
            rb.Margin = new Thickness(0, 0, 16, 8);
            rb.Checked += (_, _) => OnSettingsChanged();
            scopePanel.Children.Add(rb);
        }
        stack.Children.Add(WrapCard(scopePanel));

        // --- Before/After 预览 ---
        stack.Children.Add(MakeSectionLabel("预览对比"));
        var previewGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var beforeCol = BuildPreviewColumn("原始", out _beforePanel);
        Grid.SetColumn(beforeCol, 0);
        var afterCol = BuildPreviewColumn("着色后", out _afterPanel);
        Grid.SetColumn(afterCol, 1);
        previewGrid.Children.Add(beforeCol);
        previewGrid.Children.Add(afterCol);
        stack.Children.Add(WrapCard(previewGrid));

        var refreshBtn = CreateSecondaryButton("刷新预览");
        refreshBtn.Margin = new Thickness(0, 0, 0, 16);
        refreshBtn.Click += (_, _) => RefreshPreview();
        stack.Children.Add(refreshBtn);

        // --- 操作栏 ---
        var actionPanel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };

        _statusText = new TextBlock
        {
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        DockPanel.SetDock(_statusText, Dock.Left);
        actionPanel.Children.Add(_statusText);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(btnPanel, Dock.Right);

        var resetBtn = CreateSecondaryButton("恢复默认");
        resetBtn.Margin = new Thickness(0, 0, 8, 0);
        resetBtn.Click += OnResetColorize;
        btnPanel.Children.Add(resetBtn);

        var applyBtn = CreatePrimaryButton("应用着色");
        applyBtn.Click += OnApplyColorize;
        btnPanel.Children.Add(applyBtn);
        actionPanel.Children.Add(btnPanel);

        stack.Children.Add(actionPanel);

        scroll.Content = stack;
        return scroll;
    }

    /// <summary>构建单个预览列（标题 + 图标堆叠）</summary>
    private UIElement BuildPreviewColumn(string title, out Panel iconPanel)
    {
        iconPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = Theme.UiFont,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        });
        content.Children.Add(iconPanel);

        return new Border
        {
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(10),
            Margin = new Thickness(4),
            Child = content,
        };
    }

    /// <summary>创建色板按钮</summary>
    private UIElement MakeSwatch(string hex)
    {
        var border = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = Theme.SmallRadius,
            Background = HexToBrush(hex),
            BorderBrush = Theme.InputBorder,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = Cursors.Hand,
            Tag = hex,
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            _selectedColor = hex;
            UpdateSwatchSelection();
            OnSettingsChanged();
        };
        _swatchBorders.Add(border);
        return border;
    }

    /// <summary>更新色板选中态边框</summary>
    private void UpdateSwatchSelection()
    {
        foreach (var b in _swatchBorders)
        {
            var hex = (string)b.Tag!;
            var selected = string.Equals(hex, _selectedColor, StringComparison.OrdinalIgnoreCase);
            b.BorderBrush = selected ? Theme.PrimaryBrush : Theme.InputBorder;
            b.BorderThickness = new Thickness(selected ? 2 : 1);
        }
    }

    /// <summary>自定义颜色按钮 — 弹出 WinForms ColorDialog</summary>
    private void OnCustomColor(object sender, RoutedEventArgs e)
    {
        try
        {
            using var dlg = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                Color = System.Drawing.Color.FromArgb(
                    HexToByte(_selectedColor, 0),
                    HexToByte(_selectedColor, 1),
                    HexToByte(_selectedColor, 2)),
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _selectedColor = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
                UpdateSwatchSelection();
                OnSettingsChanged();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 自定义颜色失败: {ex.Message}");
        }
    }

    /// <summary>取十六进制颜色中指定通道的字节（idx 0=R 1=G 2=B）</summary>
    private static byte HexToByte(string hex, int idx)
    {
        try
        {
            var h = hex.TrimStart('#');
            return Convert.ToByte(h.Substring(idx * 2 + 1, 2), 16);
        }
        catch
        {
            return 0xFF;
        }
    }

    /// <summary>配置变更触发预览刷新</summary>
    private void OnSettingsChanged()
    {
        RefreshPreview();
    }

    /// <summary>从 UI 状态构建配色配置</summary>
    private ColorizeConfig BuildConfigFromUi()
    {
        var config = new ColorizeConfig
        {
            Mode = SelectedMode(),
            TargetColor = _selectedColor,
            Strength = (int)_strengthSlider.Value,
            Scope = SelectedScope(),
        };
        return config;
    }

    /// <summary>获取当前选中的模式</summary>
    private ColorizeMode SelectedMode()
    {
        for (var i = 0; i < _modeRadios.Length; i++)
            if (_modeRadios[i].IsChecked == true)
                return (ColorizeMode)i;
        return ColorizeMode.Off;
    }

    /// <summary>获取当前选中的范围</summary>
    private ColorizeScope SelectedScope()
    {
        for (var i = 0; i < _scopeRadios.Length; i++)
            if (_scopeRadios[i].IsChecked == true)
                return (ColorizeScope)i;
        return ColorizeScope.All;
    }

    /// <summary>把已保存的配置加载到 UI 控件</summary>
    private void LoadConfigToUi()
    {
        var config = _service.LoadColorizeConfig();
        var modeIdx = (int)config.Mode;
        if (modeIdx >= 0 && modeIdx < _modeRadios.Length)
            _modeRadios[modeIdx].IsChecked = true;
        else
            _modeRadios[0].IsChecked = true;

        _selectedColor = string.IsNullOrEmpty(config.TargetColor) ? "#6C5CE7" : config.TargetColor;
        _strengthSlider.Value = Math.Clamp(config.Strength, 0, 100);
        _strengthLabel.Text = $"{(int)_strengthSlider.Value}%";

        var scopeIdx = (int)config.Scope;
        if (scopeIdx >= 0 && scopeIdx < _scopeRadios.Length)
            _scopeRadios[scopeIdx].IsChecked = true;
        else
            _scopeRadios[0].IsChecked = true;

        UpdateSwatchSelection();
    }

    /// <summary>刷新 Before/After 预览</summary>
    private void RefreshPreview()
    {
        if (_beforePanel == null || _afterPanel == null) return;
        _beforePanel.Children.Clear();
        _afterPanel.Children.Clear();

        if (_samples.Count == 0)
        {
            _beforePanel.Children.Add(MakePreviewPlaceholder("无可用图标"));
            _afterPanel.Children.Add(MakePreviewPlaceholder("无可用图标"));
            return;
        }

        var config = BuildConfigFromUi();
        foreach (var sample in _samples)
        {
            _beforePanel.Children.Add(MakePreviewImage(sample.Original));

            ImageSource? afterSrc;
            if (config.Mode == ColorizeMode.Off)
            {
                afterSrc = sample.Original;
            }
            else
            {
                var path = _service.ColorizeIconToCache(sample.SourcePath, config);
                afterSrc = IconManagerService.LoadImageSource(path) ?? sample.Original;
            }
            _afterPanel.Children.Add(MakePreviewImage(afterSrc));
        }
    }

    /// <summary>"应用着色"按钮</summary>
    private void OnApplyColorize(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = BuildConfigFromUi();
            var count = _service.ApplyColorize(config);
            _statusText.Text = config.Mode == ColorizeMode.Off
                ? "已关闭着色，恢复原始图标"
                : $"已应用着色（{count} 个图标，模式：{ModeText(config.Mode)}）";
            RefreshPreview();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"应用失败：{ex.Message}";
        }
    }

    /// <summary>"恢复默认"按钮 — 关闭着色并清空缓存</summary>
    private void OnResetColorize(object sender, RoutedEventArgs e)
    {
        try
        {
            _modeRadios[0].IsChecked = true;
            var config = BuildConfigFromUi();
            _service.ApplyColorize(config);
            _service.ClearCache();
            _statusText.Text = "已恢复默认图标";
            RefreshPreview();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"恢复失败：{ex.Message}";
        }
    }

    /// <summary>模式文本</summary>
    private static string ModeText(ColorizeMode mode) => mode switch
    {
        ColorizeMode.Off => "关闭",
        ColorizeMode.SingleColor => "单色着色",
        ColorizeMode.MatchWallpaper => "匹配壁纸",
        _ => mode.ToString(),
    };

    // ============================================================
    //  图标包面板
    // ============================================================

    /// <summary>构建图标包面板</summary>
    private FrameworkElement BuildPacksPanel()
    {
        var dock = new DockPanel();

        // 顶部工具栏
        var toolbar = new DockPanel { Margin = new Thickness(12) };
        var importBtn = CreateSecondaryButton("导入图标包");
        importBtn.Click += OnImportPack;
        DockPanel.SetDock(importBtn, Dock.Right);
        toolbar.Children.Add(importBtn);

        var title = new TextBlock
        {
            Text = "可用的图标包",
            FontFamily = Theme.UiFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        toolbar.Children.Add(title);

        var toolbarBorder = new Border
        {
            Child = toolbar,
            Background = Theme.HeaderBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        DockPanel.SetDock(toolbarBorder, Dock.Top);
        dock.Children.Add(toolbarBorder);

        // 包列表（滚动）
        _packsListPanel = new WrapPanel();
        var scroll = new ScrollViewer
        {
            Content = _packsListPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12),
        };
        dock.Children.Add(scroll);
        return dock;
    }

    /// <summary>刷新图标包列表（内置 + 已安装）</summary>
    private void RefreshPacks()
    {
        if (_packsListPanel == null) return;
        _packsListPanel.Children.Clear();

        var packs = new List<IconPack>();
        packs.AddRange(_service.GetBuiltinPacks());
        try { packs.AddRange(_service.GetInstalledPacks()); }
        catch (Exception ex) { Console.WriteLine($"[IconManager] 读取已安装包失败: {ex.Message}"); }

        foreach (var pack in packs)
        {
            _packsListPanel.Children.Add(BuildPackCard(pack));
        }

        if (packs.Count == 0)
        {
            _packsListPanel.Children.Add(MakePreviewPlaceholder("暂无图标包"));
        }
    }

    /// <summary>构建单个图标包卡片</summary>
    private UIElement BuildPackCard(IconPack pack)
    {
        var card = new Border
        {
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 12, 12),
            Width = 180,
        };

        var panel = new StackPanel();

        // 预览图（无则占位首字母）
        UIElement preview;
        if (!string.IsNullOrEmpty(pack.Preview) && File.Exists(pack.Preview))
        {
            preview = new System.Windows.Controls.Image
            {
                Source = IconManagerService.LoadImageSource(pack.Preview),
                Width = 64,
                Height = 64,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8),
            };
        }
        else
        {
            preview = new Border
            {
                Width = 64,
                Height = 64,
                CornerRadius = Theme.ControlRadius,
                Background = Theme.PrimarySubtle,
                Margin = new Thickness(0, 0, 0, 8),
                Child = new TextBlock
                {
                    Text = string.IsNullOrEmpty(pack.Name) ? "?" : pack.Name[..1].ToUpperInvariant(),
                    FontFamily = Theme.UiFont,
                    FontSize = 28,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Theme.TextPrimary,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }
        panel.Children.Add(preview);

        // 名称
        panel.Children.Add(new TextBlock
        {
            Text = pack.Name,
            FontFamily = Theme.UiFont,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // 描述
        panel.Children.Add(new TextBlock
        {
            Text = pack.Description,
            FontFamily = Theme.UiFont,
            FontSize = 10,
            Foreground = Theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 8),
            MaxHeight = 40,
        });

        // 需下载徽章 / 应用按钮
        if (pack.NeedsDownload)
        {
            panel.Children.Add(new Border
            {
                Background = Theme.ListItemMuted,
                CornerRadius = Theme.SmallRadius,
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "需下载",
                    FontFamily = Theme.UiFont,
                    FontSize = 10,
                    Foreground = Theme.TextSecondary,
                },
            });
        }
        else
        {
            var applyBtn = CreatePrimaryButton("应用");
            applyBtn.Width = 120;
            applyBtn.Click += (_, _) => OnApplyPack(pack);
            panel.Children.Add(applyBtn);
        }

        card.Child = panel;
        return card;
    }

    /// <summary>导入图标包 — 打开文件选择对话框</summary>
    private void OnImportPack(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择图标包 ZIP",
                Filter = "图标包 ZIP (*.zip)|*.zip|所有文件 (*.*)|*.*",
            };
            if (dlg.ShowDialog() == true)
            {
                var dest = _service.ImportPack(dlg.FileName);
                RefreshPacks();
                if (dest != null)
                    System.Windows.MessageBox.Show($"已导入图标包到：\n{dest}", "导入成功");
                else
                    System.Windows.MessageBox.Show("导入失败，请检查文件格式", "导入失败");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 导入图标包失败: {ex.Message}");
        }
    }

    /// <summary>应用图标包 — 按快捷方式名匹配包内 .ico 并替换</summary>
    private void OnApplyPack(IconPack pack)
    {
        try
        {
            if (pack.NeedsDownload)
            {
                System.Windows.MessageBox.Show($"图标包 {pack.Name} 需先下载后导入", "提示");
                return;
            }

            var count = 0;
            foreach (var entry in _service.GetDesktopShortcuts())
            {
                var name = Path.GetFileNameWithoutExtension(entry.SourcePath);
                var iconFile = Path.Combine(pack.Path, name + ".ico");
                if (File.Exists(iconFile) && _service.ReplaceIcon(entry.SourcePath, iconFile))
                    count++;
            }
            System.Windows.MessageBox.Show($"已应用图标包 {pack.Name}（替换 {count} 个图标）", "应用结果");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 应用图标包失败: {ex.Message}");
        }
    }

    // ============================================================
    //  图标替换面板
    // ============================================================

    /// <summary>构建图标替换面板</summary>
    private FrameworkElement BuildReplacePanel()
    {
        var dock = new DockPanel();

        // 顶部工具栏
        var toolbar = new DockPanel { Margin = new Thickness(12) };
        var refreshBtn = CreateSecondaryButton("刷新列表");
        refreshBtn.Click += (_, _) => RefreshReplaceList();
        DockPanel.SetDock(refreshBtn, Dock.Right);
        toolbar.Children.Add(refreshBtn);

        var title = new TextBlock
        {
            Text = "桌面快捷方式",
            FontFamily = Theme.UiFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        toolbar.Children.Add(title);

        var toolbarBorder = new Border
        {
            Child = toolbar,
            Background = Theme.HeaderBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        DockPanel.SetDock(toolbarBorder, Dock.Top);
        dock.Children.Add(toolbarBorder);

        _replaceListPanel = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = _replaceListPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12),
        };
        dock.Children.Add(scroll);
        return dock;
    }

    /// <summary>刷新桌面快捷方式列表</summary>
    private void RefreshReplaceList()
    {
        if (_replaceListPanel == null) return;
        _replaceListPanel.Children.Clear();

        List<DesktopShortcutEntry> entries;
        try { entries = _service.GetDesktopShortcuts(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 读取快捷方式失败: {ex.Message}");
            return;
        }

        if (entries.Count == 0)
        {
            _replaceListPanel.Children.Add(MakePreviewPlaceholder("桌面无快捷方式"));
            return;
        }

        foreach (var entry in entries)
        {
            _replaceListPanel.Children.Add(BuildReplaceRow(entry));
        }
    }

    /// <summary>构建单行快捷方式</summary>
    private UIElement BuildReplaceRow(DesktopShortcutEntry entry)
    {
        var row = new Border
        {
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var dock = new DockPanel { LastChildFill = true };

        // 图标
        var icon = new System.Windows.Controls.Image
        {
            Source = entry.Icon,
            Width = 32,
            Height = 32,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        if (entry.Icon == null)
        {
            icon.SetValue(System.Windows.Controls.Image.SourceProperty, null);
        }
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);

        // 右侧按钮组
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(btnPanel, Dock.Right);

        var resetBtn = CreateSecondaryButton("恢复");
        resetBtn.Margin = new Thickness(0, 0, 6, 0);
        resetBtn.Click += (_, _) =>
        {
            var ok = _service.ResetIcon(entry.SourcePath);
            System.Windows.MessageBox.Show(ok ? "已恢复默认图标" : "恢复失败", "提示");
            RefreshReplaceList();
        };
        btnPanel.Children.Add(resetBtn);

        var changeBtn = CreateSecondaryButton("更换");
        changeBtn.Click += (_, _) => OnChangeIcon(entry);
        btnPanel.Children.Add(changeBtn);
        dock.Children.Add(btnPanel);

        // 名称 + 路径
        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(new TextBlock
        {
            Text = entry.DisplayName,
            FontFamily = Theme.UiFont,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        center.Children.Add(new TextBlock
        {
            Text = entry.SourcePath,
            FontFamily = Theme.MonoFont,
            FontSize = 10,
            Foreground = Theme.TextSecondary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        });
        dock.Children.Add(center);

        row.Child = dock;
        return row;
    }

    /// <summary>"更换"图标 — 打开文件选择对话框</summary>
    private void OnChangeIcon(DesktopShortcutEntry entry)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"选择图标文件 — {entry.DisplayName}",
                Filter = "图标文件 (*.ico)|*.ico|程序 (*.exe)|*.exe|库 (*.dll)|*.dll|所有文件 (*.*)|*.*",
            };
            if (dlg.ShowDialog() == true)
            {
                var ok = _service.ReplaceIcon(entry.SourcePath, dlg.FileName);
                System.Windows.MessageBox.Show(ok ? "图标已更换" : "更换失败", "提示");
                RefreshReplaceList();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 更换图标失败: {ex.Message}");
        }
    }

    // ============================================================
    //  通用 UI 辅助
    // ============================================================

    /// <summary>创建分区小标题</summary>
    private static TextBlock MakeSectionLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(2, 10, 0, 8),
        };
    }

    /// <summary>把内容包装为统一卡片外观</summary>
    private static Border WrapCard(UIElement content)
    {
        return new Border
        {
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = content,
        };
    }

    /// <summary>创建单选按钮（统一主题样式）</summary>
    private static RadioButton MakeRadioButton(string label, string group, object tag)
    {
        return new RadioButton
        {
            Content = label,
            GroupName = group,
            Tag = tag,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            Foreground = Theme.TextRegular,
            Cursor = Cursors.Hand,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>创建预览图标 Image 控件</summary>
    private static UIElement MakePreviewImage(ImageSource? src)
    {
        var img = new System.Windows.Controls.Image
        {
            Source = src,
            Width = 44,
            Height = 44,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return img;
    }

    /// <summary>创建预览占位文本</summary>
    private static UIElement MakePreviewPlaceholder(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextFaint,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 16),
        };
    }

    /// <summary>十六进制 #RRGGBB → WPF SolidColorBrush（用户数据渲染）</summary>
    private static Brush HexToBrush(string hex)
    {
        try
        {
            var h = hex.TrimStart('#');
            if (h.Length == 6)
            {
                return new SolidColorBrush(Color.FromRgb(
                    Convert.ToByte(h.Substring(0, 2), 16),
                    Convert.ToByte(h.Substring(2, 2), 16),
                    Convert.ToByte(h.Substring(4, 2), 16)));
            }
        }
        catch { }
        return new SolidColorBrush(Theme.Primary);
    }
}
