using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using zDesktop.Core.DesktopIcons;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.DesktopIcons;

/// <summary>
/// 单个桌面图标项 — 图标图片 + 文字标签 + 选中态 + 双击打开 + 拖拽移动 + 右键原生菜单
///
/// 视觉：48x48 图标 + 最多两行居中文字，选中时品牌紫半透明背景
/// 交互：单击选中、双击打开、拖拽移动（结束时网格吸附）、右键弹出 Windows 原生菜单
/// </summary>
public class DesktopIconItem : UserControl
{
    private static readonly SolidColorBrush SelectedBrush =
        new(Color.FromArgb(90, 108, 92, 231));
    private static readonly SolidColorBrush HoverBrush =
        new(Color.FromArgb(40, 255, 255, 255));

    private readonly DesktopIconInfo _info;
    private readonly Border _shell;
    private bool _isSelected;

    /// <summary>获取所有者窗口句柄的回调（用于原生右键菜单）</summary>
    public static Func<IntPtr>? GetOwnerHwnd { get; set; }

    // 拖拽状态
    private Point _dragStartPoint;
    private Point _dragStartOrigin;
    private bool _isDragging;
    private bool _movedDuringDrag;

    /// <summary>拖拽结束回调（用于持久化位置）</summary>
    public event Action<DesktopIconItem>? PositionChanged;

    /// <summary>图标信息</summary>
    public DesktopIconInfo Info => _info;

    /// <summary>是否选中</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            _shell.Background = value ? SelectedBrush : Brushes.Transparent;
        }
    }

    public DesktopIconItem(DesktopIconInfo info, ImageSource? icon)
    {
        _info = info;
        Width = 76;
        Focusable = true;

        // 图标图片
        var image = new Image
        {
            Source = icon,
            Width = 48,
            Height = 48,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 2),
        };

        // 文字标签 — 居中、最多两行、自动换行
        var label = new TextBlock
        {
            Text = info.DisplayName,
            FontSize = 12,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 2),
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        panel.Children.Add(image);
        panel.Children.Add(label);

        _shell = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2),
            Child = panel,
        };

        Content = _shell;

        // 文字阴影提升壁纸上的可读性
        var shadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 6,
            ShadowDepth = 1,
            Opacity = 0.9,
        };
        label.Effect = shadow;

        // 事件
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseEnter += (_, _) => { if (!_isSelected) _shell.Background = HoverBrush; };
        MouseLeave += (_, _) => { if (!_isSelected) _shell.Background = Brushes.Transparent; };
        MouseDoubleClick += OnDoubleClick;
        MouseRightButtonUp += OnRightClick;
    }

    /// <summary>右键 — 弹出 Windows 原生右键菜单</summary>
    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        // 选中当前图标
        if (Parent is DesktopIconLayer layer)
            layer.SelectOnly(this);
        else
            IsSelected = true;

        // 获取鼠标屏幕坐标
        var pos = e.GetPosition(null);
        var presentationSource = PresentationSource.FromVisual(this);
        var transformToDevice = presentationSource?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var deviceX = (int)(pos.X * transformToDevice.M11);
        var deviceY = (int)(pos.Y * transformToDevice.M22);

        // 获取所有者窗口句柄
        var hwnd = GetOwnerHwnd?.Invoke() ?? IntPtr.Zero;
        var sourcePath = _info.SourcePath;

        // 在 UI 线程异步执行 — 让当前右键事件先完成，再弹出菜单
        // TrackPopupMenuEx 有自己的模态循环，会在菜单显示期间接管消息泵
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ShellContextMenu.Show(sourcePath, deviceX, deviceY, hwnd);
        }));

        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);

        if (Parent is Canvas canvas)
            _dragStartOrigin = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));

        _movedDuringDrag = false;
        CaptureMouse();
        Focus();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;

        var currentPos = e.GetPosition(null);
        var dx = currentPos.X - _dragStartPoint.X;
        var dy = currentPos.Y - _dragStartPoint.Y;

        // 超过阈值才算拖拽（避免单击误判）
        if (!_movedDuringDrag && Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return;
        _movedDuringDrag = true;
        _isDragging = true;

        var canvas = Parent as Canvas;
        var newX = _dragStartOrigin.X + dx;
        var newY = _dragStartOrigin.Y + dy;

        if (canvas != null)
        {
            newX = Math.Max(0, Math.Min(newX, canvas.ActualWidth - ActualWidth));
            newY = Math.Max(0, Math.Min(newY, canvas.ActualHeight - ActualHeight));
        }

        Canvas.SetLeft(this, newX);
        Canvas.SetTop(this, newY);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();

        if (_isDragging)
        {
            // 拖拽结束 — 通知 Layer 做网格吸附与持久化
            PositionChanged?.Invoke(this);
        }
        else
        {
            // 单击 — 选中（由 Layer 协调单选，取消其他）
            if (Parent is DesktopIconLayer layer)
                layer.SelectOnly(this);
            else
                IsSelected = true;
        }

        _isDragging = false;
        _movedDuringDrag = false;
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        Open();
        e.Handled = true;
    }

    /// <summary>打开图标对应的目标</summary>
    public void Open()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _info.SourcePath,
                UseShellExecute = true,
            });
            Console.WriteLine($"[DesktopIconItem] 已打开: {_info.SourcePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DesktopIconItem] 打开失败 {_info.SourcePath}: {ex.Message}");
        }
    }

    /// <summary>命中测试 — 判断点是否在此图标范围内</summary>
    public bool HitTest(Point point)
    {
        var left = Canvas.GetLeft(this);
        var top = Canvas.GetTop(this);
        return point.X >= left && point.X <= left + ActualWidth &&
               point.Y >= top && point.Y <= top + ActualHeight;
    }
}
