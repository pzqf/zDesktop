using System.IO;
using System.Text.Json;
using zDesktop.Core.Layout;

namespace zDesktop.Shell.Layout;

/// <summary>
/// 布局持久化服务 — JSON 文件存储
///
/// 存储路径：<c>%APPDATA%\zDesktop\layout.json</c>（可通过构造参数覆盖，供测试使用）
/// 线程安全：所有读写加锁
/// </summary>
public sealed class LayoutStore
{
    private static readonly string DefaultDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _lock = new();
    private readonly string _dir;
    private readonly string _filePath;

    /// <param name="directory">存储目录；null 表示使用 <c>%APPDATA%\zDesktop</c></param>
    public LayoutStore(string? directory = null)
    {
        _dir = directory ?? DefaultDir;
        _filePath = Path.Combine(_dir, "layout.json");
    }

    /// <summary>加载布局配置，文件不存在/解析失败/迁移决定废弃时返回 null</summary>
    public LayoutConfig? Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_filePath)) return null;

                var json = File.ReadAllText(_filePath);
                var config = JsonSerializer.Deserialize<LayoutConfig>(json, JsonOptions);
                if (config == null) return null;

                var migrated = Migrate(config);
                if (migrated == null)
                {
                    // 迁移决定废弃旧布局（如 v2→v3 统一宽度改造）
                    return null;
                }

                Console.WriteLine($"[LayoutStore] 已加载布局：{migrated.Widgets.Count} 个组件（v{migrated.Version}）");
                return migrated;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LayoutStore] 加载失败: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// 布局配置迁移。返回 null 表示旧布局应被废弃、改用默认布局。
    ///
    /// v1 → v2：修复「隐藏全部组件」污染持久化的 bug。
    ///   v1 时代托盘临时隐藏会把 IsVisible 存成 false，导致重启后组件全部消失。
    ///   v2 起 PersistedVisible 与运行时 Visibility 分离，临时隐藏不再持久化。
    ///   迁移时强制把所有 IsVisible 置 true（v1 的 false 不可信，是 bug 产物）。
    ///
    /// v2 → v3：组件统一宽度 280px。旧布局宽度不一（260/280/300/311/326），
    ///   直接废弃重载默认布局。
    ///
    /// v3 → v4：组件记录所属显示器（MonitorKey），坐标语义改为相对该显示器工作区。
    ///   v3 只有主屏一个覆盖层，坐标本就相对主屏工作区，语义兼容；
    ///   MonitorKey 留空即代表主屏，**不废弃布局**，仅升版本号。
    /// </summary>
    public static LayoutConfig? Migrate(LayoutConfig config)
    {
        if (config.Version < 2)
        {
            var changed = false;
            foreach (var w in config.Widgets)
            {
                if (!w.IsVisible) { w.IsVisible = true; changed = true; }
            }
            config.Version = 2;
            if (changed)
                Console.WriteLine("[LayoutStore] 已迁移 v1 → v2：重置被错误隐藏的组件可见性");
        }

        if (config.Version < 3)
        {
            Console.WriteLine("[LayoutStore] v2 → v3：废弃旧布局（统一宽度 280px），将重新加载默认布局");
            return null;
        }

        if (config.Version < 4)
        {
            // 坐标语义兼容，MonitorKey 留空 = 主屏，无需改动条目数据
            config.Version = 4;
            Console.WriteLine($"[LayoutStore] 已迁移 v3 → v4：{config.Widgets.Count} 个组件归属主屏");
        }

        return config;
    }

    /// <summary>保存布局配置</summary>
    public void Save(LayoutConfig config)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                config.SavedAt = DateTime.Now;
                var json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LayoutStore] 保存失败: {ex.Message}");
            }
        }
    }
}
