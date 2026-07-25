using zDesktop.Core.Widgets;

namespace zDesktop.Shell.Widgets;

/// <summary>
/// 组件注册表 — 注册所有可用组件类型及其工厂函数
///
/// 启动时注册全部组件，布局恢复时根据 WidgetId 创建实例
/// </summary>
public sealed class WidgetRegistry
{
    private readonly Dictionary<string, WidgetRegistration> _registrations = new();

    /// <summary>注册一个组件类型</summary>
    /// <param name="factory">创建组件实例的工厂函数</param>
    public void Register(string widgetId, Func<WidgetBase> factory)
    {
        // 用工厂创建一个临时实例读取描述符（轻量，无副作用）
        var proto = factory();
        var descriptor = proto.Descriptor;
        proto.OnUnload();

        _registrations[widgetId] = new WidgetRegistration(widgetId, factory, descriptor);
        Console.WriteLine($"[WidgetRegistry] 已注册组件: {descriptor.Name} ({widgetId})");
    }

    /// <summary>根据 Id 创建组件实例</summary>
    public WidgetBase? Create(string widgetId)
    {
        if (_registrations.TryGetValue(widgetId, out var reg))
        {
            return reg.Factory();
        }
        Console.WriteLine($"[WidgetRegistry] 未知组件类型: {widgetId}");
        return null;
    }

    /// <summary>获取所有已注册组件的描述符</summary>
    public IReadOnlyList<WidgetDescriptor> GetAllDescriptors()
    {
        return _registrations.Values.Select(r => r.Descriptor).ToList();
    }

    /// <summary>判断组件类型是否已注册</summary>
    public bool IsRegistered(string widgetId) => _registrations.ContainsKey(widgetId);

    private sealed record WidgetRegistration(string Id, Func<WidgetBase> Factory, WidgetDescriptor Descriptor);
}
