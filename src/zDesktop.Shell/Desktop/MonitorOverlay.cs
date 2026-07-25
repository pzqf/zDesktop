using System.Windows;
using System.Windows.Controls;
using zDesktop.Shell.Widgets;

namespace zDesktop.Shell.Desktop;

/// <summary>
/// 单个显示器上的覆盖层组合 —— 窗口 + 内容根 + 组件宿主
///
/// 设计案 v3.1 §八：每个显示器一个 overlay 实例。把「窗口 / 根容器 / 组件宿主」
/// 三者绑成一个单元，避免 App 层维护三组平行集合。
/// </summary>
public sealed class MonitorOverlay
{
    /// <summary>覆盖层窗口</summary>
    public DesktopOverlayWindow Window { get; }

    /// <summary>内容根容器 —— 供 App 追加图标层、搜索框等额外图层</summary>
    public Grid Root { get; }

    /// <summary>本屏的组件宿主</summary>
    public WidgetHost Host { get; }

    /// <summary>所属显示器</summary>
    public MonitorInfo Monitor => Window.Monitor;

    /// <summary>是否主显示器</summary>
    public bool IsPrimary => Window.IsPrimary;

    public MonitorOverlay(MonitorInfo monitor)
    {
        Window = new DesktopOverlayWindow(monitor);
        Host = new WidgetHost { MonitorKey = monitor.Key };
        Root = new Grid();

        // 组件宿主铺满整个覆盖层；图标层等其他图层由 App 按需插入到它下方
        Root.Children.Add(Host);
        Window.Content = Root;
    }

    /// <summary>
    /// 在组件宿主**下方**插入一个图层（如自渲染图标层），保证组件始终浮在最上。
    /// </summary>
    public void InsertLayerBelowWidgets(UIElement layer)
    {
        Root.Children.Insert(0, layer);
    }

    /// <summary>在组件宿主**上方**追加一个图层（如常驻搜索框）。</summary>
    public void AddLayerAboveWidgets(UIElement layer)
    {
        Root.Children.Add(layer);
    }
}
