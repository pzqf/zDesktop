using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace zDesktop.Shell.Launcher;

/// <summary>
/// 已安装应用索引 — 扫描开始菜单快捷方式，建立可搜索的应用列表
///
/// 扫描位置：
/// - %APPDATA%\Microsoft\Windows\Start Menu\Programs（当前用户）
/// - %ProgramData%\Microsoft\Windows\Start Menu\Programs（所有用户）
/// - UWP 应用暂不支持（需要 WinRT API）
///
/// 缓存：扫描结果缓存到 %APPDATA%\zDesktop\app-index.json，启动时加载
/// </summary>
public sealed class AppIndex
{
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");

    private static readonly string CacheFile = Path.Combine(AppDataDir, "app-index.json");

    private List<AppEntry> _entries = new();

    /// <summary>所有已索引的应用</summary>
    public IReadOnlyList<AppEntry> Entries => _entries;

    /// <summary>
    /// 加载索引 — 优先从缓存加载，缓存不存在或过期则重新扫描
    /// </summary>
    /// <param name="forceRescan">强制重新扫描</param>
    public void Load(bool forceRescan = false)
    {
        if (!forceRescan)
        {
            TryLoadCache();
            if (_entries.Count > 0)
            {
                Console.WriteLine($"[AppIndex] 从缓存加载 {_entries.Count} 个应用");
                return;
            }
        }

        Rescan();
    }

    /// <summary>重新扫描开始菜单，更新索引</summary>
    public void Rescan()
    {
        _entries.Clear();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 扫描目录
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs"),
        };

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            ScanDirectory(dir, seen);
        }

        // 按名称排序
        _entries = _entries.OrderBy(e => e.Name).ToList();

        SaveCache();
        Console.WriteLine($"[AppIndex] 扫描完成，索引 {_entries.Count} 个应用");
    }

    /// <summary>递归扫描目录下的 .lnk 快捷方式</summary>
    private void ScanDirectory(string dir, HashSet<string> seen)
    {
        try
        {
            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(lnk);
                    if (string.IsNullOrEmpty(name)) continue;

                    // 去重（同名快捷方式只保留第一个）
                    var key = name.ToLowerInvariant();
                    if (seen.Contains(key)) continue;
                    seen.Add(key);

                    // 解析快捷方式目标路径
                    var target = ResolveShortcut(lnk);
                    if (string.IsNullOrEmpty(target)) continue;

                    // 过滤卸载程序
                    if (name.Contains("卸载", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                        continue;

                    _entries.Add(new AppEntry
                    {
                        Name = name,
                        ShortcutPath = lnk,
                        TargetPath = target,
                    });
                }
                catch
                {
                    // 跳过无法解析的快捷方式
                }
            }
        }
        catch
        {
            // 跳过无权限访问的目录
        }
    }

    /// <summary>解析 .lnk 快捷方式的目标路径（使用 WScript.Shell COM）</summary>
    private static string ResolveShortcut(string lnkPath)
    {
        try
        {
            // 使用 COM 解析快捷方式
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return string.Empty;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            var target = (string)shortcut.TargetPath;
            if (string.IsNullOrEmpty(target) || !File.Exists(target))
            {
                // 有些快捷方式目标为空，直接用快捷方式本身启动
                return lnkPath;
            }
            return target;
        }
        catch
        {
            return lnkPath;
        }
    }

    /// <summary>搜索应用 — 模糊匹配名称</summary>
    public IEnumerable<AppEntry> Search(string query, int maxResults = 8)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _entries.Take(maxResults);

        var q = query.Trim();

        // 优先级：前缀匹配 > 包含匹配
        var prefix = _entries
            .Where(e => e.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .ToList();

        if (prefix.Count >= maxResults) return prefix;

        var contains = _entries
            .Where(e => !prefix.Contains(e) &&
                        e.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults - prefix.Count)
            .ToList();

        return prefix.Concat(contains);
    }

    /// <summary>启动应用</summary>
    public bool Launch(AppEntry entry)
    {
        try
        {
            // 优先用快捷方式启动（保留图标、参数等元信息）
            var path = File.Exists(entry.ShortcutPath) ? entry.ShortcutPath : entry.TargetPath;
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            Console.WriteLine($"[AppIndex] 已启动: {entry.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AppIndex] 启动失败: {ex.Message}");
            return false;
        }
    }

    // ===== 缓存持久化 =====

    private void TryLoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return;
            var json = File.ReadAllText(CacheFile);
            _entries = JsonSerializer.Deserialize<List<AppEntry>>(json) ?? new();
        }
        catch
        {
            _entries = new();
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CacheFile, json);
        }
        catch
        {
            // 缓存写入失败不影响功能
        }
    }
}

/// <summary>应用索引条目</summary>
public sealed class AppEntry
{
    /// <summary>显示名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>快捷方式路径（.lnk）</summary>
    public string ShortcutPath { get; set; } = string.Empty;

    /// <summary>目标可执行文件路径</summary>
    public string TargetPath { get; set; } = string.Empty;
}
