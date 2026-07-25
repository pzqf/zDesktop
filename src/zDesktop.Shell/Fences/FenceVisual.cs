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

    /// <summary>
    /// 标题字体。
    ///
    /// <para><b>必须显式指定</b>：不指定时 WPF 用 Segoe UI，而它没有中文字形，
    /// 于是走逐字回退；回退字体再遇上 SemiBold 会触发合成加粗（双绘偏移），
    /// 实测「临时」二字被糊成一团而「工作文件」正常 —— 同样的代码、字号，
    /// 只因回退路径不同。指定含中文的字体族可绕开整条回退路径。</para>
    /// </summary>
    private static readonly FontFamily TitleFont =
        new("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");

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

    /// <summary>
    /// 拖拽/缩放开始与结束。
    ///
    /// <para>覆盖层靠 <c>WM_NCHITTEST</c> 返回 <c>HTTRANSPARENT</c> 实现鼠标透传，
    /// 而这会让 WPF 的 <c>CaptureMouse()</c> 形同虚设 —— 光标一旦移出标题栏那 32 像素，
    /// Win32 就把后续鼠标消息直接投给下层窗口，我们连 WM_MOUSEMOVE 都收不到，
    /// 表现就是「拖动不跟手」。</para>
    ///
    /// <para>因此拖拽期间必须让命中测试无条件返回 true，把整窗暂时变成不透传。
    /// 这两个事件就是通知 <see cref="FenceLayer"/> 切换该状态用的。</para>
    /// </summary>
    public event Action<FenceVisual, bool>? InteractingChanged;

    public FenceVisual(Fence fence)
    {
        Fence = fence;
        Background = null;               // 主体不接收鼠标
        IsHitTestVisible = true;

        var accent = ParseColor(fence.Color);

        // 分区范围指示。编辑模式下只描边；拖拽时填充成实心预览（见 UpdateAdornerVisibility）
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
            FontFamily = TitleFont,
            FontSize = 12.5,
            // 用 Normal 而非 SemiBold：中文字体多数没有 SemiBold 字重，
            // WPF 会合成加粗（双绘偏移），在小字号下直接糊成一团。
            FontWeight = FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(10, 0, 6, 0),
        };

        _collapseGlyph = new TextBlock
        {
            Text = fence.Collapsed ? "▸" : "▾",
            Foreground = Theme.TextSecondary,
            FontFamily = TitleFont,
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
            // 标题栏必须足够不透明。
            // alpha=150 时亮色壁纸（实测一张浅绿树叶图）会透上来把文字吃掉，
            // 标题在任何壁纸下都必须清晰可读，这里不为通透性让步。
            Background = new SolidColorBrush(Color.FromArgb(235, 24, 26, 38)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, accent.R, accent.G, accent.B)),
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

    private bool _editMode;

    /// <summary>编辑模式下显示轮廓与缩放角</summary>
    public void SetEditMode(bool editing)
    {
        _editMode = editing;
        UpdateAdornerVisibility();
    }

    /// <summary>
    /// 更新分区范围指示件。
    ///
    /// <para><b>拖拽期间填充成实心预览</b>：分区底色是合成在壁纸里的，松手之后才会重绘。
    /// 实测松手到重绘之间约有 0.6 秒（去抖 + 合成 + 设壁纸），
    /// 这段时间里旧底色还留在原处、只有标题栏在动，视觉上是两段式的 ——
    /// 用户报告的「闪烁」其实就是这个延迟突跳，而非渐变过渡
    /// （连拍实测最大帧间亮度差仅 4.74/255，且只跳变 1 帧，没有淡入动画）。</para>
    ///
    /// <para>拖动时先在覆盖层里把新位置实心画出来，视线就一直有着落，
    /// 壁纸随后跟上时也不再显得突兀。</para>
    /// </summary>
    private void UpdateAdornerVisibility()
    {
        var interacting = _dragging || _resizing;
        var show = (_editMode || interacting) && !Fence.Collapsed;

        _outline.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _outline.Background = interacting
            ? new SolidColorBrush(Color.FromArgb(70, 20, 22, 34))
            : null;

        _grip.Visibility = _editMode && !Fence.Collapsed ? Visibility.Visible : Visibility.Collapsed;
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
            UpdateAdornerVisibility();
            CollapseToggled?.Invoke(this);
            e.Handled = true;
            return;
        }

        _dragStart = e.GetPosition(Parent as UIElement);
        _dragging = true;
        UpdateAdornerVisibility();
        InteractingChanged?.Invoke(this, true); // 先让整窗停止透传，否则收不到后续 MouseMove
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
        UpdateAdornerVisibility();
        _titleBar.ReleaseMouseCapture();
        InteractingChanged?.Invoke(this, false);
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
        UpdateAdornerVisibility();
        InteractingChanged?.Invoke(this, true);
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
        UpdateAdornerVisibility();
        _grip.ReleaseMouseCapture();
        InteractingChanged?.Invoke(this, false);
        EditCommitted?.Invoke(this);
        e.Handled = true;
    }

    /// <summary>
    /// 强制中止进行中的拖拽/缩放并释放捕获。
    ///
    /// 退出编辑模式、分区被删除、覆盖层重建时调用 ——
    /// 拖拽状态或鼠标捕获若残留，整窗会一直停止透传，桌面图标就再也点不动了。
    /// </summary>
    public void AbortInteraction()
    {
        if (!_dragging && !_resizing) return;

        _dragging = false;
        _resizing = false;
        UpdateAdornerVisibility();
        _titleBar.ReleaseMouseCapture();
        _grip.ReleaseMouseCapture();
        InteractingChanged?.Invoke(this, false);
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
