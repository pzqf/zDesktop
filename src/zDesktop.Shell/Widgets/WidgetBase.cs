using System.Text.Json;
using System.Windows.Controls;
using zDesktop.Core.Widgets;

namespace zDesktop.Shell.Widgets;

/// <summary>
/// 组件基类 — 所有桌面组件继承此类
///
/// 子类需实现 Descriptor 属性并提供 Content
///
/// <para><b>生命周期顺序</b>（见 <c>WidgetHost.AddWidget</c>）：
/// <c>ApplyConfig → OnConfigChanged → OnInitialize →</c>（运行中）<c>→ OnUnload</c>。
/// 注意 <b>OnConfigChanged 在 OnInitialize 之前</b>，这样组件初始化时就能读到正确配置。</para>
///
/// <para>因此 <c>OnInitialize</c> 里<b>不要写</b>「加载中…」这类占位状态 ——
/// OnConfigChanged 那时已经把真实状态（甚至错误提示）摆好了，再写一遍就是把它盖掉。
/// 天气与壁纸两个组件都踩过：一个永远停在「加载中」等一个不会来的结果，
/// 一个图片都显示出来了标题还停在「加载中」。</para>
/// </summary>
public abstract class WidgetBase : UserControl
{
    /// <summary>组件描述符（元数据）</summary>
    public abstract WidgetDescriptor Descriptor { get; }

    /// <summary>当前配置（键值对，键对应 WidgetConfigField.Key）</summary>
    public Dictionary<string, object?> Config { get; private set; } = new();

    /// <summary>
    /// 组件被添加到宿主后调用 — 初始化定时器、加载数据等。
    /// <b>在 <see cref="OnConfigChanged"/> 之后执行</b>，别在这里覆盖它设好的状态文案。
    /// </summary>
    public virtual void OnInitialize() { }

    /// <summary>组件从宿主移除时调用 — 停止定时器、释放资源</summary>
    public virtual void OnUnload() { }

    /// <summary>配置变更后调用 — 子类重写以响应配置变化（如刷新频率、显示格式）</summary>
    public virtual void OnConfigChanged() { }

    /// <summary>
    /// 应用配置字典 — 合并 schema 默认值，设置 Config，触发 OnConfigChanged
    /// </summary>
    public void ApplyConfig(Dictionary<string, object?>? config)
    {
        // 以 schema 默认值为底，合并传入配置
        var merged = new Dictionary<string, object?>();
        foreach (var field in Descriptor.ConfigSchema)
        {
            merged[field.Key] = field.DefaultValue;
        }

        if (config != null)
        {
            foreach (var (key, value) in config)
            {
                merged[key] = value;
            }
        }

        Config = merged;
        OnConfigChanged();
    }

    /// <summary>获取配置值（带类型转换，找不到返回默认值）</summary>
    protected T GetConfig<T>(string key, T defaultValue = default!)
    {
        if (Config.TryGetValue(key, out var value) && value != null)
        {
            try
            {
                // JSON 反序列化后值可能是 JsonElement，需要先提取原生值
                if (value is JsonElement je)
                {
                    var raw = ExtractJsonElement(je, typeof(T));
                    if (raw == null) return defaultValue;
                    var converted = Convert.ChangeType(raw, typeof(T));
                    return converted == null ? defaultValue : (T)converted;
                }
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
        return defaultValue;
    }

    /// <summary>从 JsonElement 提取目标类型的原生值</summary>
    private static object? ExtractJsonElement(JsonElement je, Type targetType)
    {
        if (targetType == typeof(bool) || targetType == typeof(bool?))
            return je.GetBoolean();
        if (targetType == typeof(int) || targetType == typeof(int?))
            return je.GetInt32();
        if (targetType == typeof(double) || targetType == typeof(double?))
            return je.GetDouble();
        if (targetType == typeof(string))
            return je.GetString();
        // 兜底：返回原始值的字符串形式
        return je.GetRawText();
    }
}
