namespace zDesktop.Core.Widgets;

/// <summary>
/// 组件配置字段定义 — 描述单个配置项的元数据
/// 供设置面板动态渲染表单，供组件声明可配置项
/// </summary>
public sealed class WidgetConfigField
{
    /// <summary>字段键名（组件内唯一，如 "use24Hour"）</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>显示名称（如"24小时制"）</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>字段类型</summary>
    public WidgetConfigFieldType FieldType { get; init; } = WidgetConfigFieldType.Toggle;

    /// <summary>默认值（JSON 原始值：bool/int/double/string）</summary>
    public object? DefaultValue { get; init; }

    /// <summary>选项列表（仅 Choice 类型用，key=存储值, value=显示文本）</summary>
    public List<ConfigChoice>? Choices { get; init; }

    /// <summary>数值最小值（仅 Number 类型用）</summary>
    public double? Min { get; init; }

    /// <summary>数值最大值（仅 Number 类型用）</summary>
    public double? Max { get; init; }

    /// <summary>数值步长（仅 Number 类型用）</summary>
    public double? Step { get; init; }

    /// <summary>说明文字（显示在字段下方）</summary>
    public string? Description { get; init; }
}

/// <summary>配置字段类型</summary>
public enum WidgetConfigFieldType
{
    /// <summary>开关（布尔值）</summary>
    Toggle,

    /// <summary>下拉选择（从 Choices 中选）</summary>
    Choice,

    /// <summary>数值输入（带 Min/Max/Step）</summary>
    Number,

    /// <summary>文本输入</summary>
    Text,
}

/// <summary>选项项 — Choice 类型的可选值</summary>
public sealed class ConfigChoice
{
    public string Value { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
}
