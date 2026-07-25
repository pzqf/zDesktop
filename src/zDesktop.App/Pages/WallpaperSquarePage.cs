using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using zDesktop.Shell.Styles;
using zDesktop.Shell.Wallpaper;

// 项目同时启用 WPF + System.Drawing，Brush / Point / Image 在 System.Drawing 与 System.Windows 间歧义，显式别名优先 WPF
using Brush = System.Windows.Media.Brush;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;

namespace zDesktop.App.Pages;

/// <summary>
/// 壁纸广场内容页 — 分类标签 + 搜索 + 壁纸卡片网格 + 当前壁纸信息卡
///
/// 还原设计稿 wallpaper-square.html：顶部分类胶囊栏 + 搜索框，主体 WrapPanel 壁纸卡片
/// （缩略图 + 标题 + 分辨率 + 收藏 / 设为壁纸），底部当前壁纸信息卡。
/// 壁纸来源：扫描内置 assets/wallpapers 与 %APPDATA%\zDesktop\wallpapers 目录；
/// 无壁纸时回退为占位渐变色块（示例数据）。「设为壁纸」调用 WallpaperService.SetWallpaper。
/// 所有颜色 / 字体 / 圆角一律引用 <see cref="Theme"/> 常量，不硬编码。
/// </summary>
public sealed class WallpaperSquarePage : ContentPage
{
    /// <summary>壁纸服务（用于「设为壁纸」写入系统）</summary>
    private readonly WallpaperService _wallpaper = new();

    /// <summary>全部壁纸条目（主数据）</summary>
    private readonly List<WallpaperEntry> _all = new();

    /// <summary>当前选中的分类（"全部" 表示不筛选）</summary>
    private string _category = "全部";

    /// <summary>当前搜索关键字</summary>
    private string _query = string.Empty;

    /// <summary>壁纸卡片网格宿主</summary>
    private WrapPanel? _grid;

    /// <summary>分类胶囊栏宿主</summary>
    private StackPanel? _pills;

    /// <summary>当前壁纸缩略图 Border</summary>
    private Border? _currentThumb;

    /// <summary>当前壁纸名称文本</summary>
    private TextBlock? _currentName;

    /// <summary>当前壁纸分辨率文本</summary>
    private TextBlock? _currentRes;

    /// <summary>扫描结果状态文本（显示壁纸数量 / 可应用数 / 提示）</summary>
    private TextBlock? _statusText;

    /// <summary>分类列表（顺序即设计稿顺序，末尾「本地」覆盖真实扫描的壁纸）</summary>
    private static readonly string[] Categories =
        { "全部", "风景", "动漫", "科幻", "游戏", "简约", "动态", "本地" };

    /// <summary>
    /// 构造壁纸广场页（无参，壁纸目录自行扫描）
    /// </summary>
    public WallpaperSquarePage()
    {
        Title = "壁纸广场";
        NavId = "wallpaper-square";

        LoadWallpapers();

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20, 12, 20, 20),
        };

        var root = new StackPanel();
        root.Children.Add(BuildTopBar());
        root.Children.Add(BuildCategoryPills());
        root.Children.Add(BuildStatusBar());
        root.Children.Add(BuildGrid());
        root.Children.Add(BuildCurrentWallpaperCard());

        scroll.Content = root;
        Content = scroll;
    }

    // ============================================================
    //  数据加载
    // ============================================================

    /// <summary>扫描内置壁纸目录与用户壁纸目录（含必应缓存），无壁纸则生成占位示例</summary>
    private void LoadWallpapers()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");
        // 纳入必应壁纸缓存目录（设置页下载的必应每日壁纸存于此）
        var scanDirs = new (string dir, string source)[]
        {
            (Path.Combine(AppContext.BaseDirectory, "assets", "wallpapers"), "内置"),
            (Path.Combine(appData, "bing-wallpapers"), "必应"),
            (Path.Combine(appData, "wallpapers"), "本地"),
        };

        var extensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
        var found = 0;
        foreach (var (dir, source) in scanDirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    if (!extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                    var size = TryGetSize(file, out var w, out var h) ? $"{w} x {h}" : source;
                    _all.Add(new WallpaperEntry
                    {
                        Title = Path.GetFileNameWithoutExtension(file),
                        Resolution = size,
                        Source = source,
                        // 必应壁纸多为风景，归入「风景」分类；其他真实壁纸归入「本地」
                        Category = source == "必应" ? "风景" : "本地",
                        Path = file,
                    });
                    found++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WallpaperSquare] 扫描目录 {dir} 失败: {ex.Message}");
            }
        }

        if (found == 0)
        {
            // 无壁纸时回退占位示例（渐变色块，无真实文件不可应用）
            _all.AddRange(BuildPlaceholderSamples());
        }

        Console.WriteLine($"[WallpaperSquare] 扫描完成：{found} 张真实壁纸" + (found == 0 ? "（使用占位示例）" : ""));
    }

    /// <summary>占位示例壁纸（无真实文件，仅用于展示网格效果）</summary>
    private static IEnumerable<WallpaperEntry> BuildPlaceholderSamples()
    {
        yield return new WallpaperEntry
        {
            Title = "山湖暮色", Resolution = "2048 x 2048", Source = "示例", Category = "风景",
            GradientFrom = Theme.Info, GradientTo = Theme.Success,
        };
        yield return new WallpaperEntry
        {
            Title = "几何幻境", Resolution = "2048 x 2048", Source = "示例", Category = "科幻",
            GradientFrom = Theme.Primary, GradientTo = Theme.Muted,
        };
        yield return new WallpaperEntry
        {
            Title = "城市星河", Resolution = "3840 x 2160", Source = "示例", Category = "风景",
            GradientFrom = Theme.Background, GradientTo = Theme.Primary,
        };
        yield return new WallpaperEntry
        {
            Title = "粉彩梦境", Resolution = "2048 x 2048", Source = "示例", Category = "动漫",
            GradientFrom = Theme.Warning, GradientTo = Theme.Primary,
        };
        yield return new WallpaperEntry
        {
            Title = "海岸落日", Resolution = "2560 x 1440", Source = "示例", Category = "风景",
            GradientFrom = Theme.Warning, GradientTo = Theme.Error,
        };
        yield return new WallpaperEntry
        {
            Title = "迷雾森林", Resolution = "2048 x 2048", Source = "示例", Category = "风景",
            GradientFrom = Theme.Success, GradientTo = Theme.Background,
        };
        yield return new WallpaperEntry
        {
            Title = "极简线条", Resolution = "1920 x 1080", Source = "示例", Category = "简约",
            GradientFrom = Theme.Muted, GradientTo = Theme.Border,
        };
        yield return new WallpaperEntry
        {
            Title = "星云漫游", Resolution = "3840 x 2160", Source = "示例", Category = "科幻",
            GradientFrom = Theme.Primary, GradientTo = Theme.Info,
        };
    }

    /// <summary>尝试读取图片尺寸（使用 System.Drawing.Image，失败返回 false）</summary>
    private static bool TryGetSize(string path, out int width, out int height)
    {
        width = 0; height = 0;
        try
        {
            using var img = System.Drawing.Image.FromFile(path);
            width = img.Width;
            height = img.Height;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ============================================================
    //  顶部栏（标题 + 搜索框）
    // ============================================================

    /// <summary>构建顶部栏：页面说明 + 搜索框</summary>
    private Border BuildTopBar()
    {
        var title = new TextBlock
        {
            Text = "精选壁纸",
            FontFamily = Theme.UiFont,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var search = new TextBox
        {
            Width = 240,
            Height = 32,
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextRegular,
            Background = Theme.InputBackground,
            BorderBrush = Theme.InputBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = "搜索壁纸…",
        };
        search.TextChanged += (_, e) =>
        {
            _query = search.Text?.Trim() ?? string.Empty;
            RenderGrid();
        };

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(search);

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(title);

        return new Border
        {
            Child = dock,
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 0, 0, 12),
        };
    }

    // ============================================================
    //  分类胶囊栏
    // ============================================================

    /// <summary>构建分类胶囊栏（全部 / 风景 / 动漫 / 科幻 / 游戏 / 简约 / 动态）</summary>
    private StackPanel BuildCategoryPills()
    {
        _pills = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12),
        };
        foreach (var cat in Categories)
        {
            _pills.Children.Add(MakePill(cat, cat == _category));
        }
        return _pills;
    }

    /// <summary>创建单个分类胶囊（点击切换分类）</summary>
    private Border MakePill(string text, bool active)
    {
        var label = new TextBlock
        {
            Text = text,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = active ? Theme.PrimaryBrush : Theme.TextSecondary,
        };
        var border = new Border
        {
            Child = label,
            Background = active ? Theme.PrimarySubtle : Theme.InputBackground,
            BorderBrush = active ? Theme.PrimaryBrush : Theme.InputBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            _category = text;
            RefreshPills();
            RenderGrid();
        };
        return border;
    }

    /// <summary>刷新所有胶囊的选中态</summary>
    private void RefreshPills()
    {
        if (_pills == null) return;
        foreach (var child in _pills.Children)
        {
            if (child is not Border b || b.Child is not TextBlock lbl) continue;
            var active = lbl.Text == _category;
            b.Background = active ? Theme.PrimarySubtle : Theme.InputBackground;
            b.BorderBrush = active ? Theme.PrimaryBrush : Theme.InputBorder;
            lbl.Foreground = active ? Theme.PrimaryBrush : Theme.TextSecondary;
            lbl.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>构建状态栏 — 显示扫描到的壁纸数量与可应用数</summary>
    private UIElement BuildStatusBar()
    {
        var real = _all.Count(e => e.Path != null);
        var total = _all.Count;
        _statusText = new TextBlock
        {
            Text = real > 0
                ? $"共 {total} 张壁纸（{real} 张可应用）· 点击「设为壁纸」直接更换桌面"
                : $"共 {total} 张示例壁纸（无真实文件）· 请到「设置 → 壁纸」下载必应壁纸或选择本地文件夹",
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = real > 0 ? Theme.TextSecondary : new SolidColorBrush(Theme.Warning),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        return _statusText;
    }

    // ============================================================
    //  壁纸卡片网格
    // ============================================================

    /// <summary>构建壁纸卡片网格宿主</summary>
    private WrapPanel BuildGrid()
    {
        _grid = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        RenderGrid();
        return _grid;
    }

    /// <summary>按当前分类 + 搜索关键字重新渲染网格</summary>
    private void RenderGrid()
    {
        if (_grid == null) return;
        _grid.Children.Clear();

        IEnumerable<WallpaperEntry> list = _all;
        if (_category != "全部")
            list = list.Where(e => e.Category == _category);
        if (!string.IsNullOrEmpty(_query))
            list = list.Where(e => e.Title.Contains(_query, StringComparison.OrdinalIgnoreCase));

        foreach (var entry in list)
        {
            _grid.Children.Add(BuildCard(entry));
        }

        if (_grid.Children.Count == 0)
        {
            _grid.Children.Add(new TextBlock
            {
                Text = "没有匹配的壁纸",
                FontFamily = Theme.UiFont,
                FontSize = 12,
                Foreground = Theme.TextFaint,
                Margin = new Thickness(4, 12, 0, 12),
            });
        }
    }

    /// <summary>构建单张壁纸卡片</summary>
    private Border BuildCard(WallpaperEntry entry)
    {
        // 缩略图：真实文件用 Image，否则渐变色块
        UIElement thumb;
        if (entry.Path != null)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 400;
                bmp.UriSource = new Uri(entry.Path, UriKind.Absolute);
                bmp.EndInit();
                thumb = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.UniformToFill,
                    Height = 120,
                };
            }
            catch
            {
                thumb = MakeGradientThumb(entry);
            }
        }
        else
        {
            thumb = MakeGradientThumb(entry);
        }

        var thumbClip = new Border
        {
            Child = thumb,
            ClipToBounds = true,
            CornerRadius = new CornerRadius(Theme.RadiusMd, Theme.RadiusMd, 0, 0),
            Height = 120,
        };

        var title = new TextBlock
        {
            Text = entry.Title,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var res = new TextBlock
        {
            Text = $"{entry.Resolution} · {entry.Source}",
            FontFamily = Theme.UiFont,
            FontSize = 10,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 2, 0, 8),
        };

        var favBtn = CreateSecondaryButton(entry.Favorite ? "★ 已收藏" : "♡ 收藏");
        favBtn.Height = 26;
        favBtn.FontSize = 11;
        favBtn.Padding = new Thickness(10, 0, 10, 0);
        favBtn.Click += (_, _) =>
        {
            entry.Favorite = !entry.Favorite;
            favBtn.Content = entry.Favorite ? "★ 已收藏" : "♡ 收藏";
        };

        var setBtn = CreatePrimaryButton("设为壁纸");
        setBtn.Height = 26;
        setBtn.FontSize = 11;
        setBtn.Padding = new Thickness(10, 0, 10, 0);
        setBtn.Margin = new Thickness(6,0,0,0);
        // 占位示例（无真实文件）按钮仍可点击，点击后给出引导提示而非禁用
        if (entry.Path == null)
        {
            setBtn.Opacity = 0.6;
        }
        setBtn.Click += (_, _) => SetAsWallpaper(entry);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
        btnRow.Children.Add(favBtn);
        btnRow.Children.Add(setBtn);

        var panel = new StackPanel();
        panel.Children.Add(thumbClip);
        panel.Children.Add(title);
        panel.Children.Add(res);
        panel.Children.Add(btnRow);

        return new Border
        {
            Child = panel,
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 12, 12),
            Width = 200,
        };
    }

    /// <summary>渐变缩略图（无真实文件时占位）</summary>
    private static Border MakeGradientThumb(WallpaperEntry entry)
    {
        var g = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new(entry.GradientFrom, 0.0),
                new(entry.GradientTo, 1.0),
            },
        };
        return new Border
        {
            Background = g,
            Height = 120,
            Child = new TextBlock
            {
                Text = entry.Title,
                FontFamily = Theme.UiFont,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    /// <summary>设为壁纸：调用 WallpaperService 写入系统</summary>
    private void SetAsWallpaper(WallpaperEntry entry)
    {
        try
        {
            if (string.IsNullOrEmpty(entry.Path) || !File.Exists(entry.Path))
            {
                // 占位示例无真实文件 — 引导用户去设置页获取真实壁纸
                UpdateStatus("示例壁纸无真实文件，无法应用。请到「设置 → 壁纸」下载必应壁纸或选择本地文件夹后回到此处。", warn: true);
                Console.WriteLine("[WallpaperSquare] 占位壁纸无真实文件，无法应用");
                return;
            }
            var ok = _wallpaper.SetWallpaper(entry.Path);
            Console.WriteLine($"[WallpaperSquare] 设为壁纸 {entry.Title}: {(ok ? "成功" : "失败")}");
            if (ok)
            {
                UpdateStatus($"已将「{entry.Title}」设为桌面壁纸", warn: false);
            }
            else
            {
                UpdateStatus($"设置壁纸失败：{entry.Title}（文件可能无效）", warn: true);
            }
            // 刷新当前壁纸信息卡
            UpdateCurrentWallpaper(entry);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WallpaperSquare] 设为壁纸失败: {ex.Message}");
            UpdateStatus($"设为壁纸异常: {ex.Message}", warn: true);
        }
    }

    /// <summary>更新状态栏文本（warn=true 用警告色）</summary>
    private void UpdateStatus(string text, bool warn)
    {
        if (_statusText == null) return;
        _statusText.Text = text;
        _statusText.Foreground = warn ? new SolidColorBrush(Theme.Warning) : Theme.TextSecondary;
    }

    // ============================================================
    //  当前壁纸信息卡
    // ============================================================

    /// <summary>构建底部当前壁纸信息卡</summary>
    private Border BuildCurrentWallpaperCard()
    {
        var title = new TextBlock
        {
            Text = "当前壁纸",
            FontFamily = Theme.UiFont,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 8),
        };

        _currentThumb = new Border
        {
            Width = 96,
            Height = 60,
            CornerRadius = Theme.SmallRadius,
            Background = MakeBrandGradient(),
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
        };
        _currentName = new TextBlock
        {
            Text = "（未检测到）",
            FontFamily = Theme.UiFont,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            Margin = new Thickness(0, 0, 0, 2),
        };
        _currentRes = new TextBlock
        {
            Text = "—",
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
        };
        var info = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        info.Children.Add(_currentName);
        info.Children.Add(_currentRes);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_currentThumb);
        row.Children.Add(info);

        var panel = new StackPanel();
        panel.Children.Add(title);
        panel.Children.Add(row);

        UpdateCurrentWallpaper(null);

        return new Border
        {
            Child = panel,
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(14),
            Margin = new Thickness(0, 8, 0, 0),
        };
    }

    /// <summary>更新当前壁纸信息卡（读取注册表当前壁纸，或使用刚设置的壁纸）</summary>
    private void UpdateCurrentWallpaper(WallpaperEntry? justSet)
    {
        try
        {
            string? path = justSet?.Path;
            if (string.IsNullOrEmpty(path))
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                path = key?.GetValue("Wallpaper") as string;
            }

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 200;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                if (_currentThumb != null)
                {
                    _currentThumb.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                }
                if (_currentName != null)
                {
                    _currentName.Text = Path.GetFileNameWithoutExtension(path);
                }
                if (_currentRes != null)
                {
                    _currentRes.Text = TryGetSize(path, out var w, out var h) ? $"{w} x {h} · 系统" : "系统壁纸";
                }
                return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WallpaperSquare] 读取当前壁纸失败: {ex.Message}");
        }

        if (_currentThumb != null) _currentThumb.Background = MakeBrandGradient();
        if (_currentName != null) _currentName.Text = "默认壁纸";
        if (_currentRes != null) _currentRes.Text = "品牌渐变 · zDesktop";
    }

    /// <summary>品牌渐变（当前壁纸缩略图占位）</summary>
    private static Brush MakeBrandGradient()
    {
        var g = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new(Theme.Primary, 0.0),
                new(Theme.Background, 1.0),
            },
        };
        return g;
    }

    // ============================================================
    //  数据模型
    // ============================================================

    /// <summary>壁纸条目数据（真实文件或占位示例）</summary>
    private sealed class WallpaperEntry
    {
        /// <summary>标题</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>分辨率文本</summary>
        public string Resolution { get; set; } = string.Empty;

        /// <summary>来源</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>分类</summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>真实文件路径（null 表示占位示例）</summary>
        public string? Path { get; set; }

        /// <summary>渐变起始色（占位用）</summary>
        public Color GradientFrom { get; set; }

        /// <summary>渐变结束色（占位用）</summary>
        public Color GradientTo { get; set; }

        /// <summary>是否已收藏</summary>
        public bool Favorite { get; set; }
    }
}
