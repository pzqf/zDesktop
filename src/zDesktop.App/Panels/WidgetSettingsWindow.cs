using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using zDesktop.Core.Widgets;
using zDesktop.Shell.Widgets;
using WpfControls = System.Windows.Controls;

namespace zDesktop.App.Panels;

/// <summary>
/// 组件设置面板 — 动态渲染组件的配置表单
///
/// 根据 WidgetDescriptor.ConfigSchema 生成 Toggle/Choice/Number/Text 表单项，
/// 点击"应用"后回调通知外部持久化并应用配置
/// </summary>
public class WidgetSettingsWindow : Window
{
    private readonly WidgetBase _widget;
    private readonly Dictionary<string, object?> _editBuffer;
    private readonly StackPanel _formPanel;

    /// <summary>配置应用后触发（供 App 持久化）</summary>
    public event Action? ConfigApplied;

    public WidgetSettingsWindow(WidgetBase widget)
    {
        _widget = widget;

        // 编辑缓冲 — 深拷贝当前配置，取消时不影响原配置
        _editBuffer = new Dictionary<string, object?>(widget.Config);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        Width = 360;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Deactivated += (_, _) => Close();

        // ===== 外层容器 =====
        var outerBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 24, 24, 36)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 40,
                ShadowDepth = 8,
                Opacity = 0.5,
            },
            Padding = new Thickness(20),
        };

        var mainPanel = new StackPanel();

        // --- 标题栏 ---
        var headerPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };

        var title = new TextBlock
        {
            Text = $"{widget.Descriptor.Name} 设置",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerPanel.Children.Add(title);

        var closeBtn = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 28,
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
            FontSize = 12,
            Cursor = Cursors.Hand,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        closeBtn.Click += (_, _) => Close();
        DockPanel.SetDock(closeBtn, Dock.Right);
        headerPanel.Children.Add(closeBtn);

        mainPanel.Children.Add(headerPanel);

        // --- 表单滚动区 ---
        _formPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        var scroll = new ScrollViewer
        {
            Content = _formPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 320,
        };
        mainPanel.Children.Add(scroll);

        // --- 按钮栏 ---
        var btnPanel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };

        var cancelBtn = new Button
        {
            Content = "取消",
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
            Cursor = Cursors.Hand,
        };
        cancelBtn.Click += (_, _) => Close();
        DockPanel.SetDock(cancelBtn, Dock.Left);
        btnPanel.Children.Add(cancelBtn);

        var applyBtn = new Button
        {
            Content = "应用",
            Height = 32,
            Background = new SolidColorBrush(Color.FromArgb(220, 108, 92, 231)),
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
        };
        applyBtn.Click += OnApply;
        btnPanel.Children.Add(applyBtn);

        mainPanel.Children.Add(btnPanel);

        outerBorder.Child = mainPanel;
        Content = outerBorder;

        BuildForm();
    }

    /// <summary>根据 ConfigSchema 动态渲染表单项</summary>
    private void BuildForm()
    {
        var schema = _widget.Descriptor.ConfigSchema;

        if (schema.Count == 0)
        {
            _formPanel.Children.Add(new TextBlock
            {
                Text = "此组件没有可配置项",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0),
            });
            return;
        }

        foreach (var field in schema)
        {
            _formPanel.Children.Add(CreateFieldRow(field));
        }
    }

    /// <summary>创建单个字段行</summary>
    private UIElement CreateFieldRow(WidgetConfigField field)
    {
        var row = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 16),
        };

        // 标签行（字段名）
        var label = new TextBlock
        {
            Text = field.Label,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 6),
        };
        row.Children.Add(label);

        // 根据类型创建控件
        UIElement input = field.FieldType switch
        {
            WidgetConfigFieldType.Toggle => CreateToggle(field),
            WidgetConfigFieldType.Choice => CreateChoice(field),
            WidgetConfigFieldType.Number => CreateNumber(field),
            WidgetConfigFieldType.Text => CreateText(field),
            _ => new TextBlock { Text = "未知类型" },
        };
        row.Children.Add(input);

        // 说明文字
        if (!string.IsNullOrEmpty(field.Description))
        {
            row.Children.Add(new TextBlock
            {
                Text = field.Description,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return row;
    }

    /// <summary>开关控件</summary>
    private UIElement CreateToggle(WidgetConfigField field)
    {
        var current = GetBufferValue<bool>(field.Key, field.DefaultValue as bool? ?? false);
        var checkbox = new WpfControls.CheckBox
        {
            IsChecked = current,
            Content = current ? "开启" : "关闭",
            Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            FontSize = 12,
            Cursor = Cursors.Hand,
        };
        checkbox.Checked += (_, _) =>
        {
            _editBuffer[field.Key] = true;
            checkbox.Content = "开启";
        };
        checkbox.Unchecked += (_, _) =>
        {
            _editBuffer[field.Key] = false;
            checkbox.Content = "关闭";
        };
        return checkbox;
    }

    /// <summary>下拉选择控件</summary>
    private UIElement CreateChoice(WidgetConfigField field)
    {
        var combo = new WpfControls.ComboBox
        {
            MinWidth = 200,
            Cursor = Cursors.Hand,
        };

        var currentStr = GetBufferValue<string>(field.Key, field.DefaultValue?.ToString() ?? "");

        foreach (var choice in field.Choices ?? new List<ConfigChoice>())
        {
            combo.Items.Add(choice);
            if (choice.Value == currentStr)
                combo.SelectedItem = choice;
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ConfigChoice c)
                _editBuffer[field.Key] = c.Value;
        };
        return combo;
    }

    /// <summary>数值输入控件</summary>
    private UIElement CreateNumber(WidgetConfigField field)
    {
        var current = GetBufferValue<double>(field.Key,
            field.DefaultValue != null ? Convert.ToDouble(field.DefaultValue) : 0);

        var panel = new DockPanel();

        var valueLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 40,
        };
        DockPanel.SetDock(valueLabel, Dock.Right);
        panel.Children.Add(valueLabel);

        var slider = new Slider
        {
            Minimum = field.Min ?? 0,
            Maximum = field.Max ?? 100,
            Value = current,
            TickFrequency = field.Step ?? 1,
            IsSnapToTickEnabled = field.Step != null,
            Cursor = Cursors.Hand,
        };
        slider.ValueChanged += (_, e) =>
        {
            _editBuffer[field.Key] = e.NewValue;
            valueLabel.Text = e.NewValue.ToString("F0");
        };
        valueLabel.Text = current.ToString("F0");
        panel.Children.Add(slider);

        return panel;
    }

    /// <summary>文本输入控件</summary>
    private UIElement CreateText(WidgetConfigField field)
    {
        var current = GetBufferValue<string>(field.Key, field.DefaultValue?.ToString() ?? "");
        var tb = new WpfControls.TextBox
        {
            Text = current,
            FontSize = 12,
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            Padding = new Thickness(8, 4, 8, 4),
            Cursor = Cursors.IBeam,
        };
        tb.TextChanged += (_, _) => _editBuffer[field.Key] = tb.Text;
        return tb;
    }

    /// <summary>应用配置</summary>
    private void OnApply(object sender, RoutedEventArgs e)
    {
        _widget.ApplyConfig(_editBuffer);
        ConfigApplied?.Invoke();
        Close();
    }

    /// <summary>从编辑缓冲取值（带类型转换，处理 JSON 反序列化后的 JsonElement）</summary>
    private T GetBufferValue<T>(string key, T defaultValue)
    {
        if (_editBuffer.TryGetValue(key, out var value) && value != null)
        {
            try
            {
                // JSON 反序列化后值可能是 JsonElement
                if (value is System.Text.Json.JsonElement je)
                {
                    if (typeof(T) == typeof(bool) || typeof(T) == typeof(bool?))
                        return (T)(object)je.GetBoolean();
                    if (typeof(T) == typeof(int) || typeof(T) == typeof(int?))
                        return (T)(object)je.GetInt32();
                    if (typeof(T) == typeof(double) || typeof(T) == typeof(double?))
                        return (T)(object)je.GetDouble();
                    if (typeof(T) == typeof(string))
                        return (T)(object)(je.GetString() ?? "");
                }
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch { return defaultValue; }
        }
        return defaultValue;
    }
}
