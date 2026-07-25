using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using zDesktop.Shell.Styles;

namespace zDesktop.Shell.Desktop;

/// <summary>
/// 桌面搜索框 — 常驻桌面右上角的全局搜索入口
///
/// 视觉：半透明玻璃拟态胶囊形搜索框，搜索图标 + 占位文字
/// 交互：点击聚焦输入，回车触发 SearchRequested 事件（由 App 打开主窗口全局搜索页）
/// 定位：由父容器用 HorizontalAlignment=Right / VerticalAlignment=Top + Margin 控制到右上角
/// </summary>
public sealed class DesktopSearchBar : Border
{
    /// <summary>搜索框输入控件</summary>
    private readonly TextBox _input;

    /// <summary>用户回车提交搜索时触发，参数为搜索关键词</summary>
    public event Action<string>? SearchRequested;

    /// <summary>请求打开搜索面板（点击搜索框时触发，可用于预加载）</summary>
    public event Action? Activated;

    /// <summary>
    /// 构造桌面搜索框
    /// </summary>
    /// <param name="width">搜索框宽度（默认 320）</param>
    public DesktopSearchBar(double width = 320)
    {
        Width = width;
        Height = 40;
        CornerRadius = new CornerRadius(20); // 胶囊形
        Background = Theme.ContainerBackground;
        BorderBrush = Theme.ContainerBorder;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(14, 0, 14, 0);
        Cursor = Cursors.IBeam;
        Focusable = true;

        var panel = new DockPanel { LastChildFill = true };

        // 搜索图标
        var icon = new TextBlock
        {
            Text = "🔍",
            FontSize = 14,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        DockPanel.SetDock(icon, Dock.Left);
        panel.Children.Add(icon);

        // 输入框
        _input = new TextBox
        {
            FontSize = 13,
            FontFamily = Theme.UiFont,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.TextRegular,
            CaretBrush = Brushes.White,
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = "搜索应用、文件、网页…", // 占位提示
        };
        _input.GotFocus += OnInputGotFocus;
        _input.LostFocus += OnInputLostFocus;
        _input.KeyDown += OnInputKeyDown;
        UpdatePlaceholder();
        panel.Children.Add(_input);

        Child = panel;

        // 点击 Border 任意区域聚焦输入框
        MouseLeftButtonDown += (_, _) => _input.Focus();
    }

    /// <summary>输入框获得焦点 — 触发激活事件 + 清除占位</summary>
    private void OnInputGotFocus(object sender, RoutedEventArgs e)
    {
        Activated?.Invoke();
        _input.Text = string.Empty;
        _input.Foreground = Theme.TextRegular;
    }

    /// <summary>输入框失去焦点 — 恢复占位提示</summary>
    private void OnInputLostFocus(object sender, RoutedEventArgs e)
    {
        UpdatePlaceholder();
    }

    /// <summary>回车提交搜索</summary>
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var query = _input.Text.Trim();
            if (!string.IsNullOrEmpty(query))
            {
                SearchRequested?.Invoke(query);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _input.Text = string.Empty;
            UpdatePlaceholder();
            e.Handled = true;
        }
    }

    /// <summary>更新占位提示文字（空输入时显示灰色提示）</summary>
    private void UpdatePlaceholder()
    {
        if (string.IsNullOrEmpty(_input.Text))
        {
            _input.Text = "搜索应用、文件、网页…";
            _input.Foreground = Theme.TextFaint;
        }
    }

    /// <summary>命中测试 — 判断点是否在搜索框范围内（供 HitTestCallback 使用）</summary>
    public bool HitTest(Point point)
    {
        return point.X >= 0 && point.X <= ActualWidth &&
               point.Y >= 0 && point.Y <= ActualHeight;
    }
}
