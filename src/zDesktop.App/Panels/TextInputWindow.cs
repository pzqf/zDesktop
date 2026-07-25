using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using zDesktop.Shell.Styles;

namespace zDesktop.App.Panels;

/// <summary>
/// 单行文本输入对话框（重命名分区等）。
///
/// 取代 <c>Microsoft.VisualBasic.Interaction.InputBox</c> —— 后者外观是 Win9x 风格，
/// 与应用的深色主题完全冲突，且引入了不必要的 VB 运行时依赖。
/// </summary>
public sealed class TextInputWindow : Window
{
    private readonly TextBox _input;

    /// <summary>用户确认后的输入值；取消时为 null</summary>
    public string? Result { get; private set; }

    public TextInputWindow(string title, string prompt, string initialValue = "")
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = Theme.ContainerBackground;
        BorderBrush = Theme.ContainerBorder;
        BorderThickness = new Thickness(1);

        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Theme.TextPrimary,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        });

        root.Children.Add(new TextBlock
        {
            Text = prompt,
            Foreground = Theme.TextSecondary,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8),
        });

        _input = new TextBox
        {
            Text = initialValue,
            Background = Theme.InputBackground,
            Foreground = Theme.TextPrimary,
            BorderBrush = Theme.InputBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 13,
            CaretBrush = Theme.TextPrimary,
        };
        _input.KeyDown += OnInputKeyDown;
        root.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(MakeButton("取消", isPrimary: false, () => Close()));
        buttons.Children.Add(MakeButton("确定", isPrimary: true, Confirm));
        root.Children.Add(buttons);

        Content = new Border
        {
            Background = Theme.ContainerBackground,
            CornerRadius = Theme.ContainerRadius,
            Child = root,
        };

        // 无边框窗口需要自己支持拖动
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    private Button MakeButton(string text, bool isPrimary, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 76,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = isPrimary ? Brushes.White : Theme.TextRegular,
            Background = isPrimary ? Theme.PrimaryBrush : Theme.ListItemBackground,
            BorderBrush = isPrimary ? Theme.PrimaryBrush : Theme.InputBorder,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            FontSize = 12,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Confirm(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void Confirm()
    {
        var text = _input.Text?.Trim();
        Result = string.IsNullOrEmpty(text) ? null : text;
        Close();
    }

    /// <summary>弹出对话框并返回输入值；取消或留空返回 null</summary>
    public static string? Prompt(string title, string prompt, string initialValue = "")
    {
        var window = new TextInputWindow(title, prompt, initialValue);
        window.ShowDialog();
        return window.Result;
    }
}
