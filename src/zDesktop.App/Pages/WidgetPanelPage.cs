using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using zDesktop.App.Panels;
using zDesktop.Core.Widgets;
using zDesktop.Shell.Styles;
using zDesktop.Shell.Widgets;

namespace zDesktop.App.Pages;

/// <summary>
/// 桌面组件内容页 — 添加 / 移除 / 配置桌面组件
///
/// 视觉：卡片网格，每个组件一张卡片（图标圆圈 + 名称 + 描述 + 操作按钮）
/// 交互：
/// - 点击"添加"将组件放入桌面默认位置
/// - 点击"移除"将组件从桌面移除
/// - 有配置项的组件显示"设置"按钮，点击弹出 WidgetSettingsWindow
///
/// 嵌入主窗口右侧内容区，不再独立弹窗。
/// 所有颜色 / 字体 / 圆角一律引用 <see cref="Theme"/> 常量。
/// </summary>
public sealed class WidgetPanelPage : ContentPage
{
    /// <summary>组件注册表（由 App 注入）</summary>
    private readonly WidgetRegistry _registry;

    /// <summary>组件宿主（由 App 注入）</summary>
    private readonly WidgetHost _host;

    /// <summary>组件工厂（由 App 注入，按 Id 创建组件实例）</summary>
    private readonly Func<string, WidgetBase> _createWidget;

    /// <summary>卡片容器（滚动区域内的 WrapPanel）</summary>
    private readonly Panel _cardsPanel;

    /// <summary>组件添加到桌面后触发（供 App 保存布局）</summary>
    public event Action? WidgetAdded;

    /// <summary>组件从桌面移除后触发（供 App 保存布局）</summary>
    public event Action? WidgetRemoved;

    /// <summary>组件配置变更后触发（供 App 保存布局）</summary>
    public event Action? WidgetConfigured;

    /// <summary>
    /// 构造桌面组件内容页
    /// </summary>
    /// <param name="registry">组件注册表</param>
    /// <param name="host">组件宿主</param>
    /// <param name="createWidget">组件工厂（按 Id 创建组件实例）</param>
    public WidgetPanelPage(WidgetRegistry registry, WidgetHost host, Func<string, WidgetBase> createWidget)
    {
        _registry = registry;
        _host = host;
        _createWidget = createWidget;

        Title = "桌面组件";
        NavId = "desktop-widgets";

        _cardsPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
        };

        var scroll = new ScrollViewer
        {
            Content = _cardsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(16),
        };

        Content = scroll;

        BuildCards();
    }

    /// <summary>构建所有组件卡片</summary>
    private void BuildCards()
    {
        foreach (var desc in _registry.GetAllDescriptors())
        {
            _cardsPanel.Children.Add(CreateCard(desc));
        }
    }

    /// <summary>创建单个组件卡片</summary>
    private UIElement CreateCard(WidgetDescriptor desc)
    {
        var card = new Border
        {
            Width = 200,
            Margin = new Thickness(0, 0, 12, 12),
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(14),
        };

        var panel = new StackPanel();

        // 图标圆圈（品牌色首字母）
        var icon = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = Theme.ControlRadius,
            Background = Theme.PrimaryBrush,
            Margin = new Thickness(0, 0, 0, 10),
            Child = new TextBlock
            {
                Text = desc.Name[..1],
                FontFamily = Theme.UiFont,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        panel.Children.Add(icon);

        // 名称
        panel.Children.Add(new TextBlock
        {
            Text = desc.Name,
            FontFamily = Theme.UiFont,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            Margin = new Thickness(0, 0, 0, 4),
        });

        // 描述
        panel.Children.Add(new TextBlock
        {
            Text = desc.Description,
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            MinHeight = 30,
        });

        // 操作按钮区
        var isAdded = _host.Contains(desc.Id);
        var hasSchema = desc.ConfigSchema.Count > 0;

        var btnRow = new DockPanel { LastChildFill = true };

        // 设置按钮（仅当有配置项时创建，仅当已添加时显示）
        Button? settingsBtn = null;
        if (hasSchema)
        {
            settingsBtn = CreateSecondaryButton("⚙ 设置");
            settingsBtn.Visibility = isAdded ? Visibility.Visible : Visibility.Collapsed;
            settingsBtn.Margin = new Thickness(0, 0, 8, 0);
            settingsBtn.Click += (_, _) => OnOpenSettings(desc);
            DockPanel.SetDock(settingsBtn, Dock.Left);
            btnRow.Children.Add(settingsBtn);
        }

        // 添加/移除按钮
        var btn = new Button
        {
            Content = isAdded ? "移除" : "添加",
            Height = 32,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(16, 0, 16, 0),
        };
        UpdateButtonStyle(btn, isAdded);
        btn.Click += (_, _) => OnToggleWidget(desc, btn, settingsBtn);
        btnRow.Children.Add(btn);

        panel.Children.Add(btnRow);

        card.Child = panel;
        return card;
    }

    /// <summary>切换组件添加/移除</summary>
    private void OnToggleWidget(WidgetDescriptor desc, Button btn, Button? settingsBtn)
    {
        if (_host.Contains(desc.Id))
        {
            // 移除
            _host.RemoveById(desc.Id);
            WidgetRemoved?.Invoke();
            UpdateButtonStyle(btn, false);
            btn.Content = "添加";
            if (settingsBtn != null)
                settingsBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            // 添加 — 放在默认位置
            var widget = _createWidget(desc.Id);
            _host.AddWidget(widget, new WidgetSettings
            {
                WidgetId = desc.Id,
                X = 100 + new Random().Next(0, 400),
                Y = 100 + new Random().Next(0, 300),
                Width = desc.DefaultWidth,
                Height = desc.DefaultHeight,
            });
            WidgetAdded?.Invoke();
            UpdateButtonStyle(btn, true);
            btn.Content = "移除";
            if (settingsBtn != null)
                settingsBtn.Visibility = Visibility.Visible;
        }
    }

    /// <summary>打开组件设置窗口</summary>
    private void OnOpenSettings(WidgetDescriptor desc)
    {
        // 从宿主查找已添加的组件实例
        var container = _host.Containers.FirstOrDefault(c => c.Widget.Descriptor.Id == desc.Id);
        if (container == null) return;

        var window = new WidgetSettingsWindow(container.Widget);
        window.ConfigApplied += () => WidgetConfigured?.Invoke();
        window.Show();
    }

    /// <summary>根据状态更新按钮样式（已添加=次级样式，未添加=主按钮样式）</summary>
    private static void UpdateButtonStyle(Button btn, bool isAdded)
    {
        if (isAdded)
        {
            btn.Background = Theme.InputBackground;
            btn.Foreground = Theme.TextRegular;
            btn.BorderBrush = Theme.InputBorder;
            btn.BorderThickness = new Thickness(1);
        }
        else
        {
            btn.Background = Theme.PrimaryBrush;
            btn.Foreground = Brushes.White;
            btn.BorderThickness = new Thickness(0);
        }
    }
}
