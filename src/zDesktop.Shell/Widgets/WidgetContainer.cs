using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using zDesktop.Core.Widgets;
using zDesktop.Shell.Styles;

namespace zDesktop.Shell.Widgets;

/// <summary>
/// 组件容器 — 包裹 WidgetBase，提供拖拽、缩放、视觉边框、标题栏
///
/// 职责：
/// 1. 玻璃拟态视觉边框（圆角、半透明、阴影）
/// 2. 标题栏拖拽移动组件
/// 3. 右下角缩放手柄调整大小（仅 AllowResize=true 的组件）
/// 4. 命中测试边界计算（供 WidgetHost 聚合）
/// </summary>
public class WidgetContainer : Border
{
    private readonly WidgetBase _widget;
    private Point _dragStartPoint;
    private Point _dragStartOrigin;
    private bool _isDragging;

    /// <summary>
    /// 持久可见性标记 — 区别于运行时 Visibility。
    /// 托盘"隐藏全部组件"只改 Visibility（临时视图切换），不污染此标记；
    /// 仅用户关闭单个组件或通过面板操作时才置 false。
    /// GetCurrentLayout 据此持久化，避免重启后组件全消失。
    /// </summary>
    public bool PersistedVisible { get; set; } = true;

    // 缩放状态
    private bool _isResizing;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private const double MinWidgetWidth = 180;
    private const double MinWidgetHeight = 120;

    /// <summary>拖拽完成回调（用于持久化位置）</summary>
    public event Action<WidgetContainer, double, double>? PositionChanged;

    /// <summary>缩放完成回调（用于持久化尺寸）</summary>
    public event Action<WidgetContainer, double, double>? WidgetSizeChanged;

    /// <summary>关闭按钮回调</summary>
    public event Action<WidgetContainer>? CloseRequested;

    /// <summary>设置按钮回调（打开组件配置面板）</summary>
    public event Action<WidgetContainer>? SettingsRequested;

    /// <summary>被包裹的组件实例</summary>
    public WidgetBase Widget => _widget;

    public WidgetContainer(WidgetBase widget)
    {
        _widget = widget;

        // ===== 容器视觉样式 — 玻璃拟态（参考设计稿 token）=====
        Background = Theme.ContainerBackground;
        BorderBrush = Theme.ContainerBorder;
        BorderThickness = new Thickness(1);
        CornerRadius = Theme.ContainerRadius;
        Padding = new Thickness(0);
        Effect = new DropShadowEffect
        {
            Color = Theme.ShadowColor,
            BlurRadius = 32,
            ShadowDepth = 4,
            Opacity = 0.5,
            Direction = 270,
        };

        // ===== 布局：标题栏 + 内容区 + 缩放手柄 =====
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // --- 标题栏 ---
        var header = new Border
        {
            Height = 36,
            Background = Theme.HeaderBackground,
            CornerRadius = Theme.HeaderRadius,
            Cursor = Cursors.SizeAll,
        };

        var headerPanel = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 0, 2, 0) };

        // 拖拽手柄图标
        var grip = new TextBlock
        {
            Text = "⋮⋮",
            FontSize = 10,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextFaint,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0),
        };
        DockPanel.SetDock(grip, Dock.Left);
        headerPanel.Children.Add(grip);

        // 关闭按钮
        var closeBtn = new Button
        {
            Content = "✕",
            Width = 24,
            Height = 24,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.TextSecondary,
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 5, 4, 5),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0),
        };
        closeBtn.Click += (_, _) => CloseRequested?.Invoke(this);
        DockPanel.SetDock(closeBtn, Dock.Right);
        headerPanel.Children.Add(closeBtn);

        // 设置按钮（齿轮）— 仅当组件有配置项时显示
        if (widget.Descriptor.ConfigSchema.Count > 0)
        {
            var settingsBtn = new Button
            {
                Content = "⚙",
                Width = 24,
                Height = 24,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Theme.TextSecondary,
                FontSize = 12,
                FontFamily = Theme.UiFont,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 5, 2, 5),
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(0),
            };
            settingsBtn.Click += (_, _) => SettingsRequested?.Invoke(this);
            DockPanel.SetDock(settingsBtn, Dock.Right);
            headerPanel.Children.Add(settingsBtn);
        }

        // 标题文字
        var title = new TextBlock
        {
            Text = widget.Descriptor.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerPanel.Children.Add(title);

        header.Child = headerPanel;
        header.MouseLeftButtonDown += OnHeaderMouseDown;

        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        // --- 组件内容 ---
        _widget.Margin = new Thickness(2, 0, 2, 2);
        _widget.FontFamily = Theme.UiFont;
        Grid.SetRow(_widget, 1);
        grid.Children.Add(_widget);

        // --- 缩放手柄（仅允许缩放的组件显示）---
        if (widget.Descriptor.ResizeMode != WidgetResizeMode.None)
        {
            var resizeHandle = CreateResizeHandle(widget.Descriptor.ResizeMode);
            // 放在内容区（row 1），浮在内容右下角，避免和标题栏按钮重叠
            Grid.SetRow(resizeHandle, 1);
            grid.Children.Add(resizeHandle);
        }

        Child = grid;

        _widget.OnInitialize();
    }

    /// <summary>缩放模式（缓存描述符值，避免每次拖拽都访问 Descriptor）</summary>
    private WidgetResizeMode _resizeMode = WidgetResizeMode.None;

    /// <summary>创建缩放手柄 — 根据模式决定位置与光标</summary>
    private UIElement CreateResizeHandle(WidgetResizeMode mode)
    {
        _resizeMode = mode;

        var isHeightOnly = mode == WidgetResizeMode.HeightOnly;
        var cursor = isHeightOnly ? Cursors.SizeNS : Cursors.SizeNWSE;

        // 手柄视觉：HeightOnly 显示底部横条，Both 显示右下角三角
        Path handle;
        Border hitArea;

        if (isHeightOnly)
        {
            // 底部中间横条手柄
            handle = new Path
            {
                Width = 32,
                Height = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 3),
                Cursor = cursor,
                Stroke = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                StrokeThickness = 1.5,
                Data = new LineGeometry(new Point(0, 2), new Point(32, 2)),
            };

            hitArea = new Border
            {
                Width = 60,
                Height = 14,
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Cursor = cursor,
            };
        }
        else
        {
            // 右下角三角手柄（原有 Both 模式）
            handle = new Path
            {
                Width = 14,
                Height = 14,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 2, 2),
                Cursor = cursor,
                Stroke = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                StrokeThickness = 1.5,
                Data = new PathGeometry(new[]
                {
                    new PathFigure(new Point(14, 4), new[]
                    {
                        new LineSegment(new Point(14, 14), true),
                        new LineSegment(new Point(4, 14), true),
                    }, false),
                }),
            };

            hitArea = new Border
            {
                Width = 22,
                Height = 22,
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Cursor = cursor,
            };
        }

        hitArea.MouseLeftButtonDown += OnResizeMouseDown;
        hitArea.Child = handle;

        return hitArea;
    }

    // ===== 拖拽移动 =====

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _dragStartPoint = e.GetPosition(null);

        if (Parent is Canvas canvas)
        {
            _dragStartOrigin = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
        }

        CaptureMouse();
        MouseMove += OnDragMove;
        MouseLeftButtonUp += OnDragEnd;
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        var currentPos = e.GetPosition(null);
        var dx = currentPos.X - _dragStartPoint.X;
        var dy = currentPos.Y - _dragStartPoint.Y;

        var host = Parent as WidgetHost;
        var newX = _dragStartOrigin.X + dx;
        var newY = _dragStartOrigin.Y + dy;

        if (host != null)
        {
            // 边界约束 — 不超出画布
            newX = Math.Max(0, Math.Min(newX, host.ActualWidth - ActualWidth));
            newY = Math.Max(0, Math.Min(newY, host.ActualHeight - ActualHeight));

            // 对齐吸附 — 屏幕中线 + 其他组件边缘
            var (sx, sy, showV, lineX, showH, lineY) = host.CalculateSnap(this, newX, newY);
            // 吸附后再次约束到画布内
            sx = Math.Max(0, Math.Min(sx, host.ActualWidth - ActualWidth));
            sy = Math.Max(0, Math.Min(sy, host.ActualHeight - ActualHeight));
            host.UpdateGuideLines(showV, lineX, showH, lineY);

            Canvas.SetLeft(this, sx);
            Canvas.SetTop(this, sy);
        }
        else
        {
            Canvas.SetLeft(this, newX);
            Canvas.SetTop(this, newY);
        }
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        MouseMove -= OnDragMove;
        MouseLeftButtonUp -= OnDragEnd;
        ReleaseMouseCapture();

        // 隐藏对齐辅助线
        if (Parent is WidgetHost host)
            host.HideGuideLines();

        PositionChanged?.Invoke(this, Canvas.GetLeft(this), Canvas.GetTop(this));
    }

    // ===== 缩放调整大小 =====

    private void OnResizeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isResizing = true;
        _resizeStartWidth = ActualWidth;
        _resizeStartHeight = ActualHeight;
        _dragStartPoint = e.GetPosition(null);

        ((UIElement)sender).CaptureMouse();
        MouseMove += OnResizeMove;
        MouseLeftButtonUp += OnResizeEnd;
        e.Handled = true;
    }

    private void OnResizeMove(object sender, MouseEventArgs e)
    {
        if (!_isResizing) return;

        var currentPos = e.GetPosition(null);
        var dx = currentPos.X - _dragStartPoint.X;
        var dy = currentPos.Y - _dragStartPoint.Y;

        var newHeight = Math.Max(MinWidgetHeight, _resizeStartHeight + dy);

        // 边界约束 — 不超出画布
        var canvas = Parent as Canvas;
        if (canvas != null)
        {
            var top = Canvas.GetTop(this);
            newHeight = Math.Min(newHeight, canvas.ActualHeight - top);
        }

        // HeightOnly 模式：仅改高度，宽度锁定
        if (_resizeMode == WidgetResizeMode.HeightOnly)
        {
            Height = newHeight;
        }
        else
        {
            // Both 模式：宽高均可调
            var newWidth = Math.Max(MinWidgetWidth, _resizeStartWidth + dx);
            if (canvas != null)
            {
                var left = Canvas.GetLeft(this);
                newWidth = Math.Min(newWidth, canvas.ActualWidth - left);
            }
            Width = newWidth;
            Height = newHeight;
        }
    }

    private void OnResizeEnd(object sender, MouseButtonEventArgs e)
    {
        _isResizing = false;
        MouseMove -= OnResizeMove;
        MouseLeftButtonUp -= OnResizeEnd;
        ((UIElement)sender).ReleaseMouseCapture();

        WidgetSizeChanged?.Invoke(this, Width, Height);
    }

    /// <summary>
    /// 命中测试 — 判断窗口坐标点是否在此容器范围内
    /// </summary>
    public bool HitTest(Point point)
    {
        var left = Canvas.GetLeft(this);
        var top = Canvas.GetTop(this);

        return point.X >= left && point.X <= left + ActualWidth &&
               point.Y >= top && point.Y <= top + ActualHeight;
    }
}
