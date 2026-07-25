using System.IO;
using System.Text.Json;
using zDesktop.Core.Layout;

namespace zDesktop.Shell.Layout;

/// <summary>
/// 布局持久化服务 — JSON 文件存储
///
/// 存储路径：%APPDATA%\zDesktop\layout.json
/// 线程安全：所有读写加锁
/// </summary>
public sealed class LayoutStore
{
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");

    private static readonly string FilePath = Path.Combine(AppDataDir, "layout.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _lock = new();

    /// <summary>加载布局配置，文件不存在或解析失败返回 null</summary>
    public LayoutConfig? Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(FilePath)) return null;

                var json = File.ReadAllText(FilePath);
                var config = JsonSerializer.Deserialize<LayoutConfig>(json, JsonOptions);
                if (config != null)
                {
                    var migrated = Migrate(config);
                    if (migrated == null)
                    {
                        // 迁移决定废弃旧布局（如 v3 统一宽度改造）
                        return null;
                    }
                    Console.WriteLine($"[LayoutStore] 已加载布局：{migrated.Widgets.Count} 个组件（v{migrated.Version}）");
                    return migrated;
                }
                return config;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LayoutStore] 加载失败: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// 布局配置迁移
    /// v1 → v2：修复"隐藏全部组件"污染持久化的 bug。
    ///   v1 时代托盘临时隐藏会把 IsVisible 存成 false，导致重启后组件全部消失。
    ///   v2 起 PersistedVisible 与运行时 Visibility 分离，临时隐藏不再持久化。
    ///   迁移时强制把所有 IsVisible 置 true（v1 的 false 不可信，是 bug 产物）。
    /// v2 → v3：组件统一宽度 280px + 桌面图标竖排重排。
    ///   v2 时代各组件宽度不一致（260/280/300/311/326），视觉不整齐。
    ///   v3 起所有组件统一 280px 宽度，直接废弃旧布局重新加载默认布局。
    /// </summary>
    private static LayoutConfig Migrate(LayoutConfig config)
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
            // v3：组件统一宽度改造，旧布局宽度不一致，废弃重载默认布局
            config.Version = 3;
            Console.WriteLine("[LayoutStore] 已迁移 v2 → v3：废弃旧布局（统一宽度 280px），将重新加载默认布局");
            return null!;
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
                Directory.CreateDirectory(AppDataDir);
                config.SavedAt = DateTime.Now;
                var json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LayoutStore] 保存失败: {ex.Message}");
            }
        }
    }
}
