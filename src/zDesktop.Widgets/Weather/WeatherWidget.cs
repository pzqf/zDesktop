using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using zDesktop.Core.Widgets;
using zDesktop.Shell.Styles;
using zDesktop.Shell.Weather;
using zDesktop.Shell.Widgets;

namespace zDesktop.Widgets.Weather;

/// <summary>
/// 天气组件 — 实时天气 + 3 天预报
///
/// 视觉：
/// - 顶部：城市名 + 天气描述 + 大号温度
/// - 中部：体感温度 / 湿度 / 风向
/// - 底部：3 天预报（日期 + 天气 + 高低温）
///
/// 配置：
/// - apiKey：和风天气 API Key
/// - city：城市名
/// - refreshInterval：刷新频率（分钟）
/// </summary>
public class WeatherWidget : WidgetBase
{
    private readonly WeatherService _service;
    private readonly DispatcherTimer _timer;

    private readonly TextBlock _cityText;
    private readonly TextBlock _tempText;
    private readonly TextBlock _weatherText;
    private readonly TextBlock _detailText;
    private readonly StackPanel _forecastPanel;
    private readonly TextBlock _statusText;
    private readonly StackPanel _dataPanel;   // 没配 Key 时整块收起

    private string _locationId = string.Empty;

    public override WidgetDescriptor Descriptor { get; } = new()
    {
        Id = "weather",
        Name = "天气",
        Description = "实时天气与 3 天预报",
        DefaultWidth = 280,
        DefaultHeight = 320,
        AllowResize = false,
        ConfigSchema = new()
        {
            new WidgetConfigField
            {
                Key = "apiKey",
                Label = "和风天气 API Key",
                FieldType = WidgetConfigFieldType.Text,
                DefaultValue = "",
                Description = "在 dev.qweather.com 免费注册获取",
            },
            new WidgetConfigField
            {
                Key = "city",
                Label = "城市名",
                FieldType = WidgetConfigFieldType.Text,
                DefaultValue = "北京",
                Description = "如：北京、上海、深圳",
            },
            new WidgetConfigField
            {
                Key = "refreshMinutes",
                Label = "刷新频率（分钟）",
                FieldType = WidgetConfigFieldType.Choice,
                DefaultValue = "30",
                Description = "天气数据刷新间隔",
                Choices = new()
                {
                    new ConfigChoice { Value = "10", Label = "10 分钟" },
                    new ConfigChoice { Value = "30", Label = "30 分钟" },
                    new ConfigChoice { Value = "60", Label = "1 小时" },
                    new ConfigChoice { Value = "180", Label = "3 小时" },
                },
            },
        },
    };

    public WeatherWidget()
    {
        _service = new WeatherService();

        var panel = new StackPanel { Margin = new Thickness(14, 10, 14, 14) };

        // 数据区整体成组：没配 API Key 时一起收起。
        // 否则用户看到的是一个空城市名、一个孤零零的「°」和空的「未来 3 天」，
        // 像是组件坏了，而不是「还没填 Key」。
        _dataPanel = new StackPanel();
        panel.Children.Add(_dataPanel);

        // ===== 当前天气区 =====
        _cityText = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextPrimary,
        };
        _dataPanel.Children.Add(_cityText);

        // 温度 + 天气描述
        var tempRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _tempText = new TextBlock
        {
            FontSize = 42,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.TextPrimary,
            FontFamily = Theme.MonoFont,
        };
        _weatherText = new TextBlock
        {
            FontSize = 15,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextRegular,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 0, 8),
        };
        tempRow.Children.Add(_tempText);
        tempRow.Children.Add(new TextBlock
        {
            Text = "°",
            FontSize = 22,
            Foreground = Theme.TextRegular,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(2, 0, 8, 4),
        });
        tempRow.Children.Add(_weatherText);
        _dataPanel.Children.Add(tempRow);

        // 详情
        _detailText = new TextBlock
        {
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 6, 0, 0),
            FontFamily = Theme.MonoFont,
        };
        _dataPanel.Children.Add(_detailText);

        // 分隔线
        _dataPanel.Children.Add(new Border
        {
            Height = 1,
            Background = Theme.Divider,
            Margin = new Thickness(0, 12, 0, 10),
        });

        // ===== 预报区标题 =====
        _dataPanel.Children.Add(new TextBlock
        {
            Text = "未来 3 天",
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 6),
        });

        // 预报列表
        _forecastPanel = new StackPanel();
        _dataPanel.Children.Add(_forecastPanel);

        // 状态提示（加载中/错误）
        _statusText = new TextBlock
        {
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextFaint,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        panel.Children.Add(_statusText);

        Content = new Grid
        {
            Background = Brushes.Transparent,
            Children = { panel },
        };

        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) => _ = RefreshWeatherAsync();
    }

    // 不重写 OnInitialize：配置在容器创建之前就已应用（见 WidgetHost.AddWidget），
    // 此时 OnConfigChanged 已经把状态摆好了。这里再写一句「加载中…」，
    // 只会把「请填写 API Key」盖掉，让用户干等一个永远不会来的结果。

    public override void OnConfigChanged()
    {
        var apiKey = GetConfig("apiKey", "");
        var city = GetConfig("city", "北京");
        var refreshStr = GetConfig("refreshMinutes", "30");

        _service.ApiKey = apiKey;

        // 设置刷新频率
        var minutes = refreshStr switch
        {
            "10" => 10,
            "60" => 60,
            "180" => 180,
            _ => 30,
        };
        _timer.Interval = TimeSpan.FromMinutes(minutes);

        if (string.IsNullOrEmpty(apiKey))
        {
            _timer.Stop();
            _dataPanel.Visibility = Visibility.Collapsed;
            _statusText.Text = "天气需要一个和风天气 API Key。\n" +
                               "点右上角齿轮填入，dev.qweather.com 可免费申请。";
            _statusText.TextWrapping = TextWrapping.Wrap;
            _statusText.TextAlignment = TextAlignment.Center;
            _statusText.Margin = new Thickness(4, 26, 4, 0);
            return;
        }

        _dataPanel.Visibility = Visibility.Visible;
        _statusText.Margin = new Thickness(0, 8, 0, 0);
        _statusText.Text = "加载中...";

        // 城市变更 → 重新查询 LocationId
        if (!string.IsNullOrEmpty(city))
        {
            _ = InitializeAndRefreshAsync(city);
        }
    }

    public override void OnUnload()
    {
        _timer.Stop();
        _service.Dispose();
    }

    /// <summary>初始化城市 LocationId 并刷新天气</summary>
    private async Task InitializeAndRefreshAsync(string cityName)
    {
        _statusText.Text = $"查询 {cityName}...";

        var geo = await _service.LookupCityAsync(cityName);
        if (geo == null)
        {
            _statusText.Text = "城市未找到，请检查城市名";
            return;
        }

        _locationId = geo.Id;
        _cityText.Text = geo.Name;

        await RefreshWeatherAsync();

        // 启动定时刷新
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>刷新天气数据</summary>
    private async Task RefreshWeatherAsync()
    {
        if (string.IsNullOrEmpty(_locationId)) return;

        _statusText.Text = "更新中...";

        var now = await _service.GetWeatherNowAsync(_locationId);
        var forecast = await _service.GetForecastAsync(_locationId);

        if (now == null)
        {
            _statusText.Text = "天气数据获取失败";
            return;
        }

        // 实时天气
        _tempText.Text = now.Temp;
        _weatherText.Text = now.Text;
        _detailText.Text = $"体感 {now.FeelsLike}°  湿度 {now.Humidity}%  {now.WindDir} {now.WindScale}级";

        // 预报
        _forecastPanel.Children.Clear();
        if (forecast != null)
        {
            foreach (var d in forecast)
            {
                _forecastPanel.Children.Add(CreateForecastRow(d));
            }
        }

        _statusText.Text = $"更新于 {DateTime.Now:HH:mm}";
    }

    /// <summary>创建预报行</summary>
    private UIElement CreateForecastRow(DailyForecast forecast)
    {
        var row = new DockPanel
        {
            Margin = new Thickness(0, 3, 0, 3),
        };

        // 日期
        var date = DateTime.TryParse(forecast.FxDate, out var dt) ? dt : DateTime.Now;
        var dateLabel = date.ToString("M/d") + " " + WeekdayLabel(date.DayOfWeek);

        var dateText = new TextBlock
        {
            Text = dateLabel,
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextRegular,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 80,
        };
        row.Children.Add(dateText);

        // 天气描述
        var weatherRow = new TextBlock
        {
            Text = $"{forecast.TextDay} / {forecast.TextNight}",
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        DockPanel.SetDock(weatherRow, Dock.Right);
        row.Children.Add(weatherRow);

        // 温度
        var tempText = new TextBlock
        {
            Text = $"{forecast.TempMin}° ~ {forecast.TempMax}°",
            FontSize = 12,
            Foreground = Theme.TextRegular,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 12, 0),
            FontFamily = Theme.MonoFont,
        };
        DockPanel.SetDock(tempText, Dock.Right);
        row.Children.Add(tempText);

        return row;
    }

    private static string WeekdayLabel(DayOfWeek dow) => dow switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };
}
