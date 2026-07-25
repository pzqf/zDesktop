using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using zDesktop.Core.Widgets;
using zDesktop.Shell.Styles;
using zDesktop.Shell.Widgets;

namespace zDesktop.Widgets.Clock;

/// <summary>
/// 时钟组件 — 显示当前时间、日期、星期
///
/// 视觉：大号等宽时间 + 秒数 + 中文日期 + 星期
/// </summary>
public class ClockWidget : WidgetBase
{
    private readonly TextBlock _timeText;
    private readonly TextBlock _secondsText;
    private readonly TextBlock _ampmText;
    private readonly TextBlock _dateText;
    private readonly TextBlock _weekdayText;
    private readonly DispatcherTimer _timer;

    private static readonly CultureInfo ZhCn = new("zh-CN");

    /// <summary>是否使用 24 小时制</summary>
    private bool _use24Hour = true;

    /// <summary>是否显示秒数</summary>
    private bool _showSeconds = true;

    public override WidgetDescriptor Descriptor { get; } = new()
    {
        Id = "clock",
        Name = "时钟",
        Description = "显示当前时间与日期",
        DefaultWidth = 280,
        DefaultHeight = 150,
        AllowResize = false,
        ConfigSchema = new()
        {
            new WidgetConfigField
            {
                Key = "use24Hour",
                Label = "24 小时制",
                FieldType = WidgetConfigFieldType.Toggle,
                DefaultValue = true,
                Description = "关闭后使用 12 小时制（上午/下午）",
            },
            new WidgetConfigField
            {
                Key = "showSeconds",
                Label = "显示秒数",
                FieldType = WidgetConfigFieldType.Toggle,
                DefaultValue = true,
                Description = "在时间右侧显示秒数",
            },
        },
    };

    public ClockWidget()
    {
        // ===== 时间行：HH:mm : ss =====
        var timeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _timeText = new TextBlock
        {
            FontSize = 52,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.TextPrimary,
            FontFamily = Theme.MonoFont,
        };

        _secondsText = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeights.Normal,
            Foreground = Theme.TextSecondary,
            FontFamily = Theme.MonoFont,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(4, 0, 0, 10),
        };

        _ampmText = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.Normal,
            Foreground = Theme.TextSecondary,
            FontFamily = Theme.MonoFont,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(6, 0, 0, 12),
        };

        timeRow.Children.Add(_timeText);
        timeRow.Children.Add(_secondsText);
        timeRow.Children.Add(_ampmText);

        // ===== 日期行 =====
        _dateText = new TextBlock
        {
            FontSize = 14,
            Foreground = Theme.TextRegular,
            FontFamily = Theme.UiFont,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 2),
        };

        // ===== 星期行 =====
        _weekdayText = new TextBlock
        {
            FontSize = 12,
            Foreground = Theme.TextSecondary,
            FontFamily = Theme.UiFont,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // ===== 主面板 =====
        var panel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        panel.Children.Add(timeRow);
        panel.Children.Add(_dateText);
        panel.Children.Add(_weekdayText);

        Content = new Grid
        {
            Background = Brushes.Transparent,
            Children = { panel },
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateTime();
    }

    public override void OnInitialize()
    {
        UpdateTime();
        _timer.Start();
    }

    public override void OnUnload()
    {
        _timer.Stop();
    }

    public override void OnConfigChanged()
    {
        _use24Hour = GetConfig("use24Hour", true);
        _showSeconds = GetConfig("showSeconds", true);

        // 秒数隐藏时降低刷新频率（5 秒），显示时恢复 1 秒
        _timer.Interval = TimeSpan.FromSeconds(_showSeconds ? 1 : 5);

        // 立即刷新一次以反映新配置
        UpdateTime();
    }

    private void UpdateTime()
    {
        var now = DateTime.Now;

        _timeText.Text = _use24Hour
            ? now.ToString("HH:mm")
            : now.ToString("h:mm");

        _secondsText.Text = now.ToString("ss");
        _secondsText.Visibility = _showSeconds
            ? Visibility.Visible
            : Visibility.Collapsed;

        _ampmText.Text = _use24Hour ? "" : now.ToString("tt", ZhCn);
        _ampmText.Visibility = _use24Hour
            ? Visibility.Collapsed
            : Visibility.Visible;

        _dateText.Text = now.ToString("yyyy\u5e74MM\u6708dd\u65e5");
        _weekdayText.Text = now.ToString("dddd", ZhCn);
    }
}
