using System.IO;
using System.Text.Json;
using zDesktop.Shell.Search;

namespace zDesktop.Shell.DiskMapper;

/// <summary>
/// 磁盘精简信息 — 单个分区的可读摘要（用于磁盘映射窗格展示）
/// </summary>
public sealed class DriveInfoLite
{
    /// <summary>盘符（如 "C:\"）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>卷标（如 "系统"），无卷标时为空</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>总容量（字节）</summary>
    public long TotalBytes { get; set; }

    /// <summary>可用空间（字节）</summary>
    public long AvailableBytes { get; set; }

    /// <summary>驱动器类型（Fixed / Removable / Network 等可读名称）</summary>
    public string DriveType { get; set; } = string.Empty;

    /// <summary>占用百分比（0-100），未就绪分区为 0</summary>
    public double UsagePercent { get; set; }
}

/// <summary>
/// 大文件条目 — 磁盘大文件扫描结果中的单个文件
/// </summary>
public sealed class FileInfoLite
{
    /// <summary>完整路径</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>大小（字节）</summary>
    public long Size { get; set; }

    /// <summary>最后修改时间</summary>
    public DateTime LastModified { get; set; }
}

/// <summary>
/// 目录条目 — 浏览某个目录时返回的单个文件 / 文件夹
/// </summary>
public sealed class FileEntryLite
{
    /// <summary>显示名称（含扩展名）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>完整路径</summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>是否为目录</summary>
    public bool IsDirectory { get; set; }

    /// <summary>大小（字节，目录为 0）</summary>
    public long Size { get; set; }

    /// <summary>最后修改时间</summary>
    public DateTime LastModified { get; set; }

    /// <summary>扩展名（含点，小写；目录为空）</summary>
    public string Extension { get; set; } = string.Empty;
}

/// <summary>
/// 文件类型分布统计 — 某个分类的文件数与总大小
/// </summary>
public sealed class TypeDistributionStat
{
    /// <summary>文件分类</summary>
    public FileCategory Category { get; set; }

    /// <summary>该分类下的文件数</summary>
    public int FileCount { get; set; }

    /// <summary>该分类下的总大小（字节）</summary>
    public long TotalBytes { get; set; }
}

/// <summary>
/// 通用导航历史栈 — 维护后退 / 前进两个方向的历史记录
///
/// 用于多窗格文件管理器中每个窗格独立的路径导航历史。
/// Navigate 入栈当前值并清空前进栈；Back / Forward 在两个栈间移动当前值。
/// </summary>
/// <typeparam name="T">历史条目类型（磁盘映射中为 string 路径）</typeparam>
public sealed class HistoryStack<T>
{
    /// <summary>后退栈（最近一次在栈顶，用 List 末尾模拟栈顶）</summary>
    private readonly List<T> _back = new();

    /// <summary>前进栈</summary>
    private readonly List<T> _forward = new();

    /// <summary>当前位置</summary>
    private T? _current;

    /// <summary>当前条目（首次导航前为 default）</summary>
    public T? Current => _current;

    /// <summary>是否可后退</summary>
    public bool CanGoBack => _back.Count > 0;

    /// <summary>是否可前进</summary>
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>后退栈只读视图（供下拉菜单展示）</summary>
    public IReadOnlyList<T> BackEntries => _back;

    /// <summary>前进栈只读视图（供下拉菜单展示）</summary>
    public IReadOnlyList<T> ForwardEntries => _forward;

    /// <summary>
    /// 导航到新条目 — 将当前值压入后退栈，清空前进栈，当前值更新为 <paramref name="value"/>。
    /// 与当前值相同（相等比较）时忽略，避免重复入栈。
    /// </summary>
    public void Navigate(T value)
    {
        if (_current is not null && EqualityComparer<T>.Default.Equals(_current, value))
        {
            _current = value; // 保持引用一致（值类型无影响）
            return;
        }
        if (_current is not null) _back.Add(_current);
        _current = value;
        _forward.Clear();
    }

    /// <summary>后退一步 — 当前值压入前进栈，后退栈顶出栈成为当前值；无可后退时返回当前值</summary>
    public T? Back()
    {
        if (_back.Count == 0) return _current;
        if (_current is not null) _forward.Add(_current);
        _current = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        return _current;
    }

    /// <summary>前进一步 — 当前值压入后退栈，前进栈顶出栈成为当前值；无可前进时返回当前值</summary>
    public T? Forward()
    {
        if (_forward.Count == 0) return _current;
        if (_current is not null) _back.Add(_current);
        _current = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        return _current;
    }
}

/// <summary>
/// 磁盘映射服务 — Q-Dir 风格多窗格文件管理器的数据层
///
/// 职责：
/// 1. 列出所有磁盘分区（GetDrives），过滤 Fixed / Removable / Network
/// 2. 磁盘大文件扫描（ScanLargeFilesAsync），递归遍历，按大小降序，支持 CancellationToken
/// 3. 文件类型分布统计（GetTypeDistribution），按 <see cref="FileCategory"/> 聚合文件数与大小
/// 4. 路径导航（GetDirectoryContents），列出目录内容（目录在前，文件在后）
/// 5. 每个窗格的导航历史（后退 / 前进），通过 <see cref="HistoryStack{T}"/> 按 paneId 维护
/// 6. 书签收藏，持久化到 %APPDATA%\zDesktop\bookmarks.json
///
/// 所有 IO 操作均容错：无权限目录 / 不可访问文件跳过，失败不影响整体功能。
/// </summary>
public sealed class DiskMapperService
{
    /// <summary>应用数据目录（%APPDATA%\zDesktop）</summary>
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");

    /// <summary>书签持久化文件</summary>
    private static readonly string BookmarkFile = Path.Combine(AppDataDir, "bookmarks.json");

    /// <summary>JSON 序列化选项</summary>
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>类型分布扫描的最大递归深度（避免全盘深扫导致 UI 卡顿）</summary>
    private const int DistributionMaxDepth = 4;

    /// <summary>书签列表（内存缓存，启动时从磁盘加载）</summary>
    private List<string> _bookmarks = new();

    /// <summary>每个窗格的导航历史（key = paneId）</summary>
    private readonly Dictionary<int, HistoryStack<string>> _histories = new();

    /// <summary>类型分布缓存（key = 盘根路径，避免重复深扫）</summary>
    private readonly Dictionary<string, List<TypeDistributionStat>> _distCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>构造磁盘映射服务，并加载已持久化的书签</summary>
    public DiskMapperService()
    {
        LoadBookmarks();
    }

    // ===== 磁盘列表 =====

    /// <summary>
    /// 列出所有可用磁盘分区 — 过滤 Fixed / Removable / Network，忽略未就绪分区。
    /// </summary>
    /// <returns>磁盘精简信息列表</returns>
    public List<DriveInfoLite> GetDrives()
    {
        var result = new List<DriveInfoLite>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return result; }

        foreach (var d in drives)
        {
            try
            {
                if (d.DriveType != DriveType.Fixed &&
                    d.DriveType != DriveType.Removable &&
                    d.DriveType != DriveType.Network)
                {
                    continue;
                }

                var lite = new DriveInfoLite
                {
                    Name = d.Name,
                    DriveType = d.DriveType.ToString(),
                };

                if (d.IsReady)
                {
                    lite.Label = d.VolumeLabel ?? string.Empty;
                    lite.TotalBytes = d.TotalSize;
                    lite.AvailableBytes = d.TotalFreeSpace;
                    lite.UsagePercent = d.TotalSize > 0
                        ? Math.Round((double)(d.TotalSize - d.TotalFreeSpace) / d.TotalSize * 100, 1)
                        : 0;
                }
                result.Add(lite);
            }
            catch
            {
                // 单个分区读取失败跳过
            }
        }
        return result;
    }

    // ===== 大文件扫描 =====

    /// <summary>
    /// 异步扫描指定盘根下的大文件（默认 ≥100MB），按大小降序返回。
    /// 支持取消；无权限目录跳过；取消时返回已扫描的部分结果。
    /// </summary>
    /// <param name="driveRoot">盘根路径（如 "C:\"）</param>
    /// <param name="minSizeMB">最小文件大小（MB），默认 100</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>大文件列表（按大小降序）</returns>
    public async Task<List<FileInfoLite>> ScanLargeFilesAsync(
        string driveRoot, int minSizeMB = 100, CancellationToken cancellationToken = default)
    {
        var minBytes = (long)minSizeMB * 1024 * 1024;
        var results = new List<FileInfoLite>();

        await Task.Run(() =>
        {
            try
            {
                ScanLargeFilesRecursive(driveRoot, minBytes, results, cancellationToken);
            }
            catch
            {
                // 递归内部已容错，此处兜底
            }
        }, cancellationToken);

        results.Sort((a, b) => b.Size.CompareTo(a.Size));
        return results;
    }

    /// <summary>递归扫描目录下的大文件</summary>
    private static void ScanLargeFilesRecursive(
        string dir, long minBytes, List<FileInfoLite> results, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir); }
        catch { return; }

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var fi = new FileInfo(file);
                if (fi.Length >= minBytes)
                {
                    results.Add(new FileInfoLite
                    {
                        Path = file,
                        Size = fi.Length,
                        LastModified = fi.LastWriteTime,
                    });
                }
            }
            catch
            {
                // 单个文件读取失败跳过
            }
        }

        IEnumerable<string> subdirs;
        try { subdirs = Directory.EnumerateDirectories(dir); }
        catch { return; }

        foreach (var sub in subdirs)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var di = new DirectoryInfo(sub);
                if ((di.Attributes & FileAttributes.Hidden) != 0 ||
                    (di.Attributes & FileAttributes.System) != 0)
                {
                    continue;
                }
            }
            catch
            {
                // 属性读取失败仍尝试递归
            }
            ScanLargeFilesRecursive(sub, minBytes, results, ct);
        }
    }

    // ===== 文件类型分布 =====

    /// <summary>
    /// 统计指定盘根下各 <see cref="FileCategory"/> 的文件数与总大小（用于迷你堆叠条）。
    /// 为避免全盘深扫卡顿，限制递归深度为 <see cref="DistributionMaxDepth"/>，结果按盘根缓存。
    /// </summary>
    /// <param name="driveRoot">盘根路径</param>
    /// <returns>各分类的统计列表</returns>
    public List<TypeDistributionStat> GetTypeDistribution(string driveRoot)
    {
        if (string.IsNullOrEmpty(driveRoot)) return new List<TypeDistributionStat>();
        if (_distCache.TryGetValue(driveRoot, out var cached)) return cached;

        var stats = new Dictionary<FileCategory, (int Count, long Bytes)>();
        foreach (FileCategory cat in Enum.GetValues(typeof(FileCategory)))
            stats[cat] = (0, 0);

        try
        {
            CollectTypeDistribution(driveRoot, stats, 0);
        }
        catch
        {
            // 容错
        }

        var list = stats
            .Select(kv => new TypeDistributionStat
            {
                Category = kv.Key,
                FileCount = kv.Value.Count,
                TotalBytes = kv.Value.Bytes,
            })
            .ToList();

        _distCache[driveRoot] = list;
        return list;
    }

    /// <summary>递归收集文件类型分布</summary>
    private static void CollectTypeDistribution(
        string dir, Dictionary<FileCategory, (int Count, long Bytes)> stats, int depth)
    {
        if (depth > DistributionMaxDepth) return;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir); }
        catch { return; }

        foreach (var file in files)
        {
            try
            {
                var fi = new FileInfo(file);
                var cat = FileIndexService.GetCategory(fi.Extension);
                var prev = stats[cat];
                stats[cat] = (prev.Count + 1, prev.Bytes + fi.Length);
            }
            catch
            {
                // 单个文件失败跳过
            }
        }

        if (depth >= DistributionMaxDepth) return;

        IEnumerable<string> subdirs;
        try { subdirs = Directory.EnumerateDirectories(dir); }
        catch { return; }

        foreach (var sub in subdirs)
        {
            try
            {
                var di = new DirectoryInfo(sub);
                if ((di.Attributes & FileAttributes.Hidden) != 0 ||
                    (di.Attributes & FileAttributes.System) != 0)
                {
                    continue;
                }
            }
            catch
            {
                // 属性读取失败仍尝试递归
            }
            CollectTypeDistribution(sub, stats, depth + 1);
        }
    }

    // ===== 路径导航 =====

    /// <summary>
    /// 列出指定目录的内容 — 目录在前（按名称升序），文件在后（按名称升序）。
    /// </summary>
    /// <param name="path">目录路径</param>
    /// <returns>目录条目列表；路径无效或无权限时返回空列表</returns>
    public List<FileEntryLite> GetDirectoryContents(string path)
    {
        var result = new List<FileEntryLite>();
        if (string.IsNullOrEmpty(path)) return result;

        var dirs = new List<FileEntryLite>();
        var files = new List<FileEntryLite>();

        try
        {
            if (!Directory.Exists(path)) return result;

            foreach (var d in Directory.EnumerateDirectories(path))
            {
                try
                {
                    var di = new DirectoryInfo(d);
                    dirs.Add(new FileEntryLite
                    {
                        Name = di.Name,
                        FullPath = di.FullName,
                        IsDirectory = true,
                        Size = 0,
                        LastModified = di.LastWriteTime,
                        Extension = string.Empty,
                    });
                }
                catch
                {
                    // 单个目录读取失败跳过
                }
            }

            foreach (var f in Directory.EnumerateFiles(path))
            {
                try
                {
                    var fi = new FileInfo(f);
                    files.Add(new FileEntryLite
                    {
                        Name = fi.Name,
                        FullPath = fi.FullName,
                        IsDirectory = false,
                        Size = fi.Length,
                        LastModified = fi.LastWriteTime,
                        Extension = fi.Extension ?? string.Empty,
                    });
                }
                catch
                {
                    // 单个文件读取失败跳过
                }
            }
        }
        catch
        {
            // 目录访问失败返回已收集的部分
        }

        dirs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        result.AddRange(dirs);
        result.AddRange(files);
        return result;
    }

    // ===== 每窗格导航历史 =====

    /// <summary>获取指定窗格的导航历史栈（不存在则创建）</summary>
    /// <param name="paneId">窗格标识（通常为窗格索引）</param>
    /// <returns>该窗格专属的路径历史栈</returns>
    public HistoryStack<string> GetHistory(int paneId)
    {
        if (!_histories.TryGetValue(paneId, out var hist))
        {
            hist = new HistoryStack<string>();
            _histories[paneId] = hist;
        }
        return hist;
    }

    /// <summary>导航到新路径并记入指定窗格的历史栈</summary>
    /// <param name="paneId">窗格标识</param>
    /// <param name="path">目标路径</param>
    public void Navigate(int paneId, string path)
    {
        GetHistory(paneId).Navigate(path);
    }

    /// <summary>指定窗格后退一步，返回目标路径（无可后退时返回当前路径）</summary>
    public string? Back(int paneId) => GetHistory(paneId).Back();

    /// <summary>指定窗格前进一步，返回目标路径（无可前进时返回当前路径）</summary>
    public string? Forward(int paneId) => GetHistory(paneId).Forward();

    // ===== 书签收藏 =====

    /// <summary>书签列表（只读视图）</summary>
    public IReadOnlyList<string> Bookmarks => _bookmarks;

    /// <summary>添加书签（去重，重复添加不产生副本）</summary>
    /// <param name="path">要收藏的路径</param>
    public void AddBookmark(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_bookmarks.Any(b => string.Equals(b, path, StringComparison.OrdinalIgnoreCase))) return;
        _bookmarks.Add(path);
        SaveBookmarks();
    }

    /// <summary>移除书签（不存在则忽略）</summary>
    /// <param name="path">要移除的路径</param>
    public void RemoveBookmark(string path)
    {
        var idx = _bookmarks.FindIndex(b => string.Equals(b, path, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            _bookmarks.RemoveAt(idx);
            SaveBookmarks();
        }
    }

    /// <summary>从 bookmarks.json 加载书签</summary>
    private void LoadBookmarks()
    {
        try
        {
            if (!File.Exists(BookmarkFile)) return;
            var json = File.ReadAllText(BookmarkFile);
            _bookmarks = JsonSerializer.Deserialize<List<string>>(json) ?? new();
        }
        catch
        {
            _bookmarks = new();
        }
    }

    /// <summary>保存书签到 bookmarks.json</summary>
    private void SaveBookmarks()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var json = JsonSerializer.Serialize(_bookmarks, JsonOpts);
            File.WriteAllText(BookmarkFile, json);
        }
        catch
        {
            // 持久化失败不影响功能
        }
    }
}
