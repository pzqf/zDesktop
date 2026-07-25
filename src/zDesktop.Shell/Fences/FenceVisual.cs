using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using zDesktop.Core.Fences;
using zDesktop.Shell.Styles;

namespace zDesktop.Shell.Fences;

/// <summary>
/// 单个分区在覆盖层上的交互外观。
///
/// <para><b>只画标题栏，不画背景</b> —— 分区背景已经合成进壁纸（§4.3 候选 A），
/// 位于桌面图标**下方**。覆盖层在图标**上方**，若在这里再画一层背景，
/// 就会把分区里的图标盖住。</para>
///
/// <para><b>命中区域必须最小化</b>（零破坏契约）：只有标题栏和右下角缩放角捕获鼠标，
/// 分区主体区域完全透传，保证里面的原生图标照常可点、可拖、可右键。</para>
/// </summary>
public sealed class FenceVisual : Canvas
{
    /// <summary>标题栏高度（DIP）</summary>
    public const double TitleHeight = 32;

    /// <summary>右下角缩放角边长（DIP）</summary>
    public const double GripSize = 16;

    private readonly Border _titleBar;
    private readonly TextBlock _nameText;
    private readonly TextBlock _collapseGlyph;
    private readonly Border _grip;
    private readonly Border _outline;

    private Point _dragStart;
    private bool _dragging;
    private bool _resizing;

    public Fence Fence { get; }

    /// <summary>分区被移动（参数为相对工作区的新 DIP 坐标）</summary>
    public event Action<FenceVisual, double, double>? Moved;

    /// <summary>分区被缩放（参数为新的 DIP 宽高）</summary>
    public event Action<FenceVisual, double, double>? Resized;

    /// <summary>折叠状态被切换</summary>
    public event Action<FenceVisual>? CollapseToggled;

    /// <summary>请求重命名</summary>
    public event Action<FenceVisual>? RenameRequested;

    /// <summary>请求删除</summary>
    public event Action<FenceVisual>? DeleteRequested;

    /// <summary>拖拽/缩放结束（用于触发一次落盘与重新合成）</summary>
    public event Action<FenceVisual>? EditCommitted;

    public FenceVisual(Fence fence)
    {
        Fence = fence;
        Background = null;               // 主体不接收鼠标
        IsHitTestVisible = true;

        var accent = ParseColor(fence.Color);

        // 编辑模式下才显示的整体轮廓，帮助用户看清分区范围
        _outline = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Children.Add(_outline);

        // ===== 标题栏 =====
        _nameText = new TextBlock
        {
            Text = fence.Name,
            Foreground = Theme.TextPrimary,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(10, 0, 6, 0),
        };

        _collapseGlyph = new TextBlock
        {
            Text = fence.Collapsed ? "▸" : "▾",
            Foreground = Theme.TextSecondary,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };

        var titleGrid = new Grid();
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_nameText, 0);
        Grid.SetColumn(_collapseGlyph, 1);
        titleGrid.Children.Add(_nameText);
        titleGrid.Children.Add(_collapseGlyph);

        _titleBar = new Border
        {
            Height = TitleHeight,
            Background = new SolidColorBrush(Color.FromArgb(150, 22, 24, 36)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(110, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Cursor = Cursors.SizeAll,
            Child = titleGrid,
        };

        _titleBar.MouseLeftButtonDown += OnTitleMouseDown;
        _titleBar.MouseMove += OnTitleMouseMove;
        _titleBar.MouseLeftButtonUp += OnTitleMouseUp;
        _titleBar.MouseRightButtonUp += OnTitleRightClick;
        Children.Add(_titleBar);

        // ===== 右下角缩放角 =====
        _grip = new Border
        {
            Width = GripSize,
            Height = GripSize,
            Background = new SolidColorBrush(Color.FromArgb(90, accent.R, accent.G, accent.B)),
            CornerRadius = Theme.SmallRadius,
            Cursor = Cursors.SizeNWSE,
            Visibility = Visibility.Collapsed,
        };
        _grip.MouseLeftButtonDown += OnGripMouseDown;
        _grip.MouseMove += OnGripMouseMove;
        _grip.MouseLeftButtonUp += OnGripMouseUp;
        Children.Add(_grip);

        ApplyGeometry();
    }

    /// <summary>编辑模式下显示轮廓与缩放角</summary>
    public void SetEditMode(bool editing)
    {
        _outline.Visibility = editing && !Fence.Collapsed ? Visibility.Visible : Visibility.Collapsed;
        _grip.Visibility = editing && !Fence.Collapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>按 Fence 模型刷新位置、尺寸与文字</summary>
    public void ApplyGeometry()
    {
        var r = Fence.Rect;

        SetLeft(this, r.X);
        SetTop(this, r.Y);
        Width = r.Width;
        Height = Fence.Collapsed ? TitleHeight : r.Height;

        _titleBar.Width = r.Width;
        SetLeft(_titleBar, 0);
        SetTop(_titleBar, 0);

        _outline.Width = r.Width;
        _outline.Height = r.Height;
        SetLeft(_outline, 0);
        SetTop(_outline, 0);

        SetLeft(_grip, Math.Max(0, r.Width - GripSize));
        SetTop(_grip, Math.Max(TitleHeight, r.Height - GripSize));

        _nameText.Text = Fence.Name;
        _collapseGlyph.Text = Fence.Collapsed ? "▸" : "▾";
    }

    /// <summary>
    /// 命中测试 —— 供覆盖层聚合。
    /// 坐标为 FenceVisual 所在 Canvas 的坐标（即相对工作区的 DIP）。
    ///
    /// 只有标题栏与缩放角命中，主体一律不命中，保证内部原生图标可操作。
    /// </summary>
    public bool HitTest(Point pointInLayer)
    {
        var local = new Point(pointInLayer.X - GetLeft(this), pointInLayer.Y - GetTop(this));

        if (local.X < 0 || local.Y < 0 || local.X > Width) return false;

        // 标题栏
        if (local.Y >= 0 && local.Y <= TitleHeight) return true;

        // 缩放角（仅编辑模式可见时才命中）
        if (_grip.Visibility == Visibility.Visible)
        {
            var gx = GetLeft(_grip);
            var gy = GetTop(_grip);
            if (local.X >= gx && local.X <= gx + GripSize &&
                local.Y >= gy && local.Y <= gy + GripSize) return true;
        }

        return false;
    }

    // ===== 标题栏交互 =====

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Fence.Collapsed = !Fence.Collapsed;
            ApplyGeometry();
            CollapseToggled?.Invoke(this);
            e.Handled = true;
            return;
        }

        _dragStart = e.GetPosition(Parent as UIElement);
        _dragging = true;
        _titleBar.CaptureMouse();
        e.Handled = true;
    }

    private void OnTitleMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var now = e.GetPosition(Parent as UIElement);
        var dx = now.X - _dragStart.X;
        var dy = now.Y - _dragStart.Y;
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return;

        // 不允许拖出工作区左上角，否则分区会永久失联
        Fence.Rect.X = Math.Max(0, Fence.Rect.X + dx);
        Fence.Rect.Y = Math.Max(0, Fence.Rect.Y + dy);
        _dragStart = now;

        ApplyGeometry();
        Moved?.Invoke(this, Fence.Rect.X, Fence.Rect.Y);
    }

    private void OnTitleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _titleBar.ReleaseMouseCapture();
        // 松手才落盘与重新合成 —— 拖动过程中每帧合成会掉帧（§4.3 实测 4K 37.8ms）
        EditCommitted?.Invoke(this);
        e.Handled = true;
    }

    private void OnTitleRightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();

        var rename = new MenuItem { Header = "重命名" };
        rename.Click += (_, _) => RenameRequested?.Invoke(this);
        menu.Items.Add(rename);

        var collapse = new MenuItem { Header = Fence.Collapsed ? "展开" : "折叠" };
        collapse.Click += (_, _) =>
        {
            Fence.Collapsed = !Fence.Collapsed;
            ApplyGeometry();
            CollapseToggled?.Invoke(this);
        };
        menu.Items.Add(collapse);

        menu.Items.Add(new Separator());

        var del = new MenuItem { Header = "删除分区" };
        del.Click += (_, _) => DeleteRequested?.Invoke(this);
        menu.Items.Add(del);

        menu.PlacementTarget = _titleBar;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ===== 缩放角交互 =====

    private void OnGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Parent as UIElement);
        _resizing = true;
        _grip.CaptureMouse();
        e.Handled = true;
    }

    private void OnGripMouseMove(object sender, MouseEventArgs e)
    {
        if (!_resizing) return;

        var now = e.GetPosition(Parent as UIElement);
        var dx = now.X - _dragStart.X;
        var dy = now.Y - _dragStart.Y;

        // 最小尺寸保证标题栏和缩放角不会重叠到无法操作
        Fence.Rect.Width = Math.Max(120, Fence.Rect.Width + dx);
        Fence.Rect.Height = Math.Max(TitleHeight + GripSize + 20, Fence.Rect.Height + dy);
        _dragStart = now;

        ApplyGeometry();
        Resized?.Invoke(this, Fence.Rect.Width, Fence.Rect.Height);
    }

    private void OnGripMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        _grip.ReleaseMouseCapture();
        EditCommitted?.Invoke(this);
        e.Handled = true;
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            if (!string.IsNullOrEmpty(hex) && hex.StartsWith('#') && hex.Length == 7)
            {
                return Color.FromRgb(
                    Convert.ToByte(hex.Substring(1, 2), 16),
                    Convert.ToByte(hex.Substring(3, 2), 16),
                    Convert.ToByte(hex.Substring(5, 2), 16));
            }
        }
        catch
        {
            // 配色格式错误用品牌色兜底
        }
        return Color.FromRgb(0x6c, 0x5c, 0xe7);
    }
}
