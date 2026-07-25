using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using zDesktop.Core.Widgets;
using zDesktop.Shell.Styles;
using zDesktop.Shell.Widgets;

namespace zDesktop.Widgets.Calendar;

/// <summary>
/// 日历组件 — 月历网格视图
///
/// 功能：
/// 1. 显示当前月份的日历网格（6 行 × 7 列）
/// 2. 周一至周日标题行
/// 3. 今日日期高亮（品牌紫圆形背景）
/// 4. 上一月 / 下一月切换按钮
/// 5. 月份标题（如 "2026年7月"）
/// </summary>
public class CalendarWidget : WidgetBase
{
    private static readonly CultureInfo ZhCn = new("zh-CN");

    /// <summary>每周起始日（周一或周日）— 可通过配置切换</summary>
    private DayOfWeek _firstDayOfWeek = DayOfWeek.Monday;

    private DateTime _displayMonth;
    private readonly TextBlock _monthTitle;
    private readonly Grid _dayGrid;
    private readonly Grid _weekHeaderGrid;
    private readonly Button _prevBtn;
    private readonly Button _nextBtn;

    public override WidgetDescriptor Descriptor { get; } = new()
    {
        Id = "calendar",
        Name = "日历",
        Description = "月历视图，支持月份切换",
        DefaultWidth = 280,
        DefaultHeight = 300,
        AllowResize = false,
        ConfigSchema = new()
        {
            new WidgetConfigField
            {
                Key = "firstDayOfWeek",
                Label = "一周首日",
                FieldType = WidgetConfigFieldType.Choice,
                DefaultValue = "monday",
                Description = "日历网格从周几开始排列",
                Choices = new()
                {
                    new ConfigChoice { Value = "monday", Label = "周一" },
                    new ConfigChoice { Value = "sunday", Label = "周日" },
                },
            },
        },
    };

    public CalendarWidget()
    {
        _displayMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);

        // ===== 主面板 =====
        var root = new Grid { Margin = new Thickness(10, 6, 10, 10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 标题栏
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 星期标题
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 日期网格

        // --- 标题栏：上一月 / 月份 / 下一月 ---
        var headerPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

        _prevBtn = CreateNavButton("‹");
        _prevBtn.Click += (_, _) => ShiftMonth(-1);
        DockPanel.SetDock(_prevBtn, Dock.Left);
        headerPanel.Children.Add(_prevBtn);

        _nextBtn = CreateNavButton("›");
        _nextBtn.Click += (_, _) => ShiftMonth(1);
        DockPanel.SetDock(_nextBtn, Dock.Right);
        headerPanel.Children.Add(_nextBtn);

        _monthTitle = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            FontFamily = Theme.UiFont,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerPanel.Children.Add(_monthTitle);

        Grid.SetRow(headerPanel, 0);
        root.Children.Add(headerPanel);

        // --- 星期标题行 ---
        _weekHeaderGrid = BuildWeekHeader();
        Grid.SetRow(_weekHeaderGrid, 1);
        root.Children.Add(_weekHeaderGrid);

        // --- 日期网格 ---
        _dayGrid = BuildDayGrid();
        Grid.SetRow(_dayGrid, 2);
        root.Children.Add(_dayGrid);

        Content = new Grid
        {
            Background = Brushes.Transparent,
            Children = { root },
        };
    }

    public override void OnInitialize()
    {
        RenderMonth();
    }

    public override void OnConfigChanged()
    {
        var val = GetConfig("firstDayOfWeek", "monday");
        _firstDayOfWeek = val == "sunday" ? DayOfWeek.Sunday : DayOfWeek.Monday;

        // 原地重建星期标题行（复用同一个 Grid 实例）
        PopulateWeekHeader(_weekHeaderGrid);

        // 重新渲染日期网格
        RenderMonth();
    }

    // ===== 构建辅助 =====

    private Button CreateNavButton(string glyph)
    {
        return new Button
        {
            Content = glyph,
            Width = 28,
            Height = 28,
            Background = Theme.InputBackground,
            BorderThickness = new Thickness(0),
            Foreground = Theme.TextPrimary,
            FontFamily = Theme.UiFont,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Cursor = Cursors.Hand,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0),
        };
    }

    /// <summary>创建星期标题行 Grid</summary>
    private Grid BuildWeekHeader()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        PopulateWeekHeader(grid);
        return grid;
    }

    /// <summary>填充星期标题行内容（清空后重建，用于配置变更）</summary>
    private void PopulateWeekHeader(Grid grid)
    {
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();

        // 周一优先：一二三四五六日
        // 周日优先：日一二三四五六
        var labels = _firstDayOfWeek == DayOfWeek.Sunday
            ? new[] { "日", "一", "二", "三", "四", "五", "六" }
            : new[] { "一", "二", "三", "四", "五", "六", "日" };

        for (var i = 0; i < 7; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 周六周日用稍淡的色
            var isWeekend = _firstDayOfWeek == DayOfWeek.Sunday
                ? (i == 0 || i == 6)
                : i >= 5;
            var color = isWeekend
                ? Theme.TextFaint
                : Theme.TextSecondary;

            var cell = new TextBlock
            {
                Text = labels[i],
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = color,
                FontFamily = Theme.UiFont,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
    }

    /// <summary>根据 _firstDayOfWeek 的列索引判断是否为周末</summary>
    private bool IsWeekendColumn(int col)
    {
        return _firstDayOfWeek == DayOfWeek.Sunday
            ? (col == 0 || col == 6)
            : col >= 5;
    }

    /// <summary>日期网格 — 6 行 × 7 列</summary>
    private Grid BuildDayGrid()
    {
        var grid = new Grid();

        for (var i = 0; i < 7; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 6; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        return grid;
    }

    // ===== 渲染逻辑 =====

    private void ShiftMonth(int delta)
    {
        _displayMonth = _displayMonth.AddMonths(delta);
        RenderMonth();
    }

    /// <summary>渲染当前月份的日期网格</summary>
    private void RenderMonth()
    {
        _monthTitle.Text = _displayMonth.ToString("yyyy\u5e74M\u6708", ZhCn);

        _dayGrid.Children.Clear();

        // 计算本月第一天对应的网格列索引
        var firstDay = new DateTime(_displayMonth.Year, _displayMonth.Month, 1);
        var offset = ((int)firstDay.DayOfWeek - (int)_firstDayOfWeek + 7) % 7;

        // 本月天数
        var daysInMonth = DateTime.DaysInMonth(_displayMonth.Year, _displayMonth.Month);

        // 上月末尾几天（灰色填充）
        var prevMonth = firstDay.AddMonths(-1);
        var prevDaysInMonth = DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month);

        var today = DateTime.Today;

        // 渲染 42 格（6 行 × 7 列）
        for (var i = 0; i < 42; i++)
        {
            var row = i / 7;
            var col = i % 7;

            int dayNum;
            DateTime date;
            bool isCurrentMonth;
            bool isToday;

            if (i < offset)
            {
                // 上月日期
                dayNum = prevDaysInMonth - offset + i + 1;
                date = new DateTime(prevMonth.Year, prevMonth.Month, dayNum);
                isCurrentMonth = false;
            }
            else if (i < offset + daysInMonth)
            {
                // 本月日期
                dayNum = i - offset + 1;
                date = new DateTime(_displayMonth.Year, _displayMonth.Month, dayNum);
                isCurrentMonth = true;
            }
            else
            {
                // 下月日期
                dayNum = i - offset - daysInMonth + 1;
                date = new DateTime(_displayMonth.Year, _displayMonth.Month, 1).AddMonths(1);
                date = new DateTime(date.Year, date.Month, dayNum);
                isCurrentMonth = false;
            }

            isToday = date.Date == today;

            var cell = CreateDayCell(dayNum, isCurrentMonth, isToday, IsWeekendColumn(col));
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            _dayGrid.Children.Add(cell);
        }
    }

    /// <summary>创建单个日期格子</summary>
    private UIElement CreateDayCell(int day, bool isCurrentMonth, bool isToday, bool isWeekend)
    {
        // 今日：品牌紫圆形背景 + 白字加粗
        // 本月：白色字
        // 非本月：半透明灰字
        // 周末：稍淡

        Brush fg;
        Brush? bg = null;

        if (isToday)
        {
            fg = Theme.TextPrimary;
            bg = Theme.PrimaryBrush; // 品牌紫
        }
        else if (!isCurrentMonth)
        {
            fg = Theme.TextFaint;
        }
        else if (isWeekend)
        {
            fg = Theme.TextSecondary;
        }
        else
        {
            fg = Theme.TextRegular;
        }

        var label = new TextBlock
        {
            Text = day.ToString(),
            FontSize = 13,
            FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
            Foreground = fg,
            FontFamily = Theme.UiFont,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (bg != null)
        {
            // 今日用圆形高亮
            return new Border
            {
                Background = bg,
                CornerRadius = new CornerRadius(Theme.RadiusLg),
                Width = 28,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = label,
            };
        }

        return label;
    }
}
