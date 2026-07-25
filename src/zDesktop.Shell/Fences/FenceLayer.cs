using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using zDesktop.Core.Fences;
using zDesktop.Shell.Styles;

namespace zDesktop.Shell.Fences;

/// <summary>
/// 分区交互层 —— 挂在覆盖层上，承载本屏所有分区的标题栏与编辑手势。
///
/// <para><b>两种模式</b>（设计案 v3.1 §二 零破坏契约）：</para>
/// <list type="bullet">
/// <item><b>默认态</b>：只有分区标题栏捕获鼠标，其余全部透传。
/// 桌面图标照常可点、可框选、可右键 —— 与没装 zDesktop 时完全一致。</item>
/// <item><b>编辑模式</b>：用户从托盘显式进入后，整层接管鼠标，
/// 可在空白处拖拽新建分区、缩放已有分区。退出即恢复透传。</item>
/// </list>
///
/// <para><b>为什么不做「右键拖拽直接新建」</b>：Fences 的经典手势要求捕获桌面空白处的
/// 右键按下事件，而我们的覆盖层正是靠对空白区域返回 <c>HTTRANSPARENT</c> 才保住了
/// 「图标可点、右键菜单可用」。捕获空白区域会直接违反零破坏契约。
/// 改用显式的编辑模式：默认行为一个字节都不变，需要编辑时用户主动进入。</para>
/// </summary>
public sealed class FenceLayer : Canvas
{
    private readonly List<FenceVisual> _visuals = new();
    private readonly Rectangle _creationPreview;

    private Point _createStart;
    private bool _creating;
    private bool _editMode;

    /// <summary>
    /// 是否有拖拽/缩放/框选正在进行。
    ///
    /// 为 true 时命中测试无条件返回 true —— 覆盖层平时靠 <c>HTTRANSPARENT</c> 透传，
    /// 而那会让 Win32 把光标移出控件后的鼠标消息直接投给下层窗口，
    /// WPF 的 CaptureMouse 拦不住，表现为「拖动不跟手」。
    /// </summary>
    private bool _interacting;

    /// <summary>本层所属显示器的稳定标识</summary>
    public string MonitorKey { get; set; } = string.Empty;

    /// <summary>新建分区请求（参数为相对工作区的 DIP 矩形）</summary>
    public event Action<FenceLayer, FenceRect>? FenceCreateRequested;

    /// <summary>任一分区被修改（移动/缩放/折叠/重命名/删除），需落盘并重新合成</summary>
    public event Action? FencesChanged;

    /// <summary>分区删除请求</summary>
    public event Action<Fence>? FenceDeleteRequested;

    /// <summary>分区重命名请求</summary>
    public event Action<Fence>? FenceRenameRequested;

    public IReadOnlyList<FenceVisual> Visuals => _visuals;

    /// <summary>是否处于编辑模式</summary>
    public bool EditMode
    {
        get => _editMode;
        set
        {
            if (_editMode == value) return;
            _editMode = value;

            // 编辑模式下整层接收鼠标；默认态背景为 null，事件直接落到下层
            Background = value ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) : null;

            foreach (var v in _visuals) v.SetEditMode(value);

            // 退出时必须彻底复位：残留的拖拽状态或鼠标捕获会让整窗一直停止透传，
            // 桌面图标就再也点不动了 —— 这是零破坏契约的直接违反。
            if (!value) ResetInteraction();
        }
    }

    public FenceLayer()
    {
        Background = null;

        _creationPreview = new Rectangle
        {
            Stroke = Theme.PrimaryBrush,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Fill = new SolidColorBrush(Color.FromArgb(40, 108, 92, 231)),
            RadiusX = 8,
            RadiusY = 8,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Children.Add(_creationPreview);
    }

    // ===== 分区增删 =====

    /// <summary>按模型重建全部分区外观</summary>
    public void Rebuild(IEnumerable<Fence> fences)
    {
        // 先中止旧视觉上的交互，否则被销毁的控件仍持有鼠标捕获
        ResetInteraction();

        foreach (var v in _visuals) Children.Remove(v);
        _visuals.Clear();

        foreach (var fence in fences)
        {
            if (!string.Equals(fence.MonitorKey, MonitorKey, StringComparison.OrdinalIgnoreCase))
                continue; // 不属于本屏

            var visual = new FenceVisual(fence);
            visual.InteractingChanged += (_, active) => _interacting = active;
            visual.EditCommitted += _ => FencesChanged?.Invoke();
            visual.CollapseToggled += _ => FencesChanged?.Invoke();
            visual.RenameRequested += v => FenceRenameRequested?.Invoke(v.Fence);
            visual.DeleteRequested += v => FenceDeleteRequested?.Invoke(v.Fence);
            visual.SetEditMode(_editMode);

            _visuals.Add(visual);
            Children.Add(visual);
        }
    }

    /// <summary>刷新全部分区的几何（分区数据被外部改动后调用）</summary>
    public void RefreshGeometry()
    {
        foreach (var v in _visuals) v.ApplyGeometry();
    }

    // ===== 命中测试 =====

    /// <summary>
    /// 聚合命中测试 —— 由覆盖层的 <c>HitTestCallback</c> 调用。
    ///
    /// 编辑模式下整层命中；默认态只有分区标题栏命中，其余透传给原生桌面。
    /// </summary>
    public bool HitTest(Point point)
    {
        // 拖拽进行中必须无条件命中，否则光标一移出标题栏就收不到鼠标消息了
        if (_interacting || _editMode) return true;

        foreach (var v in _visuals)
        {
            if (v.HitTest(point)) return true;
        }
        return false;
    }

    /// <summary>
    /// 彻底复位交互状态：中止所有分区的拖拽、结束框选、释放鼠标捕获。
    /// 退出编辑模式与重建分区时都必须调用。
    /// </summary>
    public void ResetInteraction()
    {
        foreach (var v in _visuals) v.AbortInteraction();

        if (_creating)
        {
            _creating = false;
            ReleaseMouseCapture();
        }

        _interacting = false;
        HideCreationPreview();
    }

    // ===== 编辑模式：拖拽新建 =====

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!_editMode) return;

        // 点在已有分区上时不新建（那是移动/缩放操作）
        var p = e.GetPosition(this);
        if (_visuals.Any(v => v.HitTest(p))) return;

        _createStart = p;
        _creating = true;
        _interacting = true;
        CaptureMouse();

        _creationPreview.Visibility = Visibility.Visible;
        UpdateCreationPreview(p);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_creating) return;

        UpdateCreationPreview(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_creating) return;

        _creating = false;
        _interacting = false;
        ReleaseMouseCapture();

        var end = e.GetPosition(this);
        var rect = MakeRect(_createStart, end);
        HideCreationPreview();

        // 太小的拖拽视为误操作（比如只是想点一下），不产生分区
        if (rect.Width < 80 || rect.Height < 60) return;

        FenceCreateRequested?.Invoke(this, rect);
        e.Handled = true;
    }

    private void UpdateCreationPreview(Point current)
    {
        var r = MakeRect(_createStart, current);
        SetLeft(_creationPreview, r.X);
        SetTop(_creationPreview, r.Y);
        _creationPreview.Width = r.Width;
        _creationPreview.Height = r.Height;
    }

    private void HideCreationPreview() => _creationPreview.Visibility = Visibility.Collapsed;

    /// <summary>由两个角点求规范化矩形（支持向任意方向拖拽）</summary>
    private static FenceRect MakeRect(Point a, Point b) => new()
    {
        X = Math.Min(a.X, b.X),
        Y = Math.Min(a.Y, b.Y),
        Width = Math.Abs(a.X - b.X),
        Height = Math.Abs(a.Y - b.Y),
    };
}
