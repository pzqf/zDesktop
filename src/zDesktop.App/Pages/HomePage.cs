using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using zDesktop.Shell.Styles;

namespace zDesktop.App.Pages;

/// <summary>
/// 首页概览内容页 — 欢迎卡片 + 快捷入口卡片网格 + 系统状态简览
///
/// 参考 pages/home.html 设计稿：顶部欢迎头、四张统计卡片、快捷操作按钮、
/// 最近文件列表与系统状态简览。点击快捷入口可跳转到对应功能页。
/// </summary>
public sealed class HomePage : ContentPage
{
    /// <summary>点击快捷入口时触发 — 参数为目标 navId</summary>
    public event Action<string>? NavigateRequested;

    /// <summary>
    /// 构造首页
    /// </summary>
    public HomePage()
    {
        Title = "首页概览";
        NavId = "home";

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(24, 16, 24, 24),
        };

        var root = new StackPanel();

        // ===== 欢迎头 =====
        root.Children.Add(BuildWelcomeHeader());

        // ===== 快捷入口卡片网格 =====
        root.Children.Add(BuildSectionLabel("快捷入口"));
        root.Children.Add(BuildQuickEntries());

        // ===== 系统状态简览 =====
        root.Children.Add(BuildSectionLabel("系统状态"));
        root.Children.Add(BuildStatusOverview());

        scroll.Content = root;
        Content = scroll;
    }

    /// <summary>构建欢迎头（品牌渐变底 + 问候语 + 日期）</summary>
    private Border BuildWelcomeHeader()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(20),
        };

        var avatar = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(24),
            Background = Theme.PrimaryBrush,
            Child = new TextBlock
            {
                Text = "Z",
                FontFamily = Theme.UiFont,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        panel.Children.Add(avatar);

        var info = new StackPanel
        {
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        info.Children.Add(new TextBlock
        {
            Text = GetGreeting(),
            FontFamily = Theme.UiFont,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
        });
        info.Children.Add(new TextBlock
        {
            Text = DateTime.Now.ToString("yyyy年M月d日 · dddd"),
            FontFamily = Theme.UiFont,
            FontSize = 12,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 4, 0, 0),
        });
        panel.Children.Add(info);

        return new Border
        {
            Child = panel,
            Background = Theme.PrimarySubtle,
            CornerRadius = Theme.ControlRadius,
            Margin = new Thickness(0, 0, 0, 20),
        };
    }

    /// <summary>构建快捷入口卡片网格（搜索 / 文件分类 / 磁盘映射 / 控制中心）</summary>
    private WrapPanel BuildQuickEntries()
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 20) };

        panel.Children.Add(MakeEntryCard("全局搜索", "聚合文件 / 应用 / 网页", "🔍", "global-search"));
        panel.Children.Add(MakeEntryCard("文件分类", "一键整理桌面文件", "🗂", "file-classify"));
        panel.Children.Add(MakeEntryCard("磁盘映射", "多窗格文件管理", "💾", "disk-mapper"));
        panel.Children.Add(MakeEntryCard("控制中心", "系统状态与快捷开关", "⚙", "control-center"));
        panel.Children.Add(MakeEntryCard("窗口管理", "排列 / 置顶 / 透明度", "🪟", "window-manager"));
        panel.Children.Add(MakeEntryCard("自动化规则", "文件监控与自动处理", "⚡", "automation-rules"));

        return panel;
    }

    /// <summary>创建单个快捷入口卡片</summary>
    private Border MakeEntryCard(string title, string desc, string glyph, string navId)
    {
        var panel = new StackPanel
        {
            Width = 200,
            Margin = new Thickness(0, 0, 12, 12),
        };

        panel.Children.Add(new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = Theme.ControlRadius,
            Background = Theme.PrimarySubtle,
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Margin = new Thickness(0, 0, 0, 10),
        });

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = Theme.UiFont,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            Margin = new Thickness(0, 0, 0, 2),
        });

        panel.Children.Add(new TextBlock
        {
            Text = desc,
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
        });

        var card = new Border
        {
            Child = panel,
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(14),
            Cursor = Cursors.Hand,
        };
        card.MouseEnter += (_, _) => card.Background = Theme.InputBackground;
        card.MouseLeave += (_, _) => card.Background = Theme.ListItemBackground;
        card.MouseLeftButtonUp += (_, _) => NavigateRequested?.Invoke(navId);
        return card;
    }

    /// <summary>构建系统状态简览卡片</summary>
    private Border BuildStatusOverview()
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "zDesktop 已就绪 — 桌面图标 + 组件 + 效率工具均在运行",
            FontFamily = Theme.UiFont,
            FontSize = 12,
            Foreground = Theme.TextRegular,
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new TextBlock
        {
            Text = "提示：Alt+Space 打开全局搜索，Ctrl+Space 打开控制中心",
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        return new Border
        {
            Child = panel,
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(16),
        };
    }

    /// <summary>创建分区小标题</summary>
    private static TextBlock BuildSectionLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(2, 4, 0, 10),
        };
    }

    /// <summary>按当前小时返回问候语</summary>
    private static string GetGreeting()
    {
        var h = DateTime.Now.Hour;
        if (h < 6) return "夜深了";
        if (h < 12) return "早上好";
        if (h < 14) return "中午好";
        if (h < 18) return "下午好";
        return "晚上好";
    }
}
