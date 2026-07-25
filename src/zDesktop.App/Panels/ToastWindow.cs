using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using zDesktop.Shell.Styles;

namespace zDesktop.App.Panels;

/// <summary>一个可点击的动作按钮定义</summary>
/// <param name="Text">按钮文字</param>
/// <param name="IsPrimary">是否主按钮（品牌色实心）</param>
/// <param name="OnClick">点击回调；调用后 toast 自动关闭</param>
public sealed record ToastAction(string Text, bool IsPrimary, Action OnClick);

/// <summary>
/// 右下角非模态提示卡片 —— 引导卡片与撤销提示共用（设计案 v3.1 §六）。
///
/// <para><b>非模态、可直接关掉</b>：首次运行的引导绝不能是模态对话框挡住桌面，
/// 那与「装上之后桌面一个像素都不变」的观感直接冲突。</para>
///
/// <para>不抢焦点（<c>ShowActivated=false</c>），不进任务栏与 Alt+Tab。</para>
/// </summary>
public sealed class ToastWindow : Window
{
    private readonly DispatcherTimer? _autoClose;
    private readonly TextBlock _countdownText;
    private int _secondsLeft;

    /// <summary>
    /// 构造提示卡片。
    /// </summary>
    /// <param name="title">标题</param>
    /// <param name="message">正文</param>
    /// <param name="actions">动作按钮，从左到右</param>
    /// <param name="autoCloseSeconds">自动关闭倒计时；0 表示不自动关闭</param>
    public ToastWindow(string title, string message, IReadOnlyList<ToastAction> actions, int autoCloseSeconds = 0)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;              // 提示需要可见，但它是短时存在的，不违反「不常驻置顶」
        ShowActivated = false;       // 不抢焦点
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.Height;
        Width = 380;

        var panel = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };

        panel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Theme.TextPrimary,
            FontFamily = UiFont,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8),
        });

        panel.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Theme.TextSecondary,
            FontFamily = UiFont,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        });

        _countdownText = new TextBlock
        {
            Foreground = Theme.TextFaint,
            FontFamily = UiFont,
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(_countdownText);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        foreach (var action in actions)
            buttonRow.Children.Add(MakeButton(action));
        panel.Children.Add(buttonRow);

        Content = new Border
        {
            Background = Theme.ContainerBackground,
            BorderBrush = Theme.ContainerBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ContainerRadius,
            Child = panel,
        };

        if (autoCloseSeconds > 0)
        {
            _secondsLeft = autoCloseSeconds;
            _countdownText.Visibility = Visibility.Visible;
            UpdateCountdown();

            _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _autoClose.Tick += (_, _) =>
            {
                _secondsLeft--;
                if (_secondsLeft <= 0) { _autoClose.Stop(); Close(); }
                else UpdateCountdown();
            };
            _autoClose.Start();
        }

        Loaded += (_, _) => PositionBottomRight();
        Closed += (_, _) => _autoClose?.Stop();
    }

    /// <summary>中文字体必须显式指定，否则走字体回退会把小字号中文糊掉（M3 实测）</summary>
    private static readonly System.Windows.Media.FontFamily UiFont =
        new("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");

    private void UpdateCountdown() => _countdownText.Text = $"{_secondsLeft} 秒后自动关闭";

    /// <summary>贴主屏工作区右下角，避开任务栏</summary>
    private void PositionBottomRight()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 20;
        Top = work.Bottom - ActualHeight - 20;
    }

    private Button MakeButton(ToastAction action)
    {
        var button = new Button
        {
            Content = action.Text,
            MinWidth = 76,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
            FontFamily = UiFont,
            FontSize = 12,
            Foreground = action.IsPrimary ? Brushes.White : Theme.TextRegular,
            Background = action.IsPrimary ? Theme.PrimaryBrush : Theme.ListItemBackground,
            BorderBrush = action.IsPrimary ? Theme.PrimaryBrush : Theme.InputBorder,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };

        button.Click += (_, _) =>
        {
            _autoClose?.Stop();
            Close();
            // 先关卡片再执行回调：回调里可能弹新窗口，不该被这张卡片挡着
            action.OnClick();
        };

        return button;
    }

    /// <summary>弹出一张提示卡片</summary>
    public static ToastWindow Show(string title, string message,
        IReadOnlyList<ToastAction> actions, int autoCloseSeconds = 0)
    {
        var toast = new ToastWindow(title, message, actions, autoCloseSeconds);
        toast.Show();
        return toast;
    }
}
