using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using zDesktop.Core.Layout;
using zDesktop.Core.Widgets;

namespace zDesktop.Shell.Widgets;

/// <summary>
/// 组件宿主 — 管理桌面上的所有组件实例
///
/// 职责：
/// 1. 在 Canvas 上布局组件容器
/// 2. 聚合命中测试（所有可见组件的边界）
/// 3. 组件的添加、移除
/// 4. 布局变更通知（拖拽/缩放/关闭时触发，供 App 层持久化）
/// 5. 拖拽对齐辅助线 — 屏幕中线 + 组件边缘吸附
/// </summary>
public class WidgetHost : Canvas
{
    private readonly List<WidgetContainer> _containers = new();

    // 对齐辅助线
    private const double SnapThreshold = 8; // 吸附阈值（逻辑像素）
    private Line? _guideV; // 垂直辅助线
    private Line? _guideH; // 水平辅助线

    /// <summary>布局发生变更（组件移动、缩放、添加、移除）时触发</summary>
    public event Action? LayoutChanged;

    /// <summary>组件设置按钮被点击（齿轮）— 传递对应的容器，供 App 弹出设置面板</summary>
    public event Action<WidgetContainer>? SettingsRequested;

    public WidgetHost()
    {
        // 创建辅助线（初始隐藏）
        _guideV = new Line
        {
            Stroke = new SolidColorBrush(Color.FromArgb(180, 108, 92, 231)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _guideH = new Line
        {
            Stroke = new SolidColorBrush(Color.FromArgb(180, 108, 92, 231)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        Children.Add(_guideV);
        Children.Add(_guideH);
    }

    /// <summary>
    /// 添加组件到桌面
    /// </summary>
    public WidgetContainer AddWidget(WidgetBase widget, WidgetSettings? settings = null)
    {
        var desc = widget.Descriptor;
        settings ??= new WidgetSettings
        {
            WidgetId = desc.Id,
            X = 100,
            Y = 100,
            Width = desc.DefaultWidth,
            Height = desc.DefaultHeight,
            IsVisible = true,
        };

        // 在创建容器（会触发 OnInitialize）之前应用配置，
        // 使组件在初始化时就能读到正确的配置值
        if (settings.Config != null && settings.Config.Count > 0)
        {
            widget.ApplyConfig(settings.Config);
        }
        else
        {
            widget.ApplyConfig(null); // 仅应用 schema 默认值
        }

        var container = new WidgetContainer(widget);
        container.Width = settings.Width;
        container.Height = settings.Height;
        container.Visibility = settings.IsVisible ? Visibility.Visible : Visibility.Collapsed;

        Canvas.SetLeft(container, settings.X);
        Canvas.SetTop(container, settings.Y);

        container.CloseRequested += OnWidgetClose;
        container.PositionChanged += OnContainerMoved;
        container.WidgetSizeChanged += OnContainerResized;
        container.SettingsRequested += OnContainerSettingsRequested;

        _containers.Add(container);
        // 插入到辅助线之前，保持辅助线在最上层
        Children.Add(container);

        Console.WriteLine($"[WidgetHost] 已添加组件: {desc.Name} @ ({settings.X},{settings.Y}) {settings.Width}x{settings.Height}");
        return container;
    }

    /// <summary>移除组件</summary>
    public void RemoveWidget(WidgetContainer container)
    {
        container.CloseRequested -= OnWidgetClose;
        container.PositionChanged -= OnContainerMoved;
        container.WidgetSizeChanged -= OnContainerResized;
        container.SettingsRequested -= OnContainerSettingsRequested;
        _containers.Remove(container);
        Children.Remove(container);
        container.Widget.OnUnload();

        Console.WriteLine($"[WidgetHost] 已移除组件: {container.Widget.Descriptor.Name}");
        LayoutChanged?.Invoke();
    }

    /// <summary>获取当前所有组件容器</summary>
    public IReadOnlyList<WidgetContainer> Containers => _containers;

    /// <summary>
    /// 显示器配置变更后重新定位组件 — 将超出工作区的组件拉回边界内
    /// </summary>
    public void RepositionWidgets()
    {
        var canvasW = ActualWidth;
        var canvasH = ActualHeight;
        var changed = false;

        foreach (var container in _containers)
        {
            if (container.Visibility != Visibility.Visible) continue;

            var x = Canvas.GetLeft(container);
            var y = Canvas.GetTop(container);
            var w = container.ActualWidth > 0 ? container.ActualWidth : container.Width;
            var h = container.ActualHeight > 0 ? container.ActualHeight : container.Height;

            var newX = x;
            var newY = y;

            // 右边界超出 → 拉回
            if (x + w > canvasW)
                newX = Math.Max(0, canvasW - w);

            // 下边界超出 → 拉回
            if (y + h > canvasH)
                newY = Math.Max(0, canvasH - h);

            // 左/上边界超出
            if (x < 0) newX = 0;
            if (y < 0) newY = 0;

            if (Math.Abs(newX - x) > 0.5 || Math.Abs(newY - y) > 0.5)
            {
                Canvas.SetLeft(container, newX);
                Canvas.SetTop(container, newY);
                changed = true;
                Console.WriteLine($"[WidgetHost] 组件 {container.Widget.Descriptor.Name} 已重新定位: ({x:F0},{y:F0}) → ({newX:F0},{newY:F0})");
            }
        }

        if (changed)
            LayoutChanged?.Invoke();
    }

    /// <summary>判断指定类型的组件是否已在桌面上</summary>
    public bool Contains(string widgetId)
    {
        return _containers.Any(c => c.Widget.Descriptor.Id == widgetId);
    }

    /// <summary>按组件类型 Id 移除组件</summary>
    public bool RemoveById(string widgetId)
    {
        var container = _containers.FirstOrDefault(c => c.Widget.Descriptor.Id == widgetId);
        if (container == null) return false;
        RemoveWidget(container);
        return true;
    }

    private void OnWidgetClose(WidgetContainer container)
    {
        RemoveWidget(container);
    }

    private void OnContainerMoved(WidgetContainer container, double x, double y)
    {
        LayoutChanged?.Invoke();
    }

    private void OnContainerResized(WidgetContainer container, double w, double h)
    {
        LayoutChanged?.Invoke();
    }

    private void OnContainerSettingsRequested(WidgetContainer container)
    {
        SettingsRequested?.Invoke(container);
    }

    // ===== 对齐辅助线 =====

    /// <summary>
    /// 拖拽时计算对齐吸附 — 供 WidgetContainer 调用
    /// 返回调整后的坐标和是否显示辅助线
    /// </summary>
    /// <param name="dragging">正在拖拽的容器</param>
    /// <param name="proposedX">提议的 X 坐标</param>
    /// <param name="proposedY">提议的 Y 坐标</param>
    /// <returns>(吸附后 X, 吸附后 Y, 是否显示垂直线, 垂直线 x, 是否显示水平线, 水平线 y)</returns>
    public (double x, double y, bool showV, double lineX, bool showH, double lineY) CalculateSnap(
        WidgetContainer dragging, double proposedX, double proposedY)
    {
        var w = dragging.ActualWidth;
        var h = dragging.ActualHeight;
        var snapX = proposedX;
        var snapY = proposedY;
        var showV = false;
        var showH = false;
        var lineX = 0.0;
        var lineY = 0.0;

        // 候选对齐点：自身的左边缘、中心、右边缘
        var candidatesX = new[]
        {
            (val: proposedX, line: proposedX),          // 左边缘
            (val: proposedX + w / 2, line: proposedX + w / 2), // 中心 X
            (val: proposedX + w, line: proposedX + w),  // 右边缘
        };
        var candidatesY = new[]
        {
            (val: proposedY, line: proposedY),          // 上边缘
            (val: proposedY + h / 2, line: proposedY + h / 2), // 中心 Y
            (val: proposedY + h, line: proposedY + h),  // 下边缘
        };

        // 目标对齐点：屏幕中线 + 其他组件的左/中/右、上/中/下
        var targetsX = new List<double> { ActualWidth / 2 };
        var targetsY = new List<double> { ActualHeight / 2 };

        foreach (var c in _containers)
        {
            if (c == dragging || c.Visibility != Visibility.Visible) continue;

            var cx = Canvas.GetLeft(c);
            var cy = Canvas.GetTop(c);
            var cw = c.ActualWidth;
            var ch = c.ActualHeight;

            targetsX.Add(cx);            // 左边缘
            targetsX.Add(cx + cw / 2);   // 中心
            targetsX.Add(cx + cw);       // 右边缘

            targetsY.Add(cy);            // 上边缘
            targetsY.Add(cy + ch / 2);   // 中心
            targetsY.Add(cy + ch);       // 下边缘
        }

        // 查找最近的垂直对齐
        var bestDX = SnapThreshold + 1.0;
        foreach (var (val, line) in candidatesX)
        {
            foreach (var target in targetsX)
            {
                var d = Math.Abs(val - target);
                if (d < bestDX)
                {
                    bestDX = d;
                    snapX = proposedX - (val - target);
                    lineX = target;
                    showV = true;
                }
            }
        }

        // 查找最近的水平对齐
        var bestDY = SnapThreshold + 1.0;
        foreach (var (val, line) in candidatesY)
        {
            foreach (var target in targetsY)
            {
                var d = Math.Abs(val - target);
                if (d < bestDY)
                {
                    bestDY = d;
                    snapY = proposedY - (val - target);
                    lineY = target;
                    showH = true;
                }
            }
        }

        return (snapX, snapY, showV, lineX, showH, lineY);
    }

    /// <summary>显示/隐藏对齐辅助线</summary>
    public void UpdateGuideLines(bool showV, double lineX, bool showH, double lineY)
    {
        if (_guideV == null || _guideH == null) return;

        if (showV)
        {
            _guideV.X1 = lineX;
            _guideV.X2 = lineX;
            _guideV.Y1 = 0;
            _guideV.Y2 = ActualHeight;
            _guideV.Visibility = Visibility.Visible;
        }
        else
        {
            _guideV.Visibility = Visibility.Collapsed;
        }

        if (showH)
        {
            _guideH.X1 = 0;
            _guideH.X2 = ActualWidth;
            _guideH.Y1 = lineY;
            _guideH.Y2 = lineY;
            _guideH.Visibility = Visibility.Visible;
        }
        else
        {
            _guideH.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>隐藏所有辅助线</summary>
    public void HideGuideLines()
    {
        if (_guideV != null) _guideV.Visibility = Visibility.Collapsed;
        if (_guideH != null) _guideH.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 导出当前布局配置 — 遍历所有容器记录位置/大小
    /// </summary>
    public LayoutConfig GetCurrentLayout()
    {
        var config = new LayoutConfig();

        foreach (var container in _containers)
        {
            config.Widgets.Add(new WidgetLayoutEntry
            {
                WidgetId = container.Widget.Descriptor.Id,
                X = Canvas.GetLeft(container),
                Y = Canvas.GetTop(container),
                Width = container.Width,
                Height = container.Height,
                IsVisible = container.PersistedVisible,
                Config = new Dictionary<string, object?>(container.Widget.Config),
            });
        }

        return config;
    }

    /// <summary>
    /// 聚合命中测试 — 判断窗口坐标点是否落在任意可见组件上
    /// 被 DesktopOverlayWindow.HitTestCallback 调用
    /// </summary>
    public bool HitTest(Point point)
    {
        foreach (var container in _containers)
        {
            if (container.Visibility == Visibility.Visible && container.HitTest(point))
                return true;
        }
        return false;
    }
}
