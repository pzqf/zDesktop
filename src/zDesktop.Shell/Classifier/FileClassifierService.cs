using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using zDesktop.Shell.Search;

namespace zDesktop.Shell.Classifier;

/// <summary>
/// 已分类文件条目 — 桌面扫描结果的单文件视图
/// </summary>
public sealed class ClassifiedFile
{
    /// <summary>完整路径</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>文件名（含扩展名）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>扩展名（含点，小写）</summary>
    public string Extension { get; set; } = string.Empty;

    /// <summary>大小（字节）</summary>
    public long Size { get; set; }

    /// <summary>文件类别（复用 FileIndexService 的 FileCategory 枚举）</summary>
    public FileCategory Category { get; set; }

    /// <summary>最后修改时间</summary>
    public DateTime LastModified { get; set; }

    /// <summary>所在父目录名（用于项目标签推断）</summary>
    public string ParentFolder { get; set; } = string.Empty;
}

/// <summary>
/// 自定义分区配置 — 用户定义的文件分组规则
/// </summary>
public sealed class PartitionConfig
{
    /// <summary>分区名称（如 "设计素材"）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>分区颜色（十六进制字符串，如 "#6c5ce7"）</summary>
    public string Color { get; set; } = "#6c5ce7";

    /// <summary>匹配的扩展名列表（含或不含点均可，小写比较）</summary>
    public List<string> Extensions { get; set; } = new();

    /// <summary>文件名正则匹配（可选，为空则仅按扩展名匹配）</summary>
    public string? NamePattern { get; set; }
}

/// <summary>
/// 桌面整理结果 — 一键整理后的统计摘要
/// </summary>
public sealed class OrganizeResult
{
    /// <summary>成功移动的文件数</summary>
    public int MovedCount { get; set; }

    /// <summary>跳过的文件数（无扩展名/已在目标分类文件夹内/目标与源相同）</summary>
    public int SkippedCount { get; set; }

    /// <summary>失败的文件数（IO 异常或权限不足）</summary>
    public int FailedCount { get; set; }

    /// <summary>本次创建的分类文件夹列表</summary>
    public List<string> CreatedFolders { get; set; } = new();

    /// <summary>失败文件明细（路径 → 失败原因），用于 UI 提示</summary>
    public Dictionary<string, string> Failures { get; set; } = new();
}

/// <summary>
/// 存储分布条目 — 单个类别的文件数与总大小
/// </summary>
public sealed class DistributionEntry
{
    /// <summary>文件类别</summary>
    public FileCategory Category { get; set; }

    /// <summary>该类别文件数</summary>
    public int Count { get; set; }

    /// <summary>该类别总大小（字节）</summary>
    public long TotalSize { get; set; }
}

/// <summary>
/// 时间标签 — 用于智能标签的时间维度
/// </summary>
public enum TimeTag
{
    /// <summary>今天</summary>
    Today,
    /// <summary>本周</summary>
    ThisWeek,
    /// <summary>本月</summary>
    ThisMonth,
    /// <summary>更早</summary>
    Earlier,
}

/// <summary>
/// 智能文件分类与一键整理服务
///
/// 职责：
/// - 扫描桌面（用户桌面 + 公共桌面）所有文件（不含子目录）
/// - 复用 <see cref="FileIndexService.GetCategory"/> 判定类别，避免重复定义扩展名白名单
/// - 提供分类结果列表与存储分布统计（用于可视化堆叠条）
/// - 一键整理：在桌面创建分类文件夹并移动文件，处理同名冲突
/// - 自定义分区：用户定义分区规则（名称/颜色/扩展名/文件名正则），持久化到 partitions.json
/// - 智能标签：提取时间标签（今天/本周/本月/更早）与项目标签（基于父目录名）
///
/// 容错原则：所有 IO 操作 try-catch，单个文件失败不影响整体扫描/整理。
/// </summary>
public sealed class FileClassifierService
{
    // ===== 持久化路径 =====

    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");

    private static readonly string PartitionFile = Path.Combine(AppDataDir, "partitions.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    // ===== 类别 → 整理文件夹名映射 =====

    /// <summary>
    /// 类别 → 桌面整理目标文件夹名（中文，对应设计稿分类）
    /// </summary>
    public static readonly IReadOnlyDictionary<FileCategory, string> CategoryFolderNames =
        new Dictionary<FileCategory, string>
        {
            [FileCategory.Document] = "文档",
            [FileCategory.Image] = "图片",
            [FileCategory.Video] = "视频",
            [FileCategory.Music] = "音乐",
            [FileCategory.Application] = "应用",
            [FileCategory.Archive] = "压缩包",
            [FileCategory.Code] = "代码",
            [FileCategory.Other] = "其他",
        };

    /// <summary>
    /// 类别语义色板 — 数据语义色（非 UI token），用于堆叠条/图例渲染。
    /// 与 Theme 的状态色对齐：图片=信息蓝、文档=成功绿、视频=警告橙、音乐=错误红等。
    /// </summary>
    public static readonly IReadOnlyDictionary<FileCategory, Color> CategoryColors =
        new Dictionary<FileCategory, Color>
        {
            [FileCategory.Image] = Color.FromRgb(0x3b, 0x82, 0xf6),       // 信息蓝
            [FileCategory.Document] = Color.FromRgb(0x10, 0xb9, 0x81),     // 成功绿
            [FileCategory.Video] = Color.FromRgb(0xf5, 0x9e, 0x0b),        // 警告橙
            [FileCategory.Music] = Color.FromRgb(0xef, 0x44, 0x44),        // 错误红
            [FileCategory.Application] = Color.FromRgb(0x6c, 0x5c, 0xe7),  // 品牌紫
            [FileCategory.Archive] = Color.FromRgb(0xf9, 0x73, 0x16),      // 橙
            [FileCategory.Code] = Color.FromRgb(0x14, 0xb8, 0xa6),         // 青绿
            [FileCategory.Other] = Color.FromRgb(0x9c, 0xa3, 0xaf),        // 次要灰
        };

    /// <summary>类别显示图标（Unicode 字符，避免引入图标库）</summary>
    public static readonly IReadOnlyDictionary<FileCategory, string> CategoryIcons =
        new Dictionary<FileCategory, string>
        {
            [FileCategory.Document] = "\uE8A5",    // 文档
            [FileCategory.Image] = "\uEB9F",       // 图片
            [FileCategory.Video] = "\uE8B2",       // 视频
            [FileCategory.Music] = "\uEC4F",       // 音乐
            [FileCategory.Application] = "\uECA5", // 应用
            [FileCategory.Archive] = "\uE7B8",     // 压缩包
            [FileCategory.Code] = "\uE943",        // 代码
            [FileCategory.Other] = "\uE7C3",       // 其他
        };

    // ===== 状态 =====

    /// <summary>自定义分区列表（线程安全读写）</summary>
    private List<PartitionConfig> _partitions = new();

    private readonly object _partitionLock = new();

    /// <summary>已编译的分区正则缓存（PartitionConfig 引用 → 编译后的 Regex）</summary>
    private readonly Dictionary<PartitionConfig, Regex?> _regexCache = new();

    // ===== 构造 =====

    /// <summary>构造文件分类服务，加载已保存的自定义分区</summary>
    public FileClassifierService()
    {
        LoadPartitions();
    }

    // ===== 桌面扫描 =====

    /// <summary>
    /// 扫描桌面所有文件（不含子目录）。
    /// 同时扫描用户桌面（Desktop）与公共桌面（CommonDesktopDirectory）。
    /// </summary>
    /// <returns>已分类文件列表（按类别 → 文件名排序）</returns>
    public List<ClassifiedFile> ScanDesktop()
    {
        var result = new List<ClassifiedFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in GetDesktopDirectories())
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }

            foreach (var file in files)
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (IsHiddenOrSystem(fi.Attributes)) continue;

                    var key = file.ToLowerInvariant();
                    if (seen.Contains(key)) continue;
                    seen.Add(key);

                    var ext = fi.Extension ?? string.Empty;
                    result.Add(new ClassifiedFile
                    {
                        Path = file,
                        Name = fi.Name,
                        Extension = ext,
                        Size = fi.Length,
                        Category = FileIndexService.GetCategory(ext),
                        LastModified = fi.LastWriteTime,
                        ParentFolder = fi.Directory?.Name ?? string.Empty,
                    });
                }
                catch
                {
                    // 跳过无法访问的单个文件
                }
            }
        }

        // 按类别 → 文件名排序，便于 UI 稳定展示
        return result
            .OrderBy(f => f.Category)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>获取桌面目录列表（用户桌面 + 公共桌面，去重存在项）</summary>
    private static IEnumerable<string> GetDesktopDirectories()
    {
        var dirs = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        return dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>判断文件属性是否为隐藏/系统（不扫描）</summary>
    private static bool IsHiddenOrSystem(FileAttributes attr)
    {
        return (attr & FileAttributes.Hidden) != 0 || (attr & FileAttributes.System) != 0;
    }

    // ===== 存储分布 =====

    /// <summary>
    /// 计算分类文件的存储分布统计 — 各类别文件数与总大小。
    /// 用于 UI 渲染水平堆叠条与图例。
    /// </summary>
    /// <param name="files">已分类文件列表（通常来自 <see cref="ScanDesktop"/>）</param>
    /// <returns>按类别聚合的分布条目列表（按大小降序）</returns>
    public List<DistributionEntry> GetDistribution(IEnumerable<ClassifiedFile> files)
    {
        var map = new Dictionary<FileCategory, (int Count, long Size)>();

        foreach (var f in files)
        {
            if (!map.TryGetValue(f.Category, out var cur))
            {
                cur = (0, 0);
            }
            map[f.Category] = (cur.Count + 1, cur.Size + f.Size);
        }

        return map
            .Select(kv => new DistributionEntry
            {
                Category = kv.Key,
                Count = kv.Value.Count,
                TotalSize = kv.Value.Size,
            })
            .OrderByDescending(e => e.TotalSize)
            .ToList();
    }

    /// <summary>计算文件列表总大小（字节）</summary>
    public static long GetTotalSize(IEnumerable<ClassifiedFile> files)
    {
        long sum = 0;
        foreach (var f in files) checked { sum += f.Size; }
        return sum;
    }

    // ===== 一键整理 =====

    /// <summary>
    /// 一键整理桌面 — 在桌面创建分类文件夹，将文件移入对应类别文件夹。
    /// - 移动前检查目标文件夹是否存在，不存在则创建
    /// - 同名文件冲突时自动重命名（加序号，如 "报告.docx" → "报告 (1).docx"）
    /// - 单个文件失败不影响整体整理
    /// </summary>
    /// <param name="files">要整理的文件列表（通常来自 <see cref="ScanDesktop"/>）</param>
    /// <returns>整理结果统计</returns>
    public OrganizeResult OrganizeDesktop(IEnumerable<ClassifiedFile> files)
    {
        var result = new OrganizeResult();
        var desktopDirs = GetDesktopDirectories().ToList();
        if (desktopDirs.Count == 0)
        {
            return result;
        }

        // 以用户桌面为整理根目录（公共桌面文件也会被移入用户桌面的分类文件夹）
        var root = desktopDirs[0];

        foreach (var file in files)
        {
            try
            {
                // 跳过目录（理论上 ScanDesktop 只返回文件，防御性检查）
                if (string.IsNullOrEmpty(file.Path) || !File.Exists(file.Path))
                {
                    result.SkippedCount++;
                    continue;
                }

                var folderName = CategoryFolderNames.TryGetValue(file.Category, out var fn)
                    ? fn
                    : CategoryFolderNames[FileCategory.Other];
                var targetDir = Path.Combine(root, folderName);

                // 创建目标文件夹（若不存在）
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                    if (!result.CreatedFolders.Contains(targetDir))
                        result.CreatedFolders.Add(targetDir);
                }

                // 跳过：文件已在某个分类文件夹内（避免重复整理嵌套）
                var currentDir = Path.GetDirectoryName(file.Path);
                if (string.Equals(currentDir, targetDir, StringComparison.OrdinalIgnoreCase))
                {
                    result.SkippedCount++;
                    continue;
                }

                var destPath = ResolveUniquePath(targetDir, file.Name);

                // 源与目标完全相同（理论上前面已跳过，此处再防御）
                if (string.Equals(file.Path, destPath, StringComparison.OrdinalIgnoreCase))
                {
                    result.SkippedCount++;
                    continue;
                }

                File.Move(file.Path, destPath);
                result.MovedCount++;
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                result.Failures[file.Path] = ex.Message;
            }
        }

        return result;
    }

    /// <summary>
    /// 解析目标目录内不冲突的唯一文件名 — 同名时加序号后缀。
    /// 例：report.docx 已存在 → report (1).docx → report (2).docx …
    /// </summary>
    private static string ResolveUniquePath(string targetDir, string fileName)
    {
        var dest = Path.Combine(targetDir, fileName);
        if (!File.Exists(dest)) return dest;

        var nameNoExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 10000; i++)
        {
            var candidate = Path.Combine(targetDir, $"{nameNoExt} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        // 极端情况：用 GUID 兜底，确保不抛异常
        return Path.Combine(targetDir, $"{nameNoExt}_{Guid.NewGuid():N}{ext}");
    }

    // ===== 自定义分区 =====

    /// <summary>获取所有自定义分区（快照副本）</summary>
    public IReadOnlyList<PartitionConfig> GetPartitions()
    {
        lock (_partitionLock)
        {
            return _partitions.ToList();
        }
    }

    /// <summary>
    /// 添加自定义分区并持久化。
    /// </summary>
    /// <param name="partition">分区配置</param>
    public void AddPartition(PartitionConfig partition)
    {
        if (partition == null) throw new ArgumentNullException(nameof(partition));
        if (string.IsNullOrWhiteSpace(partition.Name))
            throw new ArgumentException("分区名称不能为空", nameof(partition));

        // 规范化扩展名（统一含点、小写）
        partition.Extensions = partition.Extensions
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(NormalizeExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_partitionLock)
        {
            _partitions.Add(partition);
        }
        SavePartitions();
    }

    /// <summary>
    /// 按名称删除自定义分区并持久化。
    /// </summary>
    /// <param name="name">分区名称</param>
    /// <returns>是否删除成功</returns>
    public bool RemovePartition(string name)
    {
        bool removed;
        lock (_partitionLock)
        {
            removed = _partitions.RemoveAll(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
        }
        if (removed) SavePartitions();
        return removed;
    }

    /// <summary>
    /// 判断文件是否匹配某个自定义分区（按分区定义顺序，返回首个匹配项）。
    /// 匹配规则：扩展名命中 或 文件名正则命中（二者满足其一即匹配）。
    /// </summary>
    /// <param name="file">待匹配文件</param>
    /// <returns>匹配的分区配置，无匹配返回 null</returns>
    public PartitionConfig? MatchFile(ClassifiedFile file)
    {
        if (file == null) return null;

        List<PartitionConfig> snapshot;
        lock (_partitionLock)
        {
            snapshot = _partitions.ToList();
        }

        foreach (var p in snapshot)
        {
            // 1. 扩展名匹配
            if (p.Extensions.Count > 0)
            {
                var ext = NormalizeExtension(file.Extension);
                if (p.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    return p;
            }

            // 2. 文件名正则匹配
            if (!string.IsNullOrWhiteSpace(p.NamePattern))
            {
                var regex = GetOrCompileRegex(p);
                if (regex != null && regex.IsMatch(file.Name))
                    return p;
            }
        }

        return null;
    }

    /// <summary>规范化扩展名（含点、小写）</summary>
    private static string NormalizeExtension(string ext)
    {
        if (string.IsNullOrEmpty(ext)) return string.Empty;
        var e = ext.ToLowerInvariant();
        if (!e.StartsWith(".")) e = "." + e;
        return e;
    }

    /// <summary>获取或编译分区的文件名正则（缓存编译结果，避免重复编译）</summary>
    private Regex? GetOrCompileRegex(PartitionConfig partition)
    {
        if (_regexCache.TryGetValue(partition, out var cached)) return cached;

        Regex? compiled = null;
        try
        {
            compiled = new Regex(partition.NamePattern!, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        }
        catch
        {
            // 正则非法则该分区的正则规则失效（不影响扩展名匹配）
            compiled = null;
        }
        _regexCache[partition] = compiled;
        return compiled;
    }

    // ===== 智能标签 =====

    /// <summary>
    /// 根据最后修改时间计算时间标签。
    /// - 今天：与当前日期同一天
    /// - 本周：本周内（以周一为一周起点）但非今天
    /// - 本月：本月内但非本周
    /// - 更早：其余
    /// </summary>
    public static TimeTag GetTimeTag(DateTime lastModified)
    {
        var now = DateTime.Now;
        if (lastModified.Date == now.Date) return TimeTag.Today;

        // 计算本周一（DayOfWeek.Sunday=0，转换为周一起算）
        var diffToMonday = ((int)now.DayOfWeek + 6) % 7;
        var thisWeekStart = now.Date.AddDays(-diffToMonday);
        if (lastModified.Date >= thisWeekStart) return TimeTag.ThisWeek;

        if (lastModified.Year == now.Year && lastModified.Month == now.Month)
            return TimeTag.ThisMonth;

        return TimeTag.Earlier;
    }

    /// <summary>
    /// 根据文件所在父目录名推断项目标签。
    /// 若父目录为桌面根（空或 "Desktop"）则返回空字符串。
    /// </summary>
    public static string GetProjectTag(string parentFolder)
    {
        if (string.IsNullOrWhiteSpace(parentFolder)) return string.Empty;
        if (string.Equals(parentFolder, "Desktop", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (string.Equals(parentFolder, "公共桌面", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return parentFolder;
    }

    // ===== 分区持久化 =====

    /// <summary>从 partitions.json 加载自定义分区</summary>
    private void LoadPartitions()
    {
        try
        {
            if (!File.Exists(PartitionFile)) return;
            var json = File.ReadAllText(PartitionFile);
            var list = JsonSerializer.Deserialize<List<PartitionConfig>>(json, JsonOpts);
            if (list != null)
            {
                _partitions = list;
                Console.WriteLine($"[FileClassifier] 已加载 {_partitions.Count} 个自定义分区");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileClassifier] 加载分区失败: {ex.Message}");
            _partitions = new();
        }
    }

    /// <summary>保存自定义分区到 partitions.json</summary>
    private void SavePartitions()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            List<PartitionConfig> snapshot;
            lock (_partitionLock)
            {
                snapshot = _partitions.ToList();
            }
            var json = JsonSerializer.Serialize(snapshot, JsonOpts);
            File.WriteAllText(PartitionFile, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileClassifier] 保存分区失败: {ex.Message}");
        }
    }
}
