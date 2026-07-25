using System.Windows;
using System.Windows.Controls;
using zDesktop.Shell.Styles;

namespace zDesktop.App.Pages;

/// <summary>
/// 占位内容页 — 用于尚未实现的功能导航项（如壁纸广场 / 任务栏 / 设置等）
///
/// 显示页面标题与"开发中"提示，保证导航结构完整。
/// </summary>
public sealed class PlaceholderPage : ContentPage
{
    /// <summary>
    /// 构造占位页
    /// </summary>
    /// <param name="title">页面标题</param>
    /// <param name="navId">导航标识</param>
    public PlaceholderPage(string title, string navId)
    {
        Title = title;
        NavId = navId;

        var root = new StackPanel
        {
            Margin = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Center,
        };

        root.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = Theme.UiFont,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            Margin = new Thickness(0, 0, 0, 8),
        });

        root.Children.Add(new TextBlock
        {
            Text = "该功能开发中，敬请期待",
            FontFamily = Theme.UiFont,
            FontSize = 13,
            Foreground = Theme.TextSecondary,
        });

        Content = root;
    }
}
