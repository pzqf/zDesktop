using System.Drawing;
using System.Windows.Forms;

namespace zDesktop.App.Tray;

/// <summary>
/// 系统托盘管理器 — 后台运行图标 + 右键菜单
///
/// 菜单项：
/// - 显示/隐藏桌面组件
/// - 添加组件
/// - 桌面图标模式：zDesktop 渲染 / 系统原生（勾选切换）
/// - 开机自启（勾选切换）
/// - 退出 zDesktop
/// 双击托盘图标：切换组件可见性
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _iconModeItem;
    private readonly ToolStripMenuItem _startupItem;
    private bool _widgetsVisible = true;

    /// <summary>切换组件可见性</summary>
    public event Action? ToggleWidgets;

    /// <summary>打开组件面板</summary>
    public event Action? ShowWidgetPanel;

    /// <summary>切换桌面图标模式（zDesktop 渲染 ↔ 系统原生）</summary>
    public event Action? ToggleIconMode;

    /// <summary>切换开机自启</summary>
    public event Action? ToggleStartup;

    /// <summary>打开全局搜索</summary>
    public event Action? ShowGlobalSearch;

    /// <summary>打开窗口管理</summary>
    public event Action? ShowWindowManager;

    /// <summary>打开控制中心</summary>
    public event Action? ShowControlCenter;

    /// <summary>打开文件分类</summary>
    public event Action? ShowFileClassify;

    /// <summary>打开磁盘映射</summary>
    public event Action? ShowDiskMapper;

    /// <summary>打开自动化规则</summary>
    public event Action? ShowAutomation;

    /// <summary>打开图标管理</summary>
    public event Action? ShowIconManager;

    /// <summary>退出程序</summary>
    public event Action? ExitRequested;

    public TrayIconManager()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = CreateBrandIcon(),
            Text = "zDesktop",
            Visible = true,
        };

        // 右键菜单
        var menu = new ContextMenuStrip();

        _toggleItem = new ToolStripMenuItem("隐藏桌面组件", null, (_, _) => ToggleWidgets?.Invoke());
        menu.Items.Add(_toggleItem);

        var addWidgetItem = new ToolStripMenuItem("添加组件", null, (_, _) => ShowWidgetPanel?.Invoke());
        menu.Items.Add(addWidgetItem);

        // 功能中心子菜单 — 所有效率工具入口
        var toolsMenu = new ToolStripMenuItem("功能中心");
        toolsMenu.DropDownItems.Add("全局搜索", null, (_, _) => ShowGlobalSearch?.Invoke());
        toolsMenu.DropDownItems.Add("窗口管理", null, (_, _) => ShowWindowManager?.Invoke());
        toolsMenu.DropDownItems.Add("控制中心", null, (_, _) => ShowControlCenter?.Invoke());
        toolsMenu.DropDownItems.Add(new ToolStripSeparator());
        toolsMenu.DropDownItems.Add("文件分类整理", null, (_, _) => ShowFileClassify?.Invoke());
        toolsMenu.DropDownItems.Add("磁盘映射", null, (_, _) => ShowDiskMapper?.Invoke());
        toolsMenu.DropDownItems.Add("自动化规则", null, (_, _) => ShowAutomation?.Invoke());
        toolsMenu.DropDownItems.Add("图标管理", null, (_, _) => ShowIconManager?.Invoke());
        menu.Items.Add(toolsMenu);

        menu.Items.Add(new ToolStripSeparator());

        // 桌面图标模式 — 默认系统原生（不勾选）；zDesktop 自渲染为实验特性，需用户显式开启
        _iconModeItem = new ToolStripMenuItem("zDesktop 图标（实验）", null, (_, _) => ToggleIconMode?.Invoke())
        {
            CheckOnClick = false,
            Checked = false, // 零破坏契约：默认保留原生桌面图标层
        };
        menu.Items.Add(_iconModeItem);

        menu.Items.Add(new ToolStripSeparator());

        // 开机自启 — 读取当前注册表状态设置勾选
        _startupItem = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleStartup?.Invoke())
        {
            CheckOnClick = false,
            Checked = StartupHelper.IsEnabled(),
        };
        menu.Items.Add(_startupItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出 zDesktop", null, (_, _) => ExitRequested?.Invoke());
        menu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = menu;

        // 双击切换
        _notifyIcon.DoubleClick += (_, _) => ToggleWidgets?.Invoke();
    }

    /// <summary>更新菜单文字（显示/隐藏）</summary>
    public void UpdateToggleText(bool widgetsVisible)
    {
        _widgetsVisible = widgetsVisible;
        _toggleItem.Text = widgetsVisible ? "隐藏桌面组件" : "显示桌面组件";
    }

    /// <summary>更新桌面图标模式勾选状态</summary>
    public void UpdateIconModeCheck(bool zdesktopMode)
    {
        _iconModeItem.Checked = zdesktopMode;
    }

    /// <summary>更新开机自启勾选状态</summary>
    public void UpdateStartupCheck(bool enabled)
    {
        _startupItem.Checked = enabled;
    }

    /// <summary>显示气泡通知</summary>
    public void ShowBalloon(string title, string message, int timeoutMs = 2000)
    {
        _notifyIcon.ShowBalloonTip(timeoutMs, title, message, ToolTipIcon.Info);
    }

    /// <summary>
    /// 动态生成品牌图标 — 紫色圆角方块带 "Z" 字
    /// 无需外部 ico 文件
    /// </summary>
    private static Icon CreateBrandIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // 紫色圆角背景
        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(108, 92, 231));
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var rect = new Rectangle(2, 2, 28, 28);
        const int radius = 7;
        path.AddArc(rect.X, rect.Y, radius, radius, 180, 90);
        path.AddArc(rect.Right - radius, rect.Y, radius, radius, 270, 90);
        path.AddArc(rect.Right - radius, rect.Bottom - radius, radius, radius, 0, 90);
        path.AddArc(rect.X, rect.Bottom - radius, radius, radius, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);

        // 白色 "Z" 字
        using var font = new Font("Segoe UI", 15, FontStyle.Bold);
        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString("Z", font, System.Drawing.Brushes.White, new RectangleF(2, 2, 28, 28), sf);

        var handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
