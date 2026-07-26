using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using zDesktop.Core.Widgets;
using zDesktop.Shell.Styles;
using zDesktop.Shell.Wallpaper;
using zDesktop.Shell.Widgets;

namespace zDesktop.Widgets.WallpaperManager;

/// <summary>
/// 壁纸管理组件 — 必应每日壁纸 / 本地轮播 / 手动切换
///
/// 视觉：
/// - 顶部：当前壁纸缩略图预览
/// - 中部：来源标签（必应每日 / 本地轮播）
/// - 底部：上一张 / 下一张 / 设为壁纸
///
/// 配置：
/// - source: bing / local / off
/// - localFolder: 本地壁纸文件夹路径
/// - autoRotate: 自动轮播开关
/// - rotateMinutes: 轮播间隔（分钟）
/// </summary>
public class WallpaperWidget : WidgetBase
{
    private readonly WallpaperService _service;
    private readonly DispatcherTimer _rotateTimer;

    private readonly Image _preview;
    private readonly TextBlock _sourceLabel;
    private readonly TextBlock _infoLabel;
    private readonly Button _prevBtn;
    private readonly Button _nextBtn;
    private readonly Button _applyBtn;

    private List<string> _wallpaperList = new();
    private int _currentIndex;

    public override WidgetDescriptor Descriptor { get; } = new()
    {
        Id = "wallpaper-manager",
        Name = "壁纸管理",
        Description = "必应每日壁纸 / 本地轮播",
        DefaultWidth = 280,
        DefaultHeight = 280,
        AllowResize = false,
        ConfigSchema = new()
        {
            new WidgetConfigField
            {
                Key = "source",
                Label = "壁纸来源",
                FieldType = WidgetConfigFieldType.Choice,
                DefaultValue = "bing",
                Description = "壁纸图片来源",
                Choices = new()
                {
                    new ConfigChoice { Value = "bing", Label = "必应每日壁纸" },
                    new ConfigChoice { Value = "local", Label = "本地文件夹" },
                    new ConfigChoice { Value = "off", Label = "关闭（仅手动）" },
                },
            },
            new WidgetConfigField
            {
                Key = "localFolder",
                Label = "本地壁纸文件夹",
                FieldType = WidgetConfigFieldType.Text,
                DefaultValue = "",
                Description = "本地壁纸图片所在的文件夹路径",
            },
            new WidgetConfigField
            {
                Key = "autoRotate",
                Label = "自动轮播",
                FieldType = WidgetConfigFieldType.Toggle,
                DefaultValue = false,
                Description = "开启后按间隔自动切换壁纸",
            },
            new WidgetConfigField
            {
                Key = "rotateMinutes",
                Label = "轮播间隔（分钟）",
                FieldType = WidgetConfigFieldType.Number,
                DefaultValue = 30,
                Min = 5,
                Max = 1440,
                Step = 5,
                Description = "自动轮播的切换间隔",
            },
        },
    };

    public WallpaperWidget()
    {
        _service = new WallpaperService();

        var panel = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };

        // ===== 缩略图预览 =====
        _preview = new Image
        {
            Height = 120,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ClipToBounds = true,
        };
        var previewBorder = new Border
        {
            CornerRadius = Theme.ControlRadius,
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 0, 8),
            Child = _preview,
        };
        panel.Children.Add(previewBorder);

        // ===== 来源标签 =====
        _sourceLabel = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextPrimary,
            Margin = new Thickness(0, 0, 0, 4),
        };
        panel.Children.Add(_sourceLabel);

        // ===== 信息标签 =====
        _infoLabel = new TextBlock
        {
            FontSize = 10,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        panel.Children.Add(_infoLabel);

        // ===== 按钮栏 =====
        var btnRow = new DockPanel { LastChildFill = true };

        _prevBtn = CreateNavButton("‹ 上一张");
        _prevBtn.Click += (_, _) => ShiftWallpaper(-1);
        DockPanel.SetDock(_prevBtn, Dock.Left);
        btnRow.Children.Add(_prevBtn);

        _nextBtn = CreateNavButton("下一张 ›");
        _nextBtn.Click += (_, _) => ShiftWallpaper(1);
        DockPanel.SetDock(_nextBtn, Dock.Right);
        btnRow.Children.Add(_nextBtn);

        _applyBtn = new Button
        {
            Content = "设为壁纸",
            Height = 30,
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            Background = Theme.PrimaryBrush,
            Foreground = Brushes.White,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _applyBtn.Click += (_, _) => ApplyCurrentWallpaper();
        btnRow.Children.Add(_applyBtn);

        panel.Children.Add(btnRow);

        Content = new Grid
        {
            Background = Brushes.Transparent,
            Children = { panel },
        };

        _rotateTimer = new DispatcherTimer();
        _rotateTimer.Tick += (_, _) => ShiftWallpaper(1);
    }

    private Button CreateNavButton(string text)
    {
        return new Button
        {
            Content = text,
            Height = 30,
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            Background = Theme.InputBackground,
            Foreground = Theme.TextRegular,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0),
        };
    }

    // 不重写 OnInitialize：配置在容器创建之前就已应用，OnConfigChanged
    // 那时已经把来源写成「必应每日壁纸」并开始加载了。这里再写一句
    //「加载中…」只会把它盖掉，图片都显示出来了标题还停在加载中。

    public override void OnConfigChanged()
    {
        var source = GetConfig("source", "bing");
        var localFolder = GetConfig("localFolder", "");
        var autoRotate = GetConfig("autoRotate", false);
        var rotateMinutes = GetConfig("rotateMinutes", 30);

        // 轮播定时器
        if (autoRotate)
        {
            _rotateTimer.Interval = TimeSpan.FromMinutes(Math.Max(5, rotateMinutes));
            _rotateTimer.Start();
        }
        else
        {
            _rotateTimer.Stop();
        }

        // 加载壁纸列表
        LoadWallpaperList(source, localFolder);
    }

    public override void OnUnload()
    {
        _rotateTimer.Stop();
        _service.Dispose();
    }

    // ===== 壁纸加载 =====

    private void LoadWallpaperList(string source, string localFolder)
    {
        _wallpaperList.Clear();
        _currentIndex = 0;

        switch (source)
        {
            case "bing":
                _sourceLabel.Text = "必应每日壁纸";
                _wallpaperList = _service.GetBingWallpapers();
                // 如果没有缓存，异步下载今日壁纸
                if (_wallpaperList.Count == 0)
                {
                    _infoLabel.Text = "正在下载必应壁纸...";
                    _ = DownloadAndRefreshAsync();
                    return;
                }
                break;

            case "local":
                _sourceLabel.Text = "本地壁纸";
                if (string.IsNullOrEmpty(localFolder))
                {
                    _infoLabel.Text = "请在设置中指定本地壁纸文件夹";
                    _preview.Source = null;
                    return;
                }
                _wallpaperList = _service.GetLocalWallpapers(localFolder);
                if (_wallpaperList.Count == 0)
                {
                    _infoLabel.Text = $"文件夹中未找到图片:\n{localFolder}";
                    _preview.Source = null;
                    return;
                }
                break;

            default:
                _sourceLabel.Text = "手动模式";
                _infoLabel.Text = "点击下方按钮浏览并设置壁纸";
                _preview.Source = null;
                return;
        }

        ShowCurrentWallpaper();
    }

    private async Task DownloadAndRefreshAsync()
    {
        var path = await _service.DownloadBingWallpaperAsync();
        if (path != null)
        {
            _wallpaperList = _service.GetBingWallpapers();
            _currentIndex = 0;
            ShowCurrentWallpaper();
        }
        else
        {
            _infoLabel.Text = "必应壁纸下载失败，请检查网络";
        }
    }

    // ===== 导航 =====

    private void ShiftWallpaper(int delta)
    {
        if (_wallpaperList.Count == 0) return;

        _currentIndex = (_currentIndex + delta + _wallpaperList.Count) % _wallpaperList.Count;
        ShowCurrentWallpaper();
    }

    private void ShowCurrentWallpaper()
    {
        if (_currentIndex < 0 || _currentIndex >= _wallpaperList.Count) return;

        var path = _wallpaperList[_currentIndex];
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.DecodePixelWidth = 400; // 缩略图尺寸
            bmp.EndInit();
            bmp.Freeze();
            _preview.Source = bmp;

            var fileName = Path.GetFileNameWithoutExtension(path);
            var fileDate = File.GetLastWriteTime(path);
            _infoLabel.Text = $"{fileName}\n{fileDate:yyyy-MM-dd}  {_currentIndex + 1}/{_wallpaperList.Count}";
        }
        catch (Exception ex)
        {
            _infoLabel.Text = $"无法加载图片: {ex.Message}";
        }
    }

    private void ApplyCurrentWallpaper()
    {
        if (_currentIndex < 0 || _currentIndex >= _wallpaperList.Count) return;

        var path = _wallpaperList[_currentIndex];
        var success = _service.SetWallpaper(path);

        _infoLabel.Text = success
            ? "已设为桌面壁纸"
            : "设置壁纸失败";
    }
}
