using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace zDesktop.Shell.Search;

/// <summary>
/// 文件类别 — 对应全局搜索设计稿的分类维度
/// </summary>
public enum FileCategory
{
    /// <summary>其他/未分类</summary>
    Other,
    /// <summary>文档（doc/docx/pdf/txt/xlsx/pptx 等）</summary>
    Document,
    /// <summary>图片（png/jpg/gif/svg 等）</summary>
    Image,
    /// <summary>视频（mp4/avi/mkv 等）</summary>
    Video,
    /// <summary>音频（mp3/wav/flac 等）</summary>
    Music,
    /// <summary>应用程序（exe/msi 等）</summary>
    Application,
    /// <summary>压缩包（zip/rar/7z 等）</summary>
    Archive,
    /// <summary>代码（cs/js/py/json 等）</summary>
    Code,
}

/// <summary>
/// 文件索引条目 — 单个文件的元信息
/// </summary>
public sealed class FileEntry
{
    /// <summary>完整路径</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>文件名（含扩展名）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>扩展名（含点，小写）</summary>
    public string Extension { get; set; } = string.Empty;

    /// <summary>大小（字节）</summary>
    public long Size { get; set; }

    /// <summary>最后修改时间</summary>
    public DateTime LastModified { get; set; }

    /// <summary>分类</summary>
    public FileCategory Category { get; set; }

    /// <summary>文件名拼音首字母（小写，用于中文模糊匹配）</summary>
    public string PinyinInitials { get; set; } = string.Empty;
}

/// <summary>
/// 内置文件索引服务 — 当未安装 Everything 时的降级方案
///
/// 职责：
/// - 后台异步扫描用户常用目录（桌面、文档、下载、图片等 SpecialFolder），建立文件路径索引
/// - 缓存到 %APPDATA%\zDesktop\file-index.json，启动优先加载缓存
/// - 提供 Search(query, maxResults) 方法，支持：文件名包含匹配、扩展名通配符（*.docx）、拼音首字母模糊匹配
/// - 提供 ForceRescan() 重新扫描
///
/// 参照 AppIndex 的缓存/扫描/JSON 序列化风格
/// </summary>
public sealed class FileIndexService
{
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");

    private static readonly string CacheFile = Path.Combine(AppDataDir, "file-index.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>最大索引文件数（避免内存溢出）</summary>
    private const int MaxFiles = 50000;

    /// <summary>最大递归深度</summary>
    private const int MaxDepth = 5;

    private List<FileEntry> _entries = new();

    /// <summary>所有已索引的文件</summary>
    public IReadOnlyList<FileEntry> Entries => _entries;

    /// <summary>已索引文件数</summary>
    public int Count => _entries.Count;

    /// <summary>加载索引 — 优先从缓存加载，缓存不存在则重新扫描</summary>
    /// <param name="forceRescan">强制重新扫描</param>
    public void Load(bool forceRescan = false)
    {
        if (!forceRescan)
        {
            TryLoadCache();
            if (_entries.Count > 0)
            {
                Console.WriteLine($"[FileIndex] 从缓存加载 {_entries.Count} 个文件");
                return;
            }
        }
        Rescan();
    }

    /// <summary>异步加载索引（不阻塞 UI 线程）</summary>
    public Task LoadAsync(bool forceRescan = false)
    {
        return Task.Run(() => Load(forceRescan));
    }

    /// <summary>强制重新扫描，更新索引</summary>
    public void ForceRescan()
    {
        Rescan();
    }

    /// <summary>重新扫描用户常用目录</summary>
    private void Rescan()
    {
        _entries.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in GetScanDirectories())
        {
            ScanDirectory(dir, seen, 0);
        }

        _entries = _entries.OrderBy(e => e.Name).ToList();
        SaveCache();
        Console.WriteLine($"[FileIndex] 扫描完成，索引 {_entries.Count} 个文件");
    }

    /// <summary>获取用户常用扫描目录（桌面、文档、下载、图片、音乐、视频）</summary>
    private static IEnumerable<string> GetScanDirectories()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Path.Combine(profile, "Downloads"),
        };
        return candidates.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>递归扫描目录下的文件</summary>
    private void ScanDirectory(string dir, HashSet<string> seen, int depth)
    {
        if (depth > MaxDepth || _entries.Count >= MaxFiles) return;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir); }
        catch { return; }

        foreach (var file in files)
        {
            if (_entries.Count >= MaxFiles) return;
            try
            {
                var fi = new FileInfo(file);
                if (IsHiddenOrSystem(fi.Attributes)) continue;

                var key = file.ToLowerInvariant();
                if (seen.Contains(key)) continue;
                seen.Add(key);

                var ext = fi.Extension ?? string.Empty;
                _entries.Add(new FileEntry
                {
                    Path = file,
                    Name = fi.Name,
                    Extension = ext,
                    Size = fi.Length,
                    LastModified = fi.LastWriteTime,
                    Category = GetCategory(ext),
                    PinyinInitials = GetPinyinInitials(fi.Name),
                });
            }
            catch
            {
                // 跳过无法访问的文件
            }
        }

        IEnumerable<string> subdirs;
        try { subdirs = Directory.EnumerateDirectories(dir); }
        catch { return; }

        foreach (var sub in subdirs)
        {
            try
            {
                var di = new DirectoryInfo(sub);
                if (IsHiddenOrSystem(di.Attributes)) continue;
                ScanDirectory(sub, seen, depth + 1);
            }
            catch
            {
                // 跳过无权限目录
            }
        }
    }

    /// <summary>判断文件属性是否为隐藏/系统（不索引）</summary>
    private static bool IsHiddenOrSystem(FileAttributes attr)
    {
        return (attr & FileAttributes.Hidden) != 0 || (attr & FileAttributes.System) != 0;
    }

    /// <summary>
    /// 搜索文件 — 支持文件名包含、扩展名通配符（*.docx）、拼音首字母模糊匹配
    /// </summary>
    /// <param name="query">查询关键词</param>
    /// <param name="maxResults">最大结果数</param>
    /// <returns>匹配的文件条目（按 前缀 > 包含 > 拼音 优先级排序）</returns>
    public IEnumerable<FileEntry> Search(string query, int maxResults = 20)
    {
        if (string.IsNullOrWhiteSpace(query) || _entries.Count == 0)
            return Enumerable.Empty<FileEntry>();

        var q = query.Trim();
        var results = new List<FileEntry>(maxResults);
        var matched = new HashSet<FileEntry>();

        // 1. 扩展名通配符：*.docx
        if (q.StartsWith("*") && q.Length > 1)
        {
            var ext = q.Substring(1).ToLowerInvariant();
            if (!ext.StartsWith(".")) ext = "." + ext;
            foreach (var e in _entries)
            {
                if (e.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(e);
                    if (results.Count >= maxResults) break;
                }
            }
            return results;
        }

        var qLower = q.ToLowerInvariant();

        // 2. 前缀匹配（最高优先级）
        foreach (var e in _entries)
        {
            if (e.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(e);
                results.Add(e);
                if (results.Count >= maxResults) return results;
            }
        }

        // 3. 包含匹配
        foreach (var e in _entries)
        {
            if (!matched.Contains(e) &&
                e.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(e);
                results.Add(e);
                if (results.Count >= maxResults) return results;
            }
        }

        // 4. 拼音首字母模糊匹配（对中文文件名有意义）
        foreach (var e in _entries)
        {
            if (!matched.Contains(e) &&
                !string.IsNullOrEmpty(e.PinyinInitials) &&
                e.PinyinInitials.Contains(qLower, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(e);
                if (results.Count >= maxResults) return results;
            }
        }

        return results;
    }

    /// <summary>
    /// 根据扩展名判断文件类别（白名单方式）
    /// </summary>
    /// <param name="extension">扩展名（含或不含点均可）</param>
    /// <returns>文件类别</returns>
    public static FileCategory GetCategory(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return FileCategory.Other;
        var ext = extension.ToLowerInvariant();
        if (!ext.StartsWith(".")) ext = "." + ext;
        return ExtensionMap.TryGetValue(ext, out var cat) ? cat : FileCategory.Other;
    }

    /// <summary>扩展名 → 类别映射表（预编译，O(1) 查询）</summary>
    private static readonly Dictionary<string, FileCategory> ExtensionMap = BuildExtensionMap();

    private static Dictionary<string, FileCategory> BuildExtensionMap()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var ext in Documents.Split('|')) map[ext] = FileCategory.Document;
        foreach (var ext in Images.Split('|')) map[ext] = FileCategory.Image;
        foreach (var ext in Videos.Split('|')) map[ext] = FileCategory.Video;
        foreach (var ext in Music.Split('|')) map[ext] = FileCategory.Music;
        foreach (var ext in Applications.Split('|')) map[ext] = FileCategory.Application;
        foreach (var ext in Archives.Split('|')) map[ext] = FileCategory.Archive;
        foreach (var ext in CodeFiles.Split('|')) map[ext] = FileCategory.Code;
        return map;
    }

    // ===== 扩展名白名单 =====

    private const string Documents = ".doc|.docx|.pdf|.txt|.rtf|.odt|.wps|.xls|.xlsx|.ppt|.pptx|.csv|.md|.markdown|.pages|.key|.numbers";
    private const string Images = ".png|.jpg|.jpeg|.gif|.bmp|.svg|.webp|.ico|.tiff|.tif|.heic|.raw|.psd|.ai|.indd";
    private const string Videos = ".mp4|.avi|.mkv|.mov|.wmv|.flv|.webm|.m4v|.mpg|.mpeg|.3gp|.rm|.rmvb";
    private const string Music = ".mp3|.wav|.flac|.aac|.ogg|.wma|.m4a|.alac|.aiff|.opus";
    private const string Applications = ".exe|.msi|.appx|.msix|.bat|.cmd|.ps1|.sh|.app";
    private const string Archives = ".zip|.rar|.7z|.tar|.gz|.bz2|.xz|.iso|.cab|.tgz";
    private const string CodeFiles = ".cs|.js|.ts|.jsx|.tsx|.py|.java|.c|.cpp|.h|.hpp|.go|.rs|.rb|.php|.swift|.kt|.json|.xml|.yaml|.yml|.toml|.ini|.config|.sql|.html|.css|.scss|.less|.vue|.svelte";

    // ===== 拼音首字母 =====

    /// <summary>计算文件名的拼音首字母（小写），用于中文模糊匹配</summary>
    private static string GetPinyinInitials(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(GetPinyinInitial(c));
        }
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>获取单个字符的拼音首字母（汉字返回大写字母，非汉字原样返回）</summary>
    private static char GetPinyinInitial(char c)
    {
        // CJK 统一表意文字范围
        if (c < 0x4E00 || c > 0x9FFF) return c;

        var enc = GetGbkEncoding();
        if (enc == null) return c;

        try
        {
            var bytes = enc.GetBytes(c.ToString());
            if (bytes.Length < 2) return c;
            var code = (bytes[0] << 8) | bytes[1];

            foreach (var (low, high, letter) in GbkPinyinRanges)
            {
                if (code >= low && code <= high) return letter;
            }
        }
        catch
        {
            // 转换失败返回原字符
        }
        return c;
    }

    /// <summary>GBK(936) 编码缓存（懒加载，失败则拼音匹配降级）</summary>
    private static Encoding? _gbkCache;
    private static bool _gbkTried;

    private static Encoding? GetGbkEncoding()
    {
        if (_gbkCache != null) return _gbkCache;
        if (_gbkTried) return null;
        _gbkTried = true;
        try
        {
            _gbkCache = Encoding.GetEncoding(936);
            return _gbkCache;
        }
        catch
        {
            // 尝试反射注册 CodePagesEncodingProvider（若程序集已加载）
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "System.Text.Encoding.CodePages");
                var providerType = asm?.GetType("System.Text.CodePagesEncodingProvider");
                var instance = providerType?.GetProperty("Instance")?.GetValue(null);
                if (instance is EncodingProvider ep)
                {
                    Encoding.RegisterProvider(ep);
                    _gbkCache = Encoding.GetEncoding(936);
                    return _gbkCache;
                }
            }
            catch
            {
                // 注册失败 — 拼音匹配不可用
            }
            return null;
        }
    }

    /// <summary>
    /// GBK 汉字编码区间 → 拼音首字母（按编码升序，覆盖一级汉字）
    /// 注：I/U/V 无对应汉字；二级汉字（部首排序）不在此表，返回原字符
    /// </summary>
    private static readonly (int Low, int High, char Letter)[] GbkPinyinRanges =
    {
        (0xB0A1, 0xB0C4, 'A'),
        (0xB0C5, 0xB2C0, 'B'),
        (0xB2C1, 0xB4ED, 'C'),
        (0xB4EE, 0xB6E9, 'D'),
        (0xB6EA, 0xB7A1, 'E'),
        (0xB7A2, 0xB8C0, 'F'),
        (0xB8C1, 0xB9FD, 'G'),
        (0xB9FE, 0xBBF6, 'H'),
        (0xBBF7, 0xBFA5, 'J'),
        (0xBFA6, 0xC0AB, 'K'),
        (0xC0AC, 0xC2E7, 'L'),
        (0xC2E8, 0xC4C2, 'M'),
        (0xC4C3, 0xC5B5, 'N'),
        (0xC5B6, 0xC5BD, 'O'),
        (0xC5BE, 0xC6D9, 'P'),
        (0xC6DA, 0xC8BA, 'Q'),
        (0xC8BB, 0xC8F5, 'R'),
        (0xC8F6, 0xCBF9, 'S'),
        (0xCBFA, 0xCDD9, 'T'),
        (0xCDDA, 0xCEF3, 'W'),
        (0xCEF4, 0xD1B8, 'X'),
        (0xD1B9, 0xD4D0, 'Y'),
        (0xD4D1, 0xD7F9, 'Z'),
    };

    // ===== 缓存持久化 =====

    /// <summary>从缓存文件加载索引</summary>
    private void TryLoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return;
            var json = File.ReadAllText(CacheFile);
            _entries = JsonSerializer.Deserialize<List<FileEntry>>(json, JsonOpts) ?? new();
        }
        catch
        {
            _entries = new();
        }
    }

    /// <summary>保存索引到缓存文件</summary>
    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var json = JsonSerializer.Serialize(_entries, JsonOpts);
            File.WriteAllText(CacheFile, json);
        }
        catch
        {
            // 缓存写入失败不影响功能
        }
    }
}