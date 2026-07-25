using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using zDesktop.Shell.Styles;

// 项目同时启用 WPF + WinForms，UserControl 在两个命名空间同名，显式别名优先 WPF
using UserControl = System.Windows.Controls.UserControl;

namespace zDesktop.App.Pages;

/// <summary>
/// 主窗口内容页基类 — 统一承载各功能页的 <see cref="UserControl"/>
///
/// 取代旧 <c>PanelWindowBase</c> 弹窗模式：每个功能不再独立弹窗，
/// 而是作为一个 <see cref="ContentPage"/> 嵌入 <see cref="MainWindow"/> 右侧内容区，
/// 通过左侧导航切换显示。
///
/// 子类只需在构造函数中设置 <see cref="Title"/> 与 <see cref="Content"/>，
/// 并复用本类提供的按钮工厂方法保持视觉一致。
/// 所有颜色 / 字体 / 圆角一律引用 <see cref="Theme"/> 常量。
/// </summary>
public abstract class ContentPage : UserControl
{
    /// <summary>页面标题 — 显示在主窗口顶部标题栏</summary>
    public string Title { get; protected set; } = string.Empty;

    /// <summary>导航标识 — 与主窗口左侧导航项一一对应，供编程式跳转</summary>
    public string NavId { get; protected set; } = string.Empty;

    /// <summary>
    /// 构造内容页 — 设置默认字体与背景透明（由主窗口外壳提供整体底色）
    /// </summary>
    protected ContentPage()
    {
        FontFamily = Theme.UiFont;
        Focusable = true;
    }

    /// <summary>
    /// 创建分区小标题（参考设计稿分组标题样式：小号大写字母 + muted 颜色）
    /// </summary>
    /// <param name="text">标题文本</param>
    /// <returns>带统一样式的 TextBlock</returns>
    public static TextBlock BuildHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = Theme.UiFont,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(2, 14, 0, 6),
        };
    }

    /// <summary>创建品牌色主按钮（统一高度 / 字号 / 圆角）</summary>
    protected static Button CreatePrimaryButton(string text, double width = 0)
    {
        var btn = new Button
        {
            Content = text,
            Height = 32,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Background = Theme.PrimaryBrush,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(16, 0, 16, 0),
        };
        if (width > 0) btn.Width = width;
        return btn;
    }

    /// <summary>创建次级按钮（透明底 + 边框）</summary>
    protected static Button CreateSecondaryButton(string text, double width = 0)
    {
        var btn = new Button
        {
            Content = text,
            Height = 32,
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Background = Theme.InputBackground,
            Foreground = Theme.TextRegular,
            BorderBrush = Theme.InputBorder,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(16, 0, 16, 0),
        };
        if (width > 0) btn.Width = width;
        return btn;
    }
}
