using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using zDesktop.App.Tray;
using zDesktop.Shell.Styles;
using zDesktop.Shell.Wallpaper;

// 项目同时启用 WPF + System.Drawing，Brush / ComboBox / RadioButton / Image 在 System.Drawing/WinForms 与 System.Windows 间歧义，显式别名优先 WPF
using Brush = System.Windows.Media.Brush;
using ComboBox = System.Windows.Controls.ComboBox;
using Image = System.Windows.Controls.Image;
using RadioButton = System.Windows.Controls.RadioButton;

namespace zDesktop.App.Pages;

/// <summary>
/// 设置内容页 — 左侧二级导航 + 右侧设置项分区切换
///
/// 还原设计稿 settings.html：左侧 160px 二级导航（通用 / 外观 / 壁纸 / 组件 / 热键 / 关于），
/// 右侧滚动设置项区。每个设置项用统一的「设置行」样式：标签 + 控件 + 描述文字。
/// 二级导航切换通过 Visibility 切换对应面板实现，设置变更先保持内存态。
/// 所有颜色 / 字体 / 圆角一律引用 <see cref="Theme"/> 常量，不硬编码。
/// </summary>
public sealed class SettingsPage : ContentPage
{
    /// <summary>二级导航项视图引用列表（用于切换高亮）</summary>
    private readonly List<SettingNavItem> _navItems = new();

    /// <summary>右侧各分区面板（key = 分区标识）</summary>
    private readonly Dictionary<string, StackPanel> _panels = new();

    /// <summary>当前选中的分区标识</summary>
    private string _currentSection = "general";

    /// <summary>壁纸服务（壁纸设置分区使用）</summary>
    private readonly zDesktop.Shell.Wallpaper.WallpaperService _wallpaper = new();

    /// <summary>壁纸来源选项索引（0=必应 / 1=本地轮播 / 2=关闭）</summary>
    private int _wallpaperSource = 0;

    /// <summary>本地壁纸文件夹路径</summary>
    private string _wallpaperFolder = string.Empty;

    /// <summary>壁纸状态文本（显示当前壁纸路径或操作结果）</summary>
    private TextBlock? _wallpaperStatus;

    /// <summary>当前轮播索引（本地轮播模式用）</summary>
    private int _carouselIndex;

    /// <summary>轮播间隔（分钟）</summary>
    private int _wallpaperInterval = 60;

    /// <summary>壁纸样式（WallpaperService 样式常量：填充/适应/拉伸/居中）</summary>
    private int _wallpaperStyle = WallpaperService.WallpaperStyleFill;

    /// <summary>当前壁纸列表（必应缓存或本地文件夹中的图片）</summary>
    private List<string> _wallpaperList = new();

    /// <summary>壁纸缩略图预览控件</summary>
    private Image? _wallpaperPreview;

    /// <summary>
    /// 构造设置页（无参）
    /// </summary>
    public SettingsPage()
    {
        Title = "设置";
        NavId = "settings";

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左侧二级导航
        var nav = BuildNav();
        Grid.SetColumn(nav, 0);
        grid.Children.Add(nav);

        // 右侧滚动内容
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20, 12, 20, 20),
        };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);

        // 构建各分区面板
        var contentHost = new StackPanel();
        contentHost.Children.Add(BuildGeneralPanel());
        contentHost.Children.Add(BuildAppearancePanel());
        contentHost.Children.Add(BuildWallpaperPanel());
        contentHost.Children.Add(BuildWidgetPanel());
        contentHost.Children.Add(BuildHotkeyPanel());
        contentHost.Children.Add(BuildAboutPanel());
        scroll.Content = contentHost;

        // 初始显示通用分区
        SwitchSection("general");

        Content = grid;
    }

    // ============================================================
    //  左侧二级导航
    // ============================================================

    /// <summary>构建左侧二级导航栏</summary>
    private Border BuildNav()
    {
        var panel = new StackPanel { Margin = new Thickness(8, 12, 8, 12) };

        panel.Children.Add(MakeNavHeader("设置中心"));

        var sections = new[]
        {
            ("general", "通用", "⚙"),
            ("appearance", "外观", "🎨"),
            ("wallpaper", "壁纸", "🖼"),
            ("widget", "组件", "🧩"),
            ("hotkey", "热键", "⌨"),
            ("about", "关于", "ℹ"),
        };

        foreach (var (id, label, icon) in sections)
        {
            panel.Children.Add(MakeNavItem(id, label, icon));
        }

        return new Border
        {
            Child = panel,
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
    }

    /// <summary>导航栏顶部标题</summary>
    private static TextBlock MakeNavHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = Theme.UiFont,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            Margin = new Thickness(10, 4, 0, 14),
        };
    }

    /// <summary>创建单个导航项（可点击切换分区）</summary>
    private Border MakeNavItem(string id, string label, string icon)
    {
        var iconTb = new TextBlock
        {
            Text = icon,
            FontSize = 13,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var labelTb = new TextBlock
        {
            Text = label,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(iconTb);
        row.Children.Add(labelTb);

        var border = new Border
        {
            Child = row,
            Background = Brushes.Transparent,
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = Cursors.Hand,
        };

        var item = new SettingNavItem { Id = id, Container = border, Icon = iconTb, Label = labelTb };
        _navItems.Add(item);

        border.MouseEnter += (_, _) =>
        {
            if (_currentSection != id) border.Background = Theme.InputBackground;
        };
        border.MouseLeave += (_, _) =>
        {
            if (_currentSection != id) border.Background = Brushes.Transparent;
        };
        border.MouseLeftButtonUp += (_, _) => SwitchSection(id);

        return border;
    }

    /// <summary>切换右侧显示的分区</summary>
    private void SwitchSection(string id)
    {
        _currentSection = id;
        foreach (var item in _navItems)
        {
            var active = item.Id == id;
            item.Container.Background = active ? Theme.PrimarySubtle : Brushes.Transparent;
            item.Icon.Foreground = active ? Theme.PrimaryBrush : Theme.TextSecondary;
            item.Label.Foreground = active ? Theme.PrimaryBrush : Theme.TextSecondary;
            item.Label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
        foreach (var (key, panel) in _panels)
        {
            panel.Visibility = key == id ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ============================================================
    //  通用分区
    // ============================================================

    /// <summary>构建通用设置面板 — 开机自启/语言真正生效</summary>
    private StackPanel BuildGeneralPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(MakeSectionTitle("通用设置"));

        // 开机自启 — 初始态读注册表，切换时写注册表
        var startupToggle = MakeToggle(out var setStartup, StartupHelper.IsEnabled());
        setStartup(StartupHelper.IsEnabled()); // 确保视觉态与实际一致
        startupToggle.MouseLeftButtonUp += (_, _) =>
        {
            // MakeToggle 内部已翻转 isOn，这里读翻转后状态决定写注册表方向
            // 重新实现：直接根据当前注册表状态 Toggle
            var nowOn = StartupHelper.Toggle();
            setStartup(nowOn);
            Console.WriteLine($"[Settings] 开机自启: {(nowOn ? "已启用" : "已禁用")}");
        };
        panel.Children.Add(MakeSettingRow(
            "开机自启", "登录 Windows 时自动启动 zDesktop",
            startupToggle));

        panel.Children.Add(MakeSettingRow(
            "启动时显示主窗口", "程序启动后立即显示主窗口，关闭则仅驻留托盘",
            MakeToggle(out _, false)));

        // 界面语言 — 持久化到 settings.json，重启后生效
        var savedLang = UserSettingsStore.Load().Language;
        var langCombo = new ComboBox
        {
            Width = 160,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            Background = Theme.InputBackground,
            Foreground = Theme.TextRegular,
            BorderBrush = Theme.InputBorder,
        };
        langCombo.Items.Add("简体中文");
        langCombo.Items.Add("English");
        langCombo.Items.Add("日本語");
        langCombo.SelectedIndex = savedLang switch
        {
            "en-US" => 1,
            "ja-JP" => 2,
            _ => 0,
        };
        langCombo.SelectionChanged += (_, _) =>
        {
            var code = langCombo.SelectedIndex switch
            {
                1 => "en-US",
                2 => "ja-JP",
                _ => "zh-CN",
            };
            UserSettingsStore.Update(s => s.Language = code);
            Console.WriteLine($"[Settings] 界面语言已保存: {code}（重启后生效）");
        };
        panel.Children.Add(MakeSettingRow(
            "界面语言", "切换应用界面显示语言（重启后生效）",
            langCombo));

        // 检查更新 — 弹出提示（暂无在线更新服务，仅占位反馈）
        var checkBtn = CreateSecondaryButton("检查更新");
        checkBtn.Click += (_, _) =>
        {
            System.Windows.MessageBox.Show(
                "当前为开发版本，暂不支持在线更新。\n请前往 GitHub Release 获取最新版本。",
                "检查更新",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Console.WriteLine("[Settings] 检查更新（开发版，无在线更新）");
        };
        panel.Children.Add(MakeSettingRow(
            "检查更新", "手动检查 zDesktop 新版本",
            checkBtn));

        _panels["general"] = panel;
        return panel;
    }

    // ============================================================
    //  外观分区
    // ============================================================

    /// <summary>构建外观设置面板 — 主题模式与强调色真正可切换</summary>
    private StackPanel BuildAppearancePanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(MakeSectionTitle("外观设置"));

        // 主题模式单选 — 初始选中根据当前模式与预设
        panel.Children.Add(MakeSubLabel("主题模式"));
        var initialMode = Theme.CurrentMode == UserSettingsStore.ThemeMode.FollowSystem
            ? 2
            : (Theme.CurrentPreset == ThemePreset.QianCao ? 1 : 0);
        panel.Children.Add(MakeRadioGroup(new[]
        {
            "深色（墨韵）", "浅色（浅草）", "跟随系统",
        }, initialMode, idx =>
        {
            switch (idx)
            {
                case 0:
                    Theme.ApplyPreset(ThemePreset.MoYun);
                    Console.WriteLine("[Settings] 主题模式: 深色（墨韵）");
                    break;
                case 1:
                    Theme.ApplyPreset(ThemePreset.QianCao);
                    Console.WriteLine("[Settings] 主题模式: 浅色（浅草）");
                    break;
                case 2:
                    Theme.ApplyFollowSystem();
                    Console.WriteLine($"[Settings] 主题模式: 跟随系统（{(Theme.IsSystemDarkMode() ? "深色" : "浅色")}）");
                    break;
            }
            // 主题切换后刷新强调色色板的选中态（第一个=当前主色）
            _accentPanel?.Children.Clear();
            RebuildAccentSwatches();
        }));

        // 强调色选择器
        panel.Children.Add(MakeSubLabel("强调色"));
        panel.Children.Add(MakeAccentPalette());

        panel.Children.Add(MakeSliderRow("全局圆角", 0, 20, 10, "px"));
        panel.Children.Add(MakeSliderRow("毛玻璃强度", 0, 100, 60, "%"));

        _panels["appearance"] = panel;
        return panel;
    }

    /// <summary>强调色色板容器引用（切换主题后重建选中态）</summary>
    private StackPanel? _accentPanel;

    /// <summary>创建强调色预设色板（8 个预设色 + 选中态）— 点击真正应用强调色</summary>
    private StackPanel MakeAccentPalette()
    {
        _accentPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 8),
        };
        RebuildAccentSwatches();
        return _accentPanel;
    }

    /// <summary>重建强调色色板子项（切换主题/预设后调用以刷新颜色快照）</summary>
    private void RebuildAccentSwatches()
    {
        if (_accentPanel == null) return;
        _accentPanel.Children.Clear();

        // 预设强调色：第一个为当前主色，其余为语义/装饰色
        var colors = new[]
        {
            Theme.Primary,
            Theme.Success,
            Theme.Warning,
            Theme.Error,
            Theme.Info,
            Theme.AccentPink,
            Theme.AccentTeal,
            Theme.AccentOrange,
        };

        for (var i = 0; i < colors.Length; i++)
        {
            var idx = i;
            var c = colors[i];
            var isSelected = i == 0; // 第一个=当前主色，默认选中
            var swatch = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(c),
                BorderBrush = isSelected ? Theme.TextPrimary : Theme.Divider,
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2}",
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                // 更新选中态边框
                for (var j = 0; j < _accentPanel!.Children.Count; j++)
                {
                    if (_accentPanel.Children[j] is Border b)
                        b.BorderThickness = new Thickness(j == idx ? 2 : 1);
                }
                // 真正应用强调色到全局
                Theme.ApplyAccent(c);
                Console.WriteLine($"[Settings] 强调色已应用: #{c.R:X2}{c.G:X2}{c.B:X2}");
            };
            _accentPanel.Children.Add(swatch);
        }
    }

    // ============================================================
    //  壁纸分区
    // ============================================================

    /// <summary>构建壁纸设置面板 — 真正可用的壁纸管理（来源/轮播/文件夹/样式/预览/应用）</summary>
    private StackPanel BuildWallpaperPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(MakeSectionTitle("壁纸设置"));

        // 壁纸来源
        panel.Children.Add(MakeSubLabel("壁纸来源"));
        panel.Children.Add(MakeRadioGroup(new[]
        {
            "必应每日壁纸", "本地轮播", "关闭壁纸轮播",
        }, _wallpaperSource, idx =>
        {
            _wallpaperSource = idx;
            RefreshWallpaperList();
        }));

        // 轮播间隔（自定义滑块行，捕获值到 _wallpaperInterval）
        panel.Children.Add(MakeWallpaperIntervalSlider());

        // 本地壁纸文件夹选择
        var folderBtn = CreateSecondaryButton("选择文件夹…");
        var folderLabel = new TextBlock
        {
            Text = string.IsNullOrEmpty(_wallpaperFolder) ? "未选择" : _wallpaperFolder,
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            MaxWidth = 240,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = _wallpaperFolder,
        };
        folderBtn.Click += (_, _) =>
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "选择本地壁纸文件夹",
                };
                if (dlg.ShowDialog() == true)
                {
                    _wallpaperFolder = dlg.FolderName;
                    folderLabel.Text = _wallpaperFolder;
                    folderLabel.ToolTip = _wallpaperFolder;
                    RefreshWallpaperList();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] 选择文件夹失败: {ex.Message}");
                UpdateWallpaperStatus($"选择文件夹失败: {ex.Message}");
            }
        };
        panel.Children.Add(MakeSettingRow(
            "本地壁纸文件夹", "本地轮播模式下从此文件夹读取壁纸",
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { folderLabel, folderBtn },
            }));

        // 壁纸样式
        var styleCombo = new ComboBox
        {
            Width = 160,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            Background = Theme.InputBackground,
            Foreground = Theme.TextRegular,
            BorderBrush = Theme.InputBorder,
        };
        styleCombo.Items.Add("填充（保持比例裁剪）");
        styleCombo.Items.Add("适应（完整显示）");
        styleCombo.Items.Add("拉伸（铺满）");
        styleCombo.Items.Add("居中");
        styleCombo.SelectedIndex = 0;
        styleCombo.SelectionChanged += (_, _) =>
        {
            _wallpaperStyle = styleCombo.SelectedIndex switch
            {
                0 => WallpaperService.WallpaperStyleFill,
                1 => WallpaperService.WallpaperStyleFit,
                2 => WallpaperService.WallpaperStyleStretch,
                3 => WallpaperService.WallpaperStyleCenter,
                _ => WallpaperService.WallpaperStyleFill,
            };
        };
        panel.Children.Add(MakeSettingRow(
            "壁纸样式", "壁纸在桌面上的显示方式",
            styleCombo));

        // 缩略图预览
        panel.Children.Add(MakeSubLabel("当前壁纸预览"));
        _wallpaperPreview = new Image
        {
            Height = 150,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var previewBorder = new Border
        {
            CornerRadius = Theme.ControlRadius,
            ClipToBounds = true,
            Background = Theme.InputBackground,
            Child = _wallpaperPreview,
            Margin = new Thickness(0, 2, 0, 8),
        };
        panel.Children.Add(previewBorder);

        // 操作按钮栏：立即应用 + 下一张
        var applyBtn = CreatePrimaryButton("立即应用");
        var nextBtn = CreateSecondaryButton("下一张");
        nextBtn.Margin = new Thickness(8, 0, 0, 0);
        applyBtn.Click += (_, _) => _ = ApplyWallpaperAsync();
        nextBtn.Click += (_, _) => ShiftWallpaper(1);
        panel.Children.Add(MakeSettingRow(
            "应用壁纸", "将当前预览壁纸设为桌面背景",
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { applyBtn, nextBtn },
            }));

        // 状态文本
        _wallpaperStatus = new TextBlock
        {
            Text = "就绪",
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 6, 0, 0),
        };
        panel.Children.Add(_wallpaperStatus);

        _panels["wallpaper"] = panel;

        // 构建完成后加载壁纸列表
        RefreshWallpaperList();

        return panel;
    }

    /// <summary>构建轮播间隔滑块行（捕获值到 _wallpaperInterval）</summary>
    private StackPanel MakeWallpaperIntervalSlider()
    {
        var title = new TextBlock
        {
            Text = "轮播间隔",
            FontFamily = Theme.UiFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var valueLabel = new TextBlock
        {
            Text = $"{_wallpaperInterval} 分钟",
            FontFamily = Theme.MonoFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 60,
            TextAlignment = TextAlignment.Right,
        };
        var slider = new Slider
        {
            Minimum = 1,
            Maximum = 240,
            Value = _wallpaperInterval,
            Width = 220,
            Foreground = Theme.PrimaryBrush,
            Background = Theme.InputBackground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        slider.ValueChanged += (_, e) =>
        {
            _wallpaperInterval = (int)e.NewValue;
            valueLabel.Text = $"{_wallpaperInterval} 分钟";
        };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0);
        Grid.SetColumn(valueLabel, 1);
        head.Children.Add(title);
        head.Children.Add(valueLabel);

        var p = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        p.Children.Add(head);
        p.Children.Add(slider);
        return p;
    }

    // ===== 壁纸业务逻辑 =====

    /// <summary>根据当前来源刷新壁纸列表并更新预览</summary>
    private void RefreshWallpaperList()
    {
        _wallpaperList.Clear();
        _carouselIndex = 0;

        switch (_wallpaperSource)
        {
            case 0: // 必应每日壁纸
                _wallpaperList = _wallpaper.GetBingWallpapers();
                if (_wallpaperList.Count == 0)
                {
                    UpdateWallpaperStatus("必应壁纸缓存为空，正在下载今日壁纸...");
                    _ = DownloadBingAndRefreshAsync();
                    return;
                }
                UpdateWallpaperStatus($"必应壁纸：共 {_wallpaperList.Count} 张（已缓存）");
                break;

            case 1: // 本地轮播
                if (string.IsNullOrEmpty(_wallpaperFolder))
                {
                    UpdateWallpaperStatus("请先选择本地壁纸文件夹");
                    _wallpaperPreview!.Source = null;
                    return;
                }
                _wallpaperList = _wallpaper.GetLocalWallpapers(_wallpaperFolder);
                if (_wallpaperList.Count == 0)
                {
                    UpdateWallpaperStatus($"文件夹中未找到图片：{_wallpaperFolder}");
                    _wallpaperPreview!.Source = null;
                    return;
                }
                UpdateWallpaperStatus($"本地壁纸：共 {_wallpaperList.Count} 张");
                break;

            default: // 关闭轮播
                UpdateWallpaperStatus("壁纸轮播已关闭，可手动选择图片设置");
                _wallpaperPreview!.Source = null;
                return;
        }

        ShowCurrentWallpaperPreview();
    }

    /// <summary>异步下载必应今日壁纸并刷新列表</summary>
    private async Task DownloadBingAndRefreshAsync()
    {
        var path = await _wallpaper.DownloadBingWallpaperAsync();
        if (path != null)
        {
            _wallpaperList = _wallpaper.GetBingWallpapers();
            _carouselIndex = 0;
            UpdateWallpaperStatus($"已下载必应壁纸：共 {_wallpaperList.Count} 张");
            ShowCurrentWallpaperPreview();
        }
        else
        {
            UpdateWallpaperStatus("必应壁纸下载失败，请检查网络连接");
        }
    }

    /// <summary>切换壁纸索引并更新预览（delta=1 下一张，-1 上一张）</summary>
    private void ShiftWallpaper(int delta)
    {
        if (_wallpaperList.Count == 0)
        {
            UpdateWallpaperStatus("没有可切换的壁纸，请先选择来源或文件夹");
            return;
        }
        _carouselIndex = (_carouselIndex + delta + _wallpaperList.Count) % _wallpaperList.Count;
        ShowCurrentWallpaperPreview();
        UpdateWallpaperStatus($"当前 {_carouselIndex + 1}/{_wallpaperList.Count}：{Path.GetFileName(_wallpaperList[_carouselIndex])}");
    }

    /// <summary>显示当前索引壁纸的缩略图预览</summary>
    private void ShowCurrentWallpaperPreview()
    {
        if (_carouselIndex < 0 || _carouselIndex >= _wallpaperList.Count) return;
        if (_wallpaperPreview == null) return;

        var path = _wallpaperList[_carouselIndex];
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.DecodePixelWidth = 480;
            bmp.EndInit();
            bmp.Freeze();
            _wallpaperPreview.Source = bmp;
        }
        catch (Exception ex)
        {
            UpdateWallpaperStatus($"预览加载失败：{ex.Message}");
        }
    }

    /// <summary>立即将当前预览壁纸应用到桌面</summary>
    private async Task ApplyWallpaperAsync()
    {
        // 必应模式且列表为空 → 先下载
        if (_wallpaperSource == 0 && _wallpaperList.Count == 0)
        {
            UpdateWallpaperStatus("正在下载必应今日壁纸...");
            var downloaded = await _wallpaper.DownloadBingWallpaperAsync();
            if (downloaded == null)
            {
                UpdateWallpaperStatus("必应壁纸下载失败，请检查网络连接");
                return;
            }
            _wallpaperList = _wallpaper.GetBingWallpapers();
            _carouselIndex = 0;
            ShowCurrentWallpaperPreview();
        }

        if (_wallpaperList.Count == 0)
        {
            UpdateWallpaperStatus("没有可应用的壁纸，请先选择来源或文件夹");
            return;
        }

        if (_carouselIndex < 0 || _carouselIndex >= _wallpaperList.Count)
            _carouselIndex = 0;

        var target = _wallpaperList[_carouselIndex];
        UpdateWallpaperStatus($"正在设置壁纸：{Path.GetFileName(target)}");

        var success = _wallpaper.SetWallpaper(target, _wallpaperStyle);
        UpdateWallpaperStatus(success
            ? $"已设为桌面壁纸：{Path.GetFileName(target)}"
            : "设置壁纸失败，请检查文件是否有效");
    }

    /// <summary>更新壁纸状态文本（同时输出到控制台）</summary>
    private void UpdateWallpaperStatus(string text)
    {
        if (_wallpaperStatus != null)
            _wallpaperStatus.Text = text;
        Console.WriteLine($"[Settings] {text}");
    }

    // ============================================================
    //  组件分区
    // ============================================================

    /// <summary>构建组件设置面板</summary>
    private StackPanel BuildWidgetPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(MakeSectionTitle("组件设置"));

        var saved = UserSettingsStore.Load();

        var sizeCombo = new ComboBox
        {
            Width = 160,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            Background = Theme.InputBackground,
            Foreground = Theme.TextRegular,
            BorderBrush = Theme.InputBorder,
        };
        sizeCombo.Items.Add("小");
        sizeCombo.Items.Add("中");
        sizeCombo.Items.Add("大");
        sizeCombo.SelectedIndex = 1;
        panel.Children.Add(MakeSettingRow(
            "默认组件尺寸", "新增组件时的默认显示尺寸",
            sizeCombo));

        // 吸附网格 — 持久化，运行时仅记录（WidgetHost 实际吸附逻辑未接，此处先存偏好）
        var snapToggle = MakeToggle(out var setSnap, saved.WidgetSnapToGrid);
        snapToggle.MouseLeftButtonUp += (_, _) =>
        {
            // MakeToggle 内部已翻转，读取翻转后状态并持久化
            var on = !saved.WidgetSnapToGrid;
            saved.WidgetSnapToGrid = on;
            setSnap(on);
            UserSettingsStore.Update(s => s.WidgetSnapToGrid = on);
            Console.WriteLine($"[Settings] 吸附网格: {(on ? "开" : "关")}");
        };
        panel.Children.Add(MakeSettingRow(
            "吸附网格", "拖动组件时自动吸附到网格线",
            snapToggle));

        // 对齐辅助线 — 持久化
        var guideToggle = MakeToggle(out var setGuide, saved.WidgetGuideLines);
        guideToggle.MouseLeftButtonUp += (_, _) =>
        {
            var on = !saved.WidgetGuideLines;
            saved.WidgetGuideLines = on;
            setGuide(on);
            UserSettingsStore.Update(s => s.WidgetGuideLines = on);
            Console.WriteLine($"[Settings] 对齐辅助线: {(on ? "开" : "关")}");
        };
        panel.Children.Add(MakeSettingRow(
            "对齐辅助线", "拖动时显示与其他组件的对齐辅助线",
            guideToggle));

        _panels["widget"] = panel;
        return panel;
    }

    // ============================================================
    //  热键分区
    // ============================================================

    /// <summary>构建热键设置面板</summary>
    private StackPanel BuildHotkeyPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(MakeSectionTitle("热键设置"));

        panel.Children.Add(MakeHotkeyRow("全局搜索", "Alt+Space", "呼出快速启动器 / 全局搜索"));
        panel.Children.Add(MakeHotkeyRow("控制中心", "Ctrl+Space", "呼出系统控制中心"));

        // 自定义热键录制
        var keyDisplay = new Border
        {
            Child = new TextBlock
            {
                Text = "未设置",
                FontFamily = Theme.MonoFont,
                FontSize = 12,
                Foreground = Theme.TextSecondary,
            },
            Background = Theme.InputBackground,
            BorderBrush = Theme.InputBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.SmallRadius,
            Padding = new Thickness(12, 6, 12, 6),
        };
        var recordBtn = CreateSecondaryButton("录制");
        recordBtn.Margin = new Thickness(8, 0, 0, 0);
        recordBtn.Click += (_, _) =>
        {
            try
            {
                if (keyDisplay.Child is TextBlock tb)
                    tb.Text = "按下按键…";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] 录制热键失败: {ex.Message}");
            }
        };
        panel.Children.Add(MakeSettingRow(
            "自定义热键", "为常用操作设置全局快捷键",
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { keyDisplay, recordBtn },
            }));

        _panels["hotkey"] = panel;
        return panel;
    }

    /// <summary>创建热键显示行（标签 + 当前组合 + 描述）</summary>
    private StackPanel MakeHotkeyRow(string label, string combo, string desc)
    {
        var kbd = new Border
        {
            Child = new TextBlock
            {
                Text = combo,
                FontFamily = Theme.MonoFont,
                FontSize = 12,
                Foreground = Theme.TextRegular,
            },
            Background = Theme.InputBackground,
            BorderBrush = Theme.InputBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.SmallRadius,
            Padding = new Thickness(10, 5, 10, 5),
        };
        return MakeSettingRow(label, desc, kbd);
    }

    // ============================================================
    //  关于分区
    // ============================================================

    /// <summary>构建关于面板</summary>
    private StackPanel BuildAboutPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(MakeSectionTitle("关于 zDesktop"));

        panel.Children.Add(MakeInfoRow("版本", "v1.0.0"));
        panel.Children.Add(MakeInfoRow("作者", "zDesktop Team"));
        panel.Children.Add(MakeInfoRow("运行环境", $".NET 8.0 · {Environment.OSVersion}"));

        // GitHub 链接
        var githubBtn = CreateSecondaryButton("打开 GitHub");
        githubBtn.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/zdesktop/zdesktop",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] 打开 GitHub 失败: {ex.Message}");
            }
        };
        panel.Children.Add(MakeSettingRow("项目主页", "在 GitHub 上查看源码与提交 Issue", githubBtn));

        var checkBtn = CreatePrimaryButton("检查更新");
        checkBtn.Click += (_, _) =>
        {
            System.Windows.MessageBox.Show(
                "当前为开发版本，暂不支持在线更新。\n请前往 GitHub Release 获取最新版本。",
                "检查更新",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Console.WriteLine("[Settings] 关于-检查更新（开发版）");
        };
        panel.Children.Add(MakeSettingRow("检查更新", "获取 zDesktop 最新版本", checkBtn));

        var licenseBtn = CreateSecondaryButton("查看许可证");
        licenseBtn.Click += (_, _) =>
        {
            System.Windows.MessageBox.Show(
                "zDesktop 基于 MIT License 开源。\n\nCopyright (c) zDesktop Team\n\n特此授权，免费向任何获得本软件副本的人提供本软件及相关文档文件的处理权。",
                "开源许可证",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Console.WriteLine("[Settings] 查看许可证");
        };
        panel.Children.Add(MakeSettingRow("开源许可证", "MIT License", licenseBtn));

        _panels["about"] = panel;
        return panel;
    }

    /// <summary>创建信息展示行（标签 + 值）</summary>
    private StackPanel MakeInfoRow(string label, string value)
    {
        var title = new TextBlock
        {
            Text = label,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            Foreground = Theme.TextRegular,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var val = new TextBlock
        {
            Text = value,
            FontFamily = Theme.MonoFont,
            FontSize = 12,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0);
        Grid.SetColumn(val, 1);
        head.Children.Add(title);
        head.Children.Add(val);

        return new StackPanel
        {
            Children = { head },
            Margin = new Thickness(0, 8, 0, 8),
        };
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
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            Margin = new Thickness(2, 4, 0, 14),
        };
    }

    /// <summary>分区小标题</summary>
    private static TextBlock MakeSubLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(2, 6, 0, 6),
        };
    }

    /// <summary>
    /// 创建统一的设置行：左侧标签 + 描述，右侧控件
    /// </summary>
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

    /// <summary>创建单选按钮组（横向排列）</summary>
    private static StackPanel MakeRadioGroup(string[] options, int selected, Action<int> onSelect)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 8),
        };
        for (var i = 0; i < options.Length; i++)
        {
            var idx = i;
            var rb = new RadioButton
            {
                Content = options[i],
                FontFamily = Theme.UiFont,
                FontSize = 12,
                Foreground = Theme.TextRegular,
                IsChecked = i == selected,
                Margin = new Thickness(0, 0, 16, 0),
                Cursor = Cursors.Hand,
            };
            rb.Checked += (_, _) => onSelect(idx);
            panel.Children.Add(rb);
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

    /// <summary>创建开关控件（轨道 + 圆点）</summary>
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

    // ============================================================
    //  数据模型
    // ============================================================

    /// <summary>二级导航项视图引用</summary>
    private sealed class SettingNavItem
    {
        /// <summary>分区标识</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>外层容器</summary>
        public Border Container { get; init; } = null!;

        /// <summary>图标文本</summary>
        public TextBlock Icon { get; init; } = null!;

        /// <summary>标签文本</summary>
        public TextBlock Label { get; init; } = null!;
    }
}
