using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using zDesktop.Shell.Styles;

namespace zDesktop.App.Panels;

/// <summary>
/// 功能面板窗口基类 — 统一玻璃拟态视觉 + 无边框透明窗口 + 失焦关闭
///
/// 子类只需设置 Title 和填充 ContentArea（一个 StackPanel/DockPanel）。
/// 视觉：圆角半透明卡片 + 阴影 + 标题栏 + 关闭按钮
/// 行为：Topmost + 不显示在任务栏 + 失活自动关闭（可禁用）
/// </summary>
public abstract class PanelWindowBase : Window
{
    /// <summary>标题栏</summary>
    protected readonly TextBlock TitleText;

    /// <summary>内容区域 — 子类将 UI 元素添加到此容器</summary>
    protected readonly Panel ContentArea;

    /// <summary>是否在失活时自动关闭（默认 true，子面板如设置面板需设为 false）</summary>
    protected bool CloseOnDeactivate { get; set; } = true;

    /// <summary>失活关闭抑制标记（打开子窗口时临时禁用）</summary>
    private bool _suppressCloseOnDeactivate;

    /// <summary>是否已通过关闭按钮关闭（避免重复处理）</summary>
    private bool _manuallyClosed;

    protected PanelWindowBase(string title, double width, double height, Panel contentPanel)
    {
        ContentArea = contentPanel;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        Width = width;
        Height = height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Deactivated += OnDeactivated;
        KeyDown += OnKeyDown;

        // ===== 外层玻璃拟态容器 =====
        var outerBorder = new Border
        {
            Background = Theme.ContainerBackground,
            BorderBrush = Theme.ContainerBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ContainerRadius,
            Effect = new DropShadowEffect
            {
                Color = Theme.ShadowColor,
                BlurRadius = 40,
                ShadowDepth = 8,
                Opacity = 0.5,
            },
            Padding = new Thickness(0),
        };

        var mainPanel = new StackPanel();

        // --- 标题栏 ---
        var headerPanel = CreateTitleBar(title, out TitleText);
        mainPanel.Children.Add(headerPanel);

        // --- 分隔线 ---
        mainPanel.Children.Add(new Border
        {
            Height = 1,
            Background = Theme.Divider,
            Margin = new Thickness(0),
        });

        // --- 内容区 ---
        contentPanel.Margin = new Thickness(0);
        mainPanel.Children.Add(contentPanel);

        outerBorder.Child = mainPanel;
        Content = outerBorder;
    }

    /// <summary>创建标题栏</summary>
    private DockPanel CreateTitleBar(string title, out TextBlock titleText)
    {
        var header = new DockPanel
        {
            Height = 44,
            Margin = new Thickness(16, 14, 10, 10),
            LastChildFill = true,
        };

        // 标题
        titleText = new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(titleText);

        // 关闭按钮
        var closeBtn = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 28,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.TextSecondary,
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Cursor = Cursors.Hand,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0),
        };
        closeBtn.Click += (_, _) =>
        {
            _manuallyClosed = true;
            Close();
        };
        DockPanel.SetDock(closeBtn, Dock.Right);
        header.Children.Add(closeBtn);

        return header;
    }

    /// <summary>抑制失活关闭（打开子窗口前调用）</summary>
    public void SuppressCloseOnDeactivate()
    {
        _suppressCloseOnDeactivate = true;
    }

    /// <summary>恢复失活关闭（子窗口关闭后调用）</summary>
    public void ResumeCloseOnDeactivate()
    {
        _suppressCloseOnDeactivate = false;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (CloseOnDeactivate && !_suppressCloseOnDeactivate && !_manuallyClosed)
        {
            Close();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _manuallyClosed = true;
            Close();
            e.Handled = true;
        }
    }

    /// <summary>创建标准按钮样式（品牌色主按钮）</summary>
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

    /// <summary>创建次级按钮样式（透明底）</summary>
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
