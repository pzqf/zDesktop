using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using zDesktop.Core.DesktopIcons;

namespace zDesktop.Shell.DesktopIcons;

/// <summary>
/// 桌面图标层 — 管理所有桌面图标的 Canvas 容器
///
/// 职责：
/// 1. 承载所有 DesktopIconItem，按 Canvas 坐标定位
/// 2. 拖拽结束时网格吸附（对齐到固定网格）
/// 3. 首次加载无布局时默认网格排列
/// 4. 单选管理（点击图标取消其他选中，点击空白取消全部）
/// 5. 布局导出（供持久化）+ 命中测试（供 overlay 聚合）
///
/// 位于 overlay 最底层（组件层之下），背景透明以接收空白点击
/// </summary>
public class DesktopIconLayer : Canvas
{
    /// <summary>网格宽度（逻辑像素）</summary>
    private const double GridW = 88;
    /// <summary>网格高度（逻辑像素）</summary>
    private const double GridH = 96;
    /// <summary>网格起点 X（左侧留白）</summary>
    private const double OriginX = 12;
    /// <summary>网格起点 Y（顶部留白）</summary>
    private const double OriginY = 12;

    private readonly List<DesktopIconItem> _items = new();

    /// <summary>布局发生变更（图标移动）时触发，供 App 持久化</summary>
    public event Action? LayoutChanged;

    /// <summary>当前所有图标项</summary>
    public IReadOnlyList<DesktopIconItem> Items => _items;

    public DesktopIconLayer()
    {
        // 透明背景以接收空白区域点击（取消选中）
        Background = Brushes.Transparent;
    }

    /// <summary>添加图标到指定坐标</summary>
    public void AddIcon(DesktopIconItem item, double x, double y)
    {
        _items.Add(item);
        Children.Add(item);
        Canvas.SetLeft(item, x);
        Canvas.SetTop(item, y);
        item.PositionChanged += OnItemMoved;
    }

    /// <summary>仅选中指定图标，取消其他</summary>
    public void SelectOnly(DesktopIconItem item)
    {
        foreach (var i in _items)
            i.IsSelected = ReferenceEquals(i, item);
    }

    /// <summary>取消所有选中</summary>
    public void ClearSelection()
    {
        foreach (var i in _items)
            i.IsSelected = false;
    }

    /// <summary>首次加载无布局时，按列优先竖排排列所有图标（先填满一列再换下一列）</summary>
    public void ArrangeDefault()
    {
        // 列优先：先计算每列可容纳的行数，再按列填充
        var rows = Math.Max(1, (int)((ActualHeight - OriginY) / GridH));
        var i = 0;
        foreach (var item in _items)
        {
            var col = i / rows;
            var row = i % rows;
            Canvas.SetLeft(item, OriginX + col * GridW);
            Canvas.SetTop(item, OriginY + row * GridH);
            i++;
        }
        LayoutChanged?.Invoke();
    }

    /// <summary>图标拖拽结束 — 网格吸附 + 通知持久化</summary>
    private void OnItemMoved(DesktopIconItem item)
    {
        var x = Canvas.GetLeft(item);
        var y = Canvas.GetTop(item);
        var snapX = SnapToGrid(x, GridW, OriginX);
        var snapY = SnapToGrid(y, GridH, OriginY);
        Canvas.SetLeft(item, snapX);
        Canvas.SetTop(item, snapY);
        LayoutChanged?.Invoke();
    }

    /// <summary>将坐标吸附到最近的网格点</summary>
    private static double SnapToGrid(double value, double grid, double origin)
    {
        var n = Math.Round((value - origin) / grid);
        return origin + n * grid;
    }

    /// <summary>导出当前图标布局配置</summary>
    public IconLayoutConfig GetCurrentLayout()
    {
        var config = new IconLayoutConfig();
        foreach (var item in _items)
        {
            config.Icons.Add(new IconLayoutEntry
            {
                SourcePath = item.Info.SourcePath,
                X = Canvas.GetLeft(item),
                Y = Canvas.GetTop(item),
            });
        }
        return config;
    }

    /// <summary>命中测试 — 判断点是否落在任意可见图标上</summary>
    public bool HitTest(Point point)
    {
        foreach (var item in _items)
        {
            if (item.Visibility == Visibility.Visible && item.HitTest(point))
                return true;
        }
        return false;
    }

    /// <summary>空白区域点击 — 取消所有选中</summary>
    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        // 点击落在图标上时，图标自己处理（e.Handled），不会到这里
        ClearSelection();
        base.OnMouseLeftButtonDown(e);
    }
}
