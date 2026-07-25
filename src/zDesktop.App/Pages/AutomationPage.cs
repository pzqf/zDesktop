using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using zDesktop.Shell.Automation;
using zDesktop.Shell.Styles;

// App 项目同时启用 WPF + WinForms + System.Drawing，Brush 在 System.Drawing 与 System.Windows.Media 间歧义，
// 此处显式别名到 WPF 画笔（与 csproj 已有的 Color/Brushes/SolidColorBrush 别名一致）
using Brush = System.Windows.Media.Brush;
// ComboBox / CheckBox / ListBox 在 System.Windows.Controls（WPF）与 System.Windows.Forms（WinForms）间歧义，
// 显式别名到 WPF 控件（本文件全部使用 WPF 控件，FolderBrowserDialog 才用 WinForms）
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
// MessageBox 在 System.Windows（WPF）与 System.Windows.Forms（WinForms）间歧义，别名到 WPF
using MessageBox = System.Windows.MessageBox;

namespace zDesktop.App.Pages;

/// <summary>
/// 自动化任务规则内容页 — 规则的可视化管理与执行日志查看
///
/// 视觉分区：
/// - 顶部操作栏：新建规则 / 从模板创建 / 全部启用·暂停
/// - 中部规则列表：每条规则一张卡片（名称 + 监控路径 + 条件摘要 + 动作摘要 + 启用开关 + 编辑/删除）
/// - 底部执行日志：最近执行记录（时间 + 规则名 + 文件 + 状态徽章）
///
/// 交互：编辑/创建通过 <see cref="AutomationRuleEditorWindow"/> 子窗口完成；
/// 模板创建通过 <see cref="TemplatePickerWindow"/> 选择后填充编辑表单。
/// 嵌入主窗口右侧内容区，不再独立弹窗。
/// 所有颜色 / 字体 / 圆角均引用 <see cref="Theme"/> 常量，不硬编码。
/// </summary>
public sealed class AutomationPage : ContentPage
{
    /// <summary>规则引擎服务（由 App 注入）</summary>
    private readonly AutomationService _service;

    /// <summary>规则卡片容器</summary>
    private readonly StackPanel _rulesListPanel = new();

    /// <summary>日志行容器</summary>
    private readonly StackPanel _logsPanel = new();

    /// <summary>全部启用/暂停按钮（引用用于更新文案）</summary>
    private Button _toggleAllBtn = null!;

    /// <summary>日志刷新定时器</summary>
    private DispatcherTimer? _logTimer;

    /// <summary>
    /// 构造自动化规则内容页
    /// </summary>
    /// <param name="service">自动化规则引擎服务（由 App 创建并注入）</param>
    public AutomationPage(AutomationService service)
    {
        _service = service;
        Title = "自动化任务规则";
        NavId = "automation-rules";

        var root = new DockPanel();
        root.LastChildFill = true;

        BuildActionBar(root);     // Dock.Top
        BuildLogsSection(root);   // Dock.Bottom
        BuildRulesSection(root);  // 填充剩余

        Content = root;

        _service.Changed += OnRulesChanged;
        _service.LogsChanged += OnLogsChanged;

        RenderRules();
        RenderLogs();
        StartLogTimer();

        // 页面卸载时取消订阅，避免泄漏
        Unloaded += OnUnloaded;
    }

    // ============================================================
    //  布局构建
    // ============================================================

    /// <summary>顶部操作栏</summary>
    private void BuildActionBar(DockPanel root)
    {
        var bar = new DockPanel
        {
            Margin = new Thickness(16, 14, 16, 8),
            LastChildFill = true,
        };

        // 全部启用/暂停（右侧）
        _toggleAllBtn = CreateSecondaryButton("全部启用");
        _toggleAllBtn.Click += (_, _) => OnToggleAll();
        DockPanel.SetDock(_toggleAllBtn, Dock.Right);
        bar.Children.Add(_toggleAllBtn);

        // 左侧操作组
        var left = new StackPanel { Orientation = Orientation.Horizontal };

        var newBtn = CreatePrimaryButton("新建规则");
        newBtn.Click += (_, _) => ShowEditor(null);
        left.Children.Add(newBtn);

        var tplBtn = CreateSecondaryButton("从模板创建");
        tplBtn.Margin = new Thickness(8, 0, 0, 0);
        tplBtn.Click += (_, _) => ShowTemplatePicker();
        left.Children.Add(tplBtn);

        bar.Children.Add(left);

        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
    }

    /// <summary>中部规则列表区</summary>
    private void BuildRulesSection(DockPanel root)
    {
        var section = new StackPanel();

        section.Children.Add(new TextBlock
        {
            Text = "规则列表",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(16, 4, 16, 8),
        });

        var scroll = new ScrollViewer
        {
            Content = _rulesListPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(16, 0, 16, 8),
        };
        section.Children.Add(scroll);

        root.Children.Add(section); // 最后添加 → 填充中部
    }

    /// <summary>底部执行日志区</summary>
    private void BuildLogsSection(DockPanel root)
    {
        var section = new StackPanel();
        DockPanel.SetDock(section, Dock.Bottom);

        // 日志标题 + 清空按钮
        var header = new DockPanel { Margin = new Thickness(16, 4, 16, 6) };
        var clearBtn = CreateSecondaryButton("清空");
        clearBtn.Height = 24;
        clearBtn.FontSize = 11;
        clearBtn.Padding = new Thickness(10, 0, 10, 0);
        clearBtn.Click += (_, _) => _service.ClearLogs();
        DockPanel.SetDock(clearBtn, Dock.Right);
        header.Children.Add(clearBtn);

        header.Children.Add(new TextBlock
        {
            Text = "执行日志",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        });
        section.Children.Add(header);

        var scroll = new ScrollViewer
        {
            Content = _logsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 180,
            Margin = new Thickness(16, 0, 16, 12),
        };
        section.Children.Add(scroll);

        root.Children.Add(section);
    }

    // ============================================================
    //  规则列表渲染
    // ============================================================

    /// <summary>重建规则卡片列表</summary>
    private void RenderRules()
    {
        _rulesListPanel.Children.Clear();
        var rules = _service.GetAll();

        if (rules.Count == 0)
        {
            _rulesListPanel.Children.Add(new TextBlock
            {
                Text = "暂无规则，点击「新建规则」或「从模板创建」开始",
                FontSize = 12,
                FontFamily = Theme.UiFont,
                Foreground = Theme.TextFaint,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 32, 0, 0),
            });
        }
        else
        {
            foreach (var r in rules)
            {
                _rulesListPanel.Children.Add(CreateRuleCard(r));
            }
        }

        UpdateToggleAllButton(rules);
    }

    /// <summary>创建单条规则卡片</summary>
    private Border CreateRuleCard(AutomationRule r)
    {
        var card = new Border
        {
            Background = Theme.ListItemBackground,
            BorderBrush = r.Enabled ? Theme.PrimarySubtle : Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.ControlRadius,
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 10),
        };

        var dp = new DockPanel { LastChildFill = true };

        // 右侧：启用开关 + 编辑 + 删除
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var enableCb = new CheckBox
        {
            IsChecked = r.Enabled,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "启用 / 暂停监控",
        };
        // 用 Dispatcher 延后切换，避免在事件处理中重建可视树引发重入
        enableCb.Checked += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => _service.ToggleEnabled(r.Id)));
        enableCb.Unchecked += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => _service.ToggleEnabled(r.Id)));
        controls.Children.Add(enableCb);

        var editBtn = CreateSecondaryButton("编辑");
        editBtn.Height = 26;
        editBtn.FontSize = 11;
        editBtn.Padding = new Thickness(10, 0, 10, 0);
        editBtn.Margin = new Thickness(10, 0, 0, 0);
        editBtn.Click += (_, _) => ShowEditor(r);
        controls.Children.Add(editBtn);

        var delBtn = CreateSecondaryButton("删除");
        delBtn.Height = 26;
        delBtn.FontSize = 11;
        delBtn.Padding = new Thickness(10, 0, 10, 0);
        delBtn.Margin = new Thickness(6, 0, 0, 0);
        delBtn.Click += (_, _) => OnDeleteRule(r);
        controls.Children.Add(delBtn);

        DockPanel.SetDock(controls, Dock.Right);
        dp.Children.Add(controls);

        // 左侧：规则信息
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(r.Name) ? "（未命名规则）" : r.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(r.WatchFolder) ? "未设置监控目录" : r.WatchFolder,
            FontSize = 11,
            FontFamily = Theme.MonoFont,
            Foreground = Theme.TextSecondary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0),
        });

        info.Children.Add(new TextBlock
        {
            Text = "条件：" + SummarizeConditions(r.Conditions),
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextFaint,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0),
        });

        info.Children.Add(new TextBlock
        {
            Text = "动作：" + SummarizeActions(r.Actions),
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextFaint,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        });

        var stats = r.LastRunAt.HasValue
            ? $"已运行 {r.RunCount} 次 · 上次 {r.LastRunAt.Value:yyyy-MM-dd HH:mm}"
            : "尚未运行";
        info.Children.Add(new TextBlock
        {
            Text = stats,
            FontSize = 10,
            FontFamily = Theme.MonoFont,
            Foreground = Theme.TextFaint,
            Margin = new Thickness(0, 4, 0, 0),
        });

        dp.Children.Add(info);

        card.Child = dp;
        return card;
    }

    /// <summary>更新「全部启用/暂停」按钮文案与可用性</summary>
    private void UpdateToggleAllButton(IReadOnlyList<AutomationRule> rules)
    {
        var allEnabled = rules.Count > 0 && rules.All(x => x.Enabled);
        _toggleAllBtn.Content = allEnabled ? "全部暂停" : "全部启用";
        _toggleAllBtn.IsEnabled = rules.Count > 0;
    }

    // ============================================================
    //  日志渲染
    // ============================================================

    /// <summary>重建日志行列表</summary>
    private void RenderLogs()
    {
        _logsPanel.Children.Clear();
        var logs = _service.GetLogs();

        if (logs.Count == 0)
        {
            _logsPanel.Children.Add(new TextBlock
            {
                Text = "暂无执行记录",
                FontSize = 11,
                FontFamily = Theme.UiFont,
                Foreground = Theme.TextFaint,
                Margin = new Thickness(0, 8, 0, 8),
            });
            return;
        }

        foreach (var log in logs)
        {
            _logsPanel.Children.Add(CreateLogRow(log));
        }
    }

    /// <summary>创建单条日志行</summary>
    private static DockPanel CreateLogRow(RuleExecutionLog log)
    {
        var row = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 6),
            LastChildFill = true,
        };

        // 状态徽章（右侧）
        var badge = new Border
        {
            CornerRadius = Theme.SmallRadius,
            Padding = new Thickness(8, 2, 8, 2),
            Background = StatusBrush(log.Status),
            Child = new TextBlock
            {
                Text = StatusText(log.Status),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                FontFamily = Theme.UiFont,
                Foreground = Brushes.White,
            },
        };
        DockPanel.SetDock(badge, Dock.Right);
        row.Children.Add(badge);

        // 时间 + 规则 + 文件（左侧填充）
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var fileName = Path.GetFileName(log.TargetFile);
        if (string.IsNullOrEmpty(fileName)) fileName = log.TargetFile;

        info.Children.Add(new TextBlock
        {
            Text = $"{log.Time:HH:mm:ss} · {log.RuleName}",
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextRegular,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        info.Children.Add(new TextBlock
        {
            Text = $"{fileName} — {log.Message}",
            FontSize = 10,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextFaint,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0),
        });

        row.Children.Add(info);
        return row;
    }

    /// <summary>状态徽章颜色</summary>
    private static Brush StatusBrush(ExecutionStatus status)
    {
        return status switch
        {
            ExecutionStatus.Success => Theme.SuccessBrush,
            ExecutionStatus.Failed => new SolidColorBrush(Theme.Error),
            _ => Theme.TextFaint,
        };
    }

    /// <summary>状态徽章文字</summary>
    private static string StatusText(ExecutionStatus status)
    {
        return status switch
        {
            ExecutionStatus.Success => "成功",
            ExecutionStatus.Failed => "失败",
            _ => "跳过",
        };
    }

    // ============================================================
    //  摘要辅助
    // ============================================================

    /// <summary>条件列表摘要文本</summary>
    private static string SummarizeConditions(IReadOnlyList<RuleCondition> conditions)
    {
        if (conditions == null || conditions.Count == 0) return "（无，匹配所有）";
        return string.Join(" 且 ", conditions.Select(c =>
            $"{GetLabel(c.Field)} {GetLabel(c.Operator)} {c.Value}"));
    }

    /// <summary>动作列表摘要文本</summary>
    private static string SummarizeActions(IReadOnlyList<RuleAction> actions)
    {
        if (actions == null || actions.Count == 0) return "（无动作）";
        return string.Join("，", actions.Select(a =>
        {
            var s = GetLabel(a.Type);
            if (a.Type == ActionType.Rename && !string.IsNullOrEmpty(a.NameTemplate))
                s += $"「{a.NameTemplate}」";
            else if (!string.IsNullOrEmpty(a.TargetPath))
                s += $" → {a.TargetPath}";
            return s;
        }));
    }

    /// <summary>枚举值的中文显示标签（供主面板与编辑器共用）</summary>
    internal static string GetLabel<T>(T value) where T : struct, Enum
    {
        return value switch
        {
            ConditionField f => f switch
            {
                ConditionField.FileName => "文件名",
                ConditionField.Extension => "扩展名",
                ConditionField.Size => "大小(字节)",
                ConditionField.ModifiedDate => "修改时间",
                ConditionField.CreationDate => "创建时间",
                _ => f.ToString(),
            },
            ConditionOperator o => o switch
            {
                ConditionOperator.Contains => "包含",
                ConditionOperator.Equals => "等于",
                ConditionOperator.StartsWith => "开头是",
                ConditionOperator.EndsWith => "结尾是",
                ConditionOperator.GreaterThan => "大于",
                ConditionOperator.LessThan => "小于",
                ConditionOperator.OlderThan => "早于(天)",
                _ => o.ToString(),
            },
            ActionType a => a switch
            {
                ActionType.Move => "移动",
                ActionType.Copy => "复制",
                ActionType.Delete => "删除",
                ActionType.Rename => "重命名",
                ActionType.Recycle => "回收站",
                ActionType.OpenApp => "打开程序",
                _ => a.ToString(),
            },
            _ => value.ToString(),
        };
    }

    // ============================================================
    //  事件处理
    // ============================================================

    /// <summary>规则集合变更 → 刷新列表（后台触发时调度到 UI 线程）</summary>
    private void OnRulesChanged()
    {
        Dispatcher.BeginInvoke(new Action(RenderRules));
    }

    /// <summary>日志变更 → 刷新日志区</summary>
    private void OnLogsChanged()
    {
        Dispatcher.BeginInvoke(new Action(RenderLogs));
    }

    /// <summary>切换全部规则启用状态</summary>
    private void OnToggleAll()
    {
        var rules = _service.GetAll();
        var allEnabled = rules.Count > 0 && rules.All(x => x.Enabled);
        _service.SetAllEnabled(!allEnabled);
    }

    /// <summary>删除规则（带确认）</summary>
    private void OnDeleteRule(AutomationRule r)
    {
        var result = MessageBox.Show(
            $"确定删除规则「{r.Name}」？此操作不可撤销。",
            "确认删除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.OK)
        {
            _service.Remove(r.Id);
        }
    }

    /// <summary>启动日志刷新定时器（2 秒）</summary>
    private void StartLogTimer()
    {
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _logTimer.Tick += (_, _) => RenderLogs();
        _logTimer.Start();
    }

    /// <summary>页面卸载时取消订阅与停止定时器</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _service.Changed -= OnRulesChanged;
        _service.LogsChanged -= OnLogsChanged;
        _logTimer?.Stop();
        _logTimer = null;
    }

    // ============================================================
    //  编辑器 / 模板选择器调用
    // ============================================================

    /// <summary>打开规则编辑器（existing 为 null 表示新建）</summary>
    private void ShowEditor(AutomationRule? existing)
    {
        var editor = new AutomationRuleEditorWindow(existing);
        editor.Saved += rule =>
        {
            if (existing == null) _service.Add(rule);
            else _service.Update(rule);
        };
        editor.ShowDialog();
    }

    /// <summary>从模板创建：先选模板，再打开编辑器预填充</summary>
    private void ShowTemplatePicker()
    {
        var templates = AutomationService.GetTemplates();

        var picker = new TemplatePickerWindow(templates);

        if (picker.ShowDialog() == true && picker.SelectedIndex >= 0)
        {
            var clone = CloneRule(templates[picker.SelectedIndex].Rule);
            ShowEditor(clone);
        }
    }

    /// <summary>深拷贝规则（新 Id、默认禁用），用于从模板预填充编辑表单</summary>
    private static AutomationRule CloneRule(AutomationRule source)
    {
        return new AutomationRule
        {
            Id = Guid.NewGuid(),
            Name = source.Name,
            Enabled = false,
            WatchFolder = source.WatchFolder,
            IsDestructive = source.IsDestructive,
            Conditions = source.Conditions
                .Select(c => new RuleCondition { Field = c.Field, Operator = c.Operator, Value = c.Value })
                .ToList(),
            Actions = source.Actions
                .Select(a => new RuleAction { Type = a.Type, TargetPath = a.TargetPath, NameTemplate = a.NameTemplate })
                .ToList(),
        };
    }
}

// ============================================================
//  规则编辑器子窗口
// ============================================================

/// <summary>
/// 自动化规则编辑器 — 内联表单：名称 / 监控目录 / 条件列表 / 动作列表 / 危险标记
///
/// 继承 <see cref="Panels.PanelWindowBase"/> 以保持玻璃拟态视觉一致性；
/// <see cref="Panels.PanelWindowBase.CloseOnDeactivate"/> 设为 false（表单不应失焦即关，且需承载文件夹选择对话框）。
/// 保存后通过 <see cref="Saved"/> 事件回传规则。
/// </summary>
internal sealed class AutomationRuleEditorWindow : zDesktop.App.Panels.PanelWindowBase
{
    /// <summary>正在编辑的规则（null 表示新建）</summary>
    private readonly AutomationRule? _existing;

    /// <summary>条件行集合</summary>
    private readonly List<ConditionRow> _conditions = new();

    /// <summary>动作行集合</summary>
    private readonly List<ActionRow> _actions = new();

    /// <summary>条件列表容器</summary>
    private readonly StackPanel _conditionsPanel = new();

    /// <summary>动作列表容器</summary>
    private readonly StackPanel _actionsPanel = new();

    private readonly TextBox _nameBox;
    private readonly TextBox _folderBox;
    private readonly CheckBox _enabledCb;
    private readonly CheckBox _destructiveCb;

    /// <summary>保存事件 — 回传编辑后的规则</summary>
    public event Action<AutomationRule>? Saved;

    /// <summary>
    /// 构造规则编辑器
    /// </summary>
    /// <param name="existing">已有规则（编辑）；null 表示新建</param>
    public AutomationRuleEditorWindow(AutomationRule? existing)
        : base(existing == null ? "新建规则" : "编辑规则", 580, 680, new DockPanel())
    {
        _existing = existing;
        CloseOnDeactivate = false;

        var root = (DockPanel)ContentArea;

        // 底部按钮栏
        var btnBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 8, 16, 12),
        };
        var cancel = CreateSecondaryButton("取消");
        cancel.Click += (_, _) => Close();
        var save = CreatePrimaryButton("保存");
        save.Margin = new Thickness(8, 0, 0, 0);
        save.Click += OnSave;
        btnBar.Children.Add(cancel);
        btnBar.Children.Add(save);
        DockPanel.SetDock(btnBar, Dock.Bottom);
        root.Children.Add(btnBar);

        // 可滚动表单
        var form = new StackPanel { Margin = new Thickness(16, 12, 16, 0) };
        var scroll = new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        // 规则名称
        _nameBox = CreateInput();
        form.Children.Add(CreateLabeledRow("规则名称", _nameBox));

        // 启用开关
        _enabledCb = new CheckBox
        {
            Content = "启用此规则（保存后立即开始监控）",
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextRegular,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
        };
        form.Children.Add(_enabledCb);

        form.Children.Add(new Border
        {
            Height = 1,
            Background = Theme.Divider,
            Margin = new Thickness(0, 12, 0, 12),
        });

        // 监控目录
        _folderBox = CreateInput();
        var folderRow = new DockPanel();
        var browse = CreateSecondaryButton("浏览…");
        browse.Width = 72;
        browse.Margin = new Thickness(8, 0, 0, 0);
        browse.Click += (_, _) => OnBrowseFolder();
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(_folderBox);
        form.Children.Add(CreateLabeledRow("监控文件夹", folderRow));

        // 条件区
        form.Children.Add(new TextBlock
        {
            Text = "条件（全部满足才触发；为空表示匹配所有）",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 4, 0, 6),
        });
        form.Children.Add(_conditionsPanel);
        var addCond = CreateSecondaryButton("+ 添加条件");
        addCond.Height = 26;
        addCond.FontSize = 11;
        addCond.Padding = new Thickness(10, 0, 10, 0);
        addCond.Click += (_, _) => AddCondition(null);
        form.Children.Add(addCond);

        form.Children.Add(new Border
        {
            Height = 1,
            Background = Theme.Divider,
            Margin = new Thickness(0, 12, 0, 12),
        });

        // 动作区
        form.Children.Add(new TextBlock
        {
            Text = "动作（按顺序执行）",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 6),
        });
        form.Children.Add(_actionsPanel);
        var addAction = CreateSecondaryButton("+ 添加动作");
        addAction.Height = 26;
        addAction.FontSize = 11;
        addAction.Padding = new Thickness(10, 0, 10, 0);
        addAction.Click += (_, _) => AddAction(null);
        form.Children.Add(addAction);

        // 危险标记
        _destructiveCb = new CheckBox
        {
            Content = "允许永久删除（Delete 动作需勾选此项才会执行）",
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Foreground = new SolidColorBrush(Theme.Warning),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
        };
        form.Children.Add(_destructiveCb);

        root.Children.Add(scroll); // 最后添加 → 填充剩余空间

        // 填充已有规则
        if (_existing != null)
        {
            _nameBox.Text = _existing.Name;
            _folderBox.Text = _existing.WatchFolder;
            _enabledCb.IsChecked = _existing.Enabled;
            _destructiveCb.IsChecked = _existing.IsDestructive;
            foreach (var c in _existing.Conditions) AddCondition(c);
            foreach (var a in _existing.Actions) AddAction(a);
        }
        else
        {
            _enabledCb.IsChecked = false;
            _destructiveCb.IsChecked = false;
        }
    }

    // ============================================================
    //  表单字段构建
    // ============================================================

    /// <summary>创建标准输入框</summary>
    private static TextBox CreateInput()
    {
        return new TextBox
        {
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Background = Theme.InputBackground,
            Foreground = Theme.TextRegular,
            BorderBrush = Theme.InputBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            Cursor = Cursors.IBeam,
        };
    }

    /// <summary>创建带标签的表单行</summary>
    private static StackPanel CreateLabeledRow(string label, UIElement input)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 6),
        });
        sp.Children.Add(input);
        return sp;
    }

    /// <summary>创建小型图标按钮（删除行用）</summary>
    private static Button CreateIconButton(string text)
    {
        var btn = CreateSecondaryButton(text);
        btn.Width = 28;
        btn.Height = 28;
        btn.FontSize = 12;
        btn.Padding = new Thickness(0);
        return btn;
    }

    /// <summary>创建枚举下拉框并预选</summary>
    private static ComboBox CreateEnumCombo<T>(T selected, double width) where T : struct, Enum
    {
        var combo = new ComboBox
        {
            Width = width,
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Background = Theme.InputBackground,
            Foreground = Theme.TextRegular,
            Cursor = Cursors.Hand,
        };

        foreach (T val in Enum.GetValues(typeof(T)))
        {
            var item = new ComboBoxItem { Content = AutomationPage.GetLabel(val), Tag = val };
            combo.Items.Add(item);
            if (val.Equals(selected)) combo.SelectedItem = item;
        }

        if (combo.SelectedItem == null && combo.Items.Count > 0)
            combo.SelectedIndex = 0;

        return combo;
    }

    /// <summary>读取下拉框当前枚举值</summary>
    private static T GetComboValue<T>(ComboBox combo) where T : struct, Enum
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Tag is T t) return t;
        return default;
    }

    // ============================================================
    //  条件行
    // ============================================================

    /// <summary>条件行控件集合</summary>
    private sealed class ConditionRow
    {
        public UIElement RowElement = null!;
        public ComboBox FieldCombo = null!;
        public ComboBox OpCombo = null!;
        public TextBox ValueBox = null!;

        public RuleCondition ToCondition() => new()
        {
            Field = GetComboValue<ConditionField>(FieldCombo),
            Operator = GetComboValue<ConditionOperator>(OpCombo),
            Value = ValueBox.Text,
        };
    }

    /// <summary>添加一条条件行（existing 非空时填充已有值）</summary>
    private ConditionRow AddCondition(RuleCondition? existing)
    {
        var row = new ConditionRow();

        var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        row.RowElement = dp;

        var remove = CreateIconButton("✕");
        remove.Click += (_, _) => RemoveCondition(row);
        DockPanel.SetDock(remove, Dock.Right);
        dp.Children.Add(remove);

        row.ValueBox = CreateInput();
        row.ValueBox.Width = 140;
        DockPanel.SetDock(row.ValueBox, Dock.Right);
        dp.Children.Add(row.ValueBox);

        row.OpCombo = CreateEnumCombo(existing?.Operator ?? ConditionOperator.Contains, 96);
        DockPanel.SetDock(row.OpCombo, Dock.Right);
        dp.Children.Add(row.OpCombo);

        row.FieldCombo = CreateEnumCombo(existing?.Field ?? ConditionField.FileName, 110);
        dp.Children.Add(row.FieldCombo);

        if (existing != null) row.ValueBox.Text = existing.Value;

        _conditions.Add(row);
        _conditionsPanel.Children.Add(dp);
        return row;
    }

    private void RemoveCondition(ConditionRow row)
    {
        _conditions.Remove(row);
        _conditionsPanel.Children.Remove(row.RowElement);
    }

    // ============================================================
    //  动作行
    // ============================================================

    /// <summary>动作行控件集合</summary>
    private sealed class ActionRow
    {
        public UIElement RowElement = null!;
        public ComboBox TypeCombo = null!;
        public TextBox TargetBox = null!;
        public TextBox TemplateBox = null!;

        public RuleAction ToAction() => new()
        {
            Type = GetComboValue<ActionType>(TypeCombo),
            TargetPath = TargetBox.Text.Trim(),
            NameTemplate = TemplateBox.Text,
        };
    }

    /// <summary>添加一条动作行（existing 非空时填充已有值）</summary>
    private ActionRow AddAction(RuleAction? existing)
    {
        var row = new ActionRow();

        var border = new Border
        {
            Background = Theme.ListItemBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.SmallRadius,
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8),
        };
        row.RowElement = border;

        var sp = new StackPanel();

        // 头部：类型 + 删除
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var remove = CreateIconButton("✕");
        remove.Click += (_, _) => RemoveAction(row);
        DockPanel.SetDock(remove, Dock.Right);
        header.Children.Add(remove);
        row.TypeCombo = CreateEnumCombo(existing?.Type ?? ActionType.Move, 120);
        header.Children.Add(row.TypeCombo);
        sp.Children.Add(header);

        // 目标路径
        row.TargetBox = CreateInput();
        var targetRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var browse = CreateSecondaryButton("浏览…");
        browse.Width = 64;
        browse.Height = 28;
        browse.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog();
            if (!string.IsNullOrEmpty(row.TargetBox.Text) && Directory.Exists(row.TargetBox.Text))
                dlg.SelectedPath = row.TargetBox.Text;
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                row.TargetBox.Text = dlg.SelectedPath;
        };
        DockPanel.SetDock(browse, Dock.Right);
        targetRow.Children.Add(browse);
        targetRow.Children.Add(row.TargetBox);
        sp.Children.Add(new TextBlock
        {
            Text = "目标路径（Move/Copy 为目录；OpenApp 为可执行程序路径）",
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 4),
        });
        sp.Children.Add(targetRow);

        // 名称模板
        row.TemplateBox = CreateInput();
        sp.Children.Add(new TextBlock
        {
            Text = "名称模板（Rename 使用；支持 {日期} {时间} {原名} {扩展名}）",
            FontSize = 11,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 6, 0, 4),
        });
        sp.Children.Add(row.TemplateBox);

        if (existing != null)
        {
            row.TargetBox.Text = existing.TargetPath;
            row.TemplateBox.Text = existing.NameTemplate;
        }

        border.Child = sp;
        _actions.Add(row);
        _actionsPanel.Children.Add(border);
        return row;
    }

    private void RemoveAction(ActionRow row)
    {
        _actions.Remove(row);
        _actionsPanel.Children.Remove(row.RowElement);
    }

    // ============================================================
    //  保存与文件夹选择
    // ============================================================

    /// <summary>选择监控文件夹</summary>
    private void OnBrowseFolder()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog();
        if (!string.IsNullOrEmpty(_folderBox.Text) && Directory.Exists(_folderBox.Text))
            dlg.SelectedPath = _folderBox.Text;
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            _folderBox.Text = dlg.SelectedPath;
    }

    /// <summary>保存规则 — 校验后回传并关闭</summary>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = _nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("请输入规则名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rule = new AutomationRule
        {
            Id = _existing?.Id ?? Guid.NewGuid(),
            Name = name,
            Enabled = _enabledCb.IsChecked ?? false,
            WatchFolder = _folderBox.Text.Trim(),
            IsDestructive = _destructiveCb.IsChecked ?? false,
            Conditions = _conditions.Select(r => r.ToCondition()).ToList(),
            Actions = _actions.Select(r => r.ToAction()).ToList(),
            LastRunAt = _existing?.LastRunAt,
            RunCount = _existing?.RunCount ?? 0,
        };

        Saved?.Invoke(rule);
        Close();
    }
}

// ============================================================
//  模板选择器子窗口
// ============================================================

/// <summary>
/// 模板选择器 — 列出内置推荐模板，供「从模板创建」使用
///
/// 继承 <see cref="Panels.PanelWindowBase"/> 保持视觉一致；
/// <see cref="Panels.PanelWindowBase.CloseOnDeactivate"/> 设为 false（模态对话框，由按钮关闭）。
/// </summary>
internal sealed class TemplatePickerWindow : zDesktop.App.Panels.PanelWindowBase
{
    /// <summary>模板列表</summary>
    private readonly IReadOnlyList<RuleTemplate> _templates;

    /// <summary>选中的模板索引（-1 表示未选）</summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>模板列表控件</summary>
    private readonly ListBox _list;

    public TemplatePickerWindow(IReadOnlyList<RuleTemplate> templates)
        : base("从模板创建", 420, 480, new StackPanel())
    {
        _templates = templates;
        CloseOnDeactivate = false;

        var root = (StackPanel)ContentArea;
        root.Margin = new Thickness(16);

        root.Children.Add(new TextBlock
        {
            Text = "选择一个模板，可在随后打开的编辑器中调整",
            FontSize = 12,
            FontFamily = Theme.UiFont,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 10),
        });

        _list = new ListBox
        {
            Background = Theme.InputBackground,
            Foreground = Theme.TextRegular,
            FontFamily = Theme.UiFont,
            BorderBrush = Theme.InputBorder,
            BorderThickness = new Thickness(1),
        };

        foreach (var t in templates)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = t.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                FontFamily = Theme.UiFont,
                Foreground = Theme.TextPrimary,
            });
            sp.Children.Add(new TextBlock
            {
                Text = t.Description,
                FontSize = 11,
                FontFamily = Theme.UiFont,
                Foreground = Theme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });

            _list.Items.Add(new ListBoxItem
            {
                Content = sp,
                Padding = new Thickness(10),
                Tag = t,
            });
        }

        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        root.Children.Add(_list);

        var btnBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var cancel = CreateSecondaryButton("取消");
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = CreatePrimaryButton("创建");
        ok.Margin = new Thickness(8, 0, 0, 0);
        ok.Click += (_, _) =>
        {
            SelectedIndex = _list.SelectedIndex;
            DialogResult = SelectedIndex >= 0;
            Close();
        };
        btnBar.Children.Add(cancel);
        btnBar.Children.Add(ok);
        root.Children.Add(btnBar);
    }
}
