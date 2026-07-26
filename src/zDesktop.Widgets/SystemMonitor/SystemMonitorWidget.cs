using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using zDesktop.Core.Widgets;
using zDesktop.Shell.Monitoring;
using zDesktop.Shell.Styles;
using zDesktop.Shell.Widgets;

namespace zDesktop.Widgets.SystemMonitor;

/// <summary>
/// 系统监控组件 — CPU / 内存使用率 + 实时折线图
///
/// 视觉：
/// - 顶部：CPU 百分比大数字 + 迷你折线图
/// - 底部：内存百分比 + 总量/可用文字
/// - 每 1 秒采样一次，保留最近 60 个数据点
/// </summary>
public class SystemMonitorWidget : WidgetBase
{
    private const int MaxPoints = 60;

    private readonly SystemPerformanceSampler _sampler = new();
    private readonly DispatcherTimer _timer;

    private readonly TextBlock _cpuValue;
    private readonly TextBlock _memValue;
    private readonly TextBlock _memDetail;
    private readonly Polyline _cpuGraph;
    private readonly Polyline _memGraph;
    private readonly Canvas _graphCanvas;
    private readonly StackPanel _memSection; // 内存区（可整体隐藏）

    private readonly SampleSeries _cpuHistory = new(MaxPoints);
    private readonly SampleSeries _memHistory = new(MaxPoints);

    /// <summary>刷新频率（秒）</summary>
    private int _refreshInterval = 1;

    public override WidgetDescriptor Descriptor { get; } = new()
    {
        Id = "system-monitor",
        Name = "系统监控",
        Description = "CPU 与内存使用率实时监控",
        DefaultWidth = 280,
        DefaultHeight = 220,
        ResizeMode = WidgetResizeMode.None,
        ConfigSchema = new()
        {
            new WidgetConfigField
            {
                Key = "refreshInterval",
                Label = "刷新频率",
                FieldType = WidgetConfigFieldType.Choice,
                DefaultValue = "1",
                Description = "CPU / 内存数据采样间隔",
                Choices = new()
                {
                    new ConfigChoice { Value = "1", Label = "1 秒（实时）" },
                    new ConfigChoice { Value = "2", Label = "2 秒（省电）" },
                    new ConfigChoice { Value = "5", Label = "5 秒（节能）" },
                },
            },
            new WidgetConfigField
            {
                Key = "showMemoryGraph",
                Label = "显示内存折线图",
                FieldType = WidgetConfigFieldType.Toggle,
                DefaultValue = true,
                Description = "关闭后仅显示 CPU 折线图",
            },
        },
    };

    public SystemMonitorWidget()
    {
        // ===== CPU 区 =====
        var cpuHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 8, 12, 0),
        };
        var cpuLabel = new TextBlock
        {
            Text = "CPU",
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            FontFamily = Theme.UiFont,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 6, 3),
        };
        _cpuValue = new TextBlock
        {
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.TextPrimary,
            FontFamily = Theme.MonoFont,
        };
        var cpuUnit = new TextBlock
        {
            Text = "%",
            FontSize = 13,
            Foreground = Theme.TextSecondary,
            FontFamily = Theme.UiFont,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(2, 0, 0, 4),
        };
        cpuHeader.Children.Add(cpuLabel);
        cpuHeader.Children.Add(_cpuValue);
        cpuHeader.Children.Add(cpuUnit);

        // CPU 折线图
        _cpuGraph = new Polyline
        {
            Stroke = Theme.PrimaryAccent, // 品牌紫
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
        };
        _graphCanvas = new Canvas
        {
            Background = Theme.ChartBackground,
            Margin = new Thickness(12, 4, 12, 0),
            Height = 36,
            ClipToBounds = true,
        };
        _graphCanvas.Children.Add(_cpuGraph);

        // ===== 内存区 =====
        var memHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 8, 12, 0),
        };
        var memLabel = new TextBlock
        {
            Text = "内存",
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            FontFamily = Theme.UiFont,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 6, 3),
        };
        _memValue = new TextBlock
        {
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.TextPrimary,
            FontFamily = Theme.MonoFont,
        };
        var memUnit = new TextBlock
        {
            Text = "%",
            FontSize = 13,
            Foreground = Theme.TextSecondary,
            FontFamily = Theme.UiFont,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(2, 0, 0, 4),
        };
        memHeader.Children.Add(memLabel);
        memHeader.Children.Add(_memValue);
        memHeader.Children.Add(memUnit);

        // 内存折线图
        _memGraph = new Polyline
        {
            Stroke = Theme.SuccessBrush, // 翠绿
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var memGraphCanvas = new Canvas
        {
            Background = Theme.ChartBackground,
            Margin = new Thickness(12, 4, 12, 0),
            Height = 36,
            ClipToBounds = true,
        };
        memGraphCanvas.Children.Add(_memGraph);

        // 内存详情
        _memDetail = new TextBlock
        {
            FontSize = 10,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(12, 4, 12, 8),
            FontFamily = Theme.MonoFont,
        };

        // ===== 主面板 =====
        var panel = new StackPanel();
        panel.Children.Add(cpuHeader);
        panel.Children.Add(_graphCanvas);

        // 内存区整体（标题 + 折线图 + 详情）— 可通过配置隐藏
        _memSection = new StackPanel();
        _memSection.Children.Add(memHeader);
        _memSection.Children.Add(memGraphCanvas);
        _memSection.Children.Add(_memDetail);
        panel.Children.Add(_memSection);

        Content = new Grid
        {
            Background = Brushes.Transparent,
            Children = { panel },
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();
    }

    public override void OnInitialize()
    {
        // 首次采样建立基准
        _sampler.SampleOnce();
        _timer.Start();
    }

    public override void OnUnload()
    {
        _timer.Stop();
    }

    public override void OnConfigChanged()
    {
        // 刷新频率
        _refreshInterval = GetConfig("refreshInterval", "1") switch
        {
            "2" => 2,
            "5" => 5,
            _ => 1,
        };
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(_refreshInterval);
        _timer.Start();

        // 内存折线图显隐
        var showMem = GetConfig("showMemoryGraph", true);
        _memSection.Visibility = showMem
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnTick()
    {
        var sample = _sampler.SampleOnce();

        // 更新数值
        _cpuValue.Text = sample.CpuUsage.ToString("F0");
        _memValue.Text = sample.MemoryUsage.ToString("F0");

        var totalGb = sample.MemoryTotalBytes / 1024.0 / 1024 / 1024;
        var availGb = sample.MemoryAvailableBytes / 1024.0 / 1024 / 1024;
        _memDetail.Text = $"可用 {availGb:F1} GB / 共 {totalGb:F1} GB";

        // 推入历史
        _cpuHistory.Push(sample.CpuUsage);
        _memHistory.Push(sample.MemoryUsage);

        // 绘制折线图
        DrawGraph(_cpuGraph, _cpuHistory);
        DrawGraph(_memGraph, _memHistory);
    }

    /// <summary>根据历史数据绘制折线图（0-100 映射到画布高度）</summary>
    private static void DrawGraph(Polyline graph, SampleSeries history)
    {
        if (graph.Parent is not Canvas canvas) return;

        canvas.Dispatcher.BeginInvoke(new Action(() =>
        {
            var w = canvas.ActualWidth;
            var h = canvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var points = new PointCollection();

            for (var i = 0; i < history.Count; i++)
            {
                var x = w * i / (history.Capacity - 1.0);
                var y = h - (h * history[i] / 100f);
                points.Add(new Point(x, y));
            }

            graph.Points = points;
        }), System.Windows.Threading.DispatcherPriority.DataBind);
    }
}
