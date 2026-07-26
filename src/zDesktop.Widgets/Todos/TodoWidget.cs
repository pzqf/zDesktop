using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using zDesktop.Core.Todos;
using zDesktop.Core.Widgets;
using zDesktop.Shell.Styles;
using zDesktop.Shell.Todos;
using zDesktop.Shell.Widgets;

namespace zDesktop.Widgets.Todos;

/// <summary>
/// 待办组件 — 增删改查 + 完成切换 + 本地持久化
///
/// 视觉：
/// - 输入框 + 添加按钮
/// - 待办列表（复选框 + 文本 + 删除按钮）
/// - 底部统计 + 清除已完成
/// </summary>
public class TodoWidget : WidgetBase
{
    private readonly TodoStore _store = new();
    private readonly TextBox _inputBox;
    private readonly StackPanel _listPanel;
    private readonly TextBlock _statsText;
    private readonly ScrollViewer _scrollViewer;

    public override WidgetDescriptor Descriptor { get; } = new()
    {
        Id = "todo",
        Name = "待办",
        Description = "本地待办事项管理",
        DefaultWidth = 280,
        DefaultHeight = 340,
        ResizeMode = WidgetResizeMode.HeightOnly,
    };

    public TodoWidget()
    {
        // ===== 输入区 =====
        // DockPanel 的填充名额只给最后一个孩子，所以固定宽度的「+」必须先入，
        // 输入框最后入才能吃掉剩下的宽度。顺序反了输入框会被压成一个小方块。
        var inputPanel = new DockPanel { Margin = new Thickness(10, 8, 10, 6) };

        var addBtn = new Button
        {
            Content = "+",
            Width = 32,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            FontFamily = Theme.UiFont,
            Background = Theme.PrimaryBrush,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        addBtn.Click += (_, _) => AddFromInput();
        DockPanel.SetDock(addBtn, Dock.Right);
        inputPanel.Children.Add(addBtn);

        _inputBox = new TextBox
        {
            FontSize = 13,
            FontFamily = Theme.UiFont,
            Background = Theme.InputBackground,
            Foreground = Theme.TextRegular,
            BorderBrush = Theme.InputBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5),
            CaretBrush = Brushes.White,
        };
        _inputBox.KeyDown += OnInputKeyDown;
        inputPanel.Children.Add(_inputBox);   // 最后入 = 占满剩余宽度

        // ===== 待办列表 =====
        _listPanel = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };

        _scrollViewer = new ScrollViewer
        {
            Content = _listPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(0),
        };

        // ===== 底部统计 =====
        // 关掉填充名额：两侧各自贴边，中间留空。开着的话「清除已完成」
        // 会去占满剩余宽度，看上去像是紧跟在统计文字后面。
        var footerPanel = new DockPanel { Margin = new Thickness(10, 0, 10, 8), LastChildFill = false };

        _statsText = new TextBlock
        {
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(_statsText, Dock.Left);
        footerPanel.Children.Add(_statsText);

        var clearBtn = new Button
        {
            Content = "清除已完成",
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Background = Brushes.Transparent,
            Foreground = Theme.TextSecondary,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Padding = new Thickness(4, 0, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        clearBtn.Click += (_, _) => _store.ClearCompleted();
        DockPanel.SetDock(clearBtn, Dock.Right);
        footerPanel.Children.Add(clearBtn);

        // ===== 主面板 =====
        // 用 Grid 而不是 StackPanel：StackPanel 里列表只占内容高度，
        // 页脚会跟在列表下面往上飘，组件下半截空着，列表也长到撑破组件才滚动。
        var grid = new Grid { Background = Brushes.Transparent };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 输入
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 列表吃满
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 页脚贴底

        Grid.SetRow(inputPanel, 0);
        Grid.SetRow(_scrollViewer, 1);
        Grid.SetRow(footerPanel, 2);
        grid.Children.Add(inputPanel);
        grid.Children.Add(_scrollViewer);
        grid.Children.Add(footerPanel);

        Content = grid;

        _store.Changed += RefreshList;
    }

    public override void OnInitialize()
    {
        RefreshList();
    }

    public override void OnUnload()
    {
        _store.Changed -= RefreshList;
    }

    // ===== 交互逻辑 =====

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddFromInput();
            e.Handled = true;
        }
    }

    private void AddFromInput()
    {
        var text = _inputBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        _store.Add(text);
        _inputBox.Clear();
    }

    /// <summary>刷新整个列表 UI</summary>
    private void RefreshList()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _listPanel.Children.Clear();

            var items = _store.GetAll();

            foreach (var item in items)
            {
                _listPanel.Children.Add(CreateTodoRow(item));
            }

            // 更新统计
            var total = items.Count;
            var done = items.Count(x => x.IsCompleted);
            _statsText.Text = total == 0 ? "暂无待办" : $"剩余 {total - done} / 共 {total}";
        }));
    }

    /// <summary>创建单个待办行</summary>
    private UIElement CreateTodoRow(TodoItem item)
    {
        var row = new Border
        {
            Margin = new Thickness(0, 0, 0, 4),
            CornerRadius = Theme.SmallRadius,
            Background = item.IsCompleted ? Theme.ListItemMuted : Theme.ListItemBackground,
            Padding = new Thickness(8, 5, 6, 5),
        };

        var panel = new DockPanel();

        // 复选框
        var checkbox = new CheckBox
        {
            IsChecked = item.IsCompleted,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 6, 0),
        };
        checkbox.Click += (_, _) => _store.Toggle(item.Id);
        DockPanel.SetDock(checkbox, Dock.Left);
        panel.Children.Add(checkbox);

        // 删除按钮
        var delBtn = new Button
        {
            Content = "✕",
            Width = 22,
            Height = 22,
            FontSize = 10,
            FontFamily = Theme.UiFont,
            Background = Brushes.Transparent,
            Foreground = Theme.TextFaint,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0),
        };
        delBtn.Click += (_, _) => _store.Remove(item.Id);
        DockPanel.SetDock(delBtn, Dock.Right);
        panel.Children.Add(delBtn);

        // 文本
        var text = new TextBlock
        {
            Text = item.Text,
            FontSize = 13,
            FontFamily = Theme.UiFont,
            Foreground = item.IsCompleted ? Theme.TextFaint : Theme.TextRegular,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            TextDecorations = item.IsCompleted ? TextDecorations.Strikethrough : null,
        };
        panel.Children.Add(text);

        row.Child = panel;
        return row;
    }
}
