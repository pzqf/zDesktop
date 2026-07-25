using System.Diagnostics;
using System.IO;
using System.Text;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Search;

/// <summary>
/// Everything 搜索引擎可选集成 — 通过 Everything CLI (es.exe) 调用，不依赖 SDK DLL
///
/// 职责：
/// - 检测 Everything 是否安装/运行（进程或窗口类名）
/// - 如果可用，通过 es.exe 执行搜索，解析输出（每行一个文件路径）
/// - 如果不可用，IsAvailable=false，调用方降级到 FileIndexService
///
/// 容错：es.exe 可能不在 PATH，检测失败直接 IsAvailable=false
/// </summary>
public sealed class EverythingBridge
{
    /// <summary>Everything 主窗口类名（1.4 版）</summary>
    private const string WindowClass1 = "EVERYTHING_TASKBAR_NOTIFICATION";

    /// <summary>Everything 主窗口类名（1.5 版）</summary>
    private const string WindowClass2 = "EVERYTHING";

    private readonly bool _available;
    private int? _fileCountCache;

    /// <summary>Everything 是否可用（已运行 + es.exe 可调用）</summary>
    public bool IsAvailable => _available;

    /// <summary>
    /// Everything 索引文件数（懒加载）
    /// <para>获取失败或不可用时返回 -1</para>
    /// </summary>
    public int IndexedFileCount
    {
        get
        {
            if (!_available) return -1;
            if (_fileCountCache.HasValue) return _fileCountCache.Value;
            _fileCountCache = QueryFileCount();
            return _fileCountCache.Value;
        }
    }

    /// <summary>构造时检测 Everything 可用性</summary>
    public EverythingBridge()
    {
        _available = DetectEverything();
        Console.WriteLine($"[EverythingBridge] 可用: {_available}");
    }

    /// <summary>检测 Everything 是否运行（进程或窗口）且 es.exe 可调用</summary>
    private static bool DetectEverything()
    {
        try
        {
            // 1. 检测进程
            var byProc = Process.GetProcessesByName("Everything").Length > 0;

            // 2. 检测窗口类名
            var byWin = Win32.FindWindow(WindowClass1, string.Empty) != IntPtr.Zero ||
                        Win32.FindWindow(WindowClass2, string.Empty) != IntPtr.Zero;

            if (!byProc && !byWin) return false;

            // 3. 检测 es.exe 是否可调用
            return IsEsCallable();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>检测 es.exe 是否在 PATH 中可调用</summary>
    private static bool IsEsCallable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "es.exe",
                Arguments = "-help",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(2000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 通过 es.exe 执行搜索
    /// </summary>
    /// <param name="query">搜索词（支持 Everything 语法）</param>
    /// <param name="maxResults">最大结果数</param>
    /// <returns>匹配的文件条目列表；不可用或失败返回空列表</returns>
    public List<FileEntry> Search(string query, int maxResults = 20)
    {
        if (!_available || string.IsNullOrWhiteSpace(query)) return new();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "es.exe",
                Arguments = BuildArguments(query.Trim(), maxResults),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return new();

            var results = new List<FileEntry>(maxResults);
            while (!p.StandardOutput.EndOfStream && results.Count < maxResults)
            {
                var line = p.StandardOutput.ReadLine();
                var entry = PathToFileEntry(line);
                if (entry != null) results.Add(entry);
            }
            try { p.WaitForExit(3000); } catch { }
            return results;
        }
        catch
        {
            return new();
        }
    }

    /// <summary>构造 es.exe 命令行参数</summary>
    private static string BuildArguments(string query, int maxResults)
    {
        // -n 限制结果数；-utf8 强制 UTF-8 输出；查询词用引号包裹
        return $"-n {maxResults} -utf8 \"{EscapeQuery(query)}\"";
    }

    /// <summary>转义查询词中的双引号</summary>
    private static string EscapeQuery(string query)
    {
        return query.Replace("\"", "\"\"");
    }

    /// <summary>查询 Everything 索引文件总数（通过 es.exe -count）</summary>
    /// <returns>索引文件数；获取失败返回 -1</returns>
    private static int QueryFileCount()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "es.exe",
                Arguments = "-count *",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return -1;
            var output = p.StandardOutput.ReadToEnd();
            try { p.WaitForExit(5000); } catch { }
            return int.TryParse(output.Trim(), out var n) ? n : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>将 es.exe 输出的一行路径转为 FileEntry</summary>
    private static FileEntry? PathToFileEntry(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var name = Path.GetFileName(path);
            var ext = Path.GetExtension(path) ?? string.Empty;
            var entry = new FileEntry
            {
                Path = path,
                Name = string.IsNullOrEmpty(name) ? path : name,
                Extension = ext,
                Category = FileIndexService.GetCategory(ext),
            };
            try
            {
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    entry.Size = fi.Length;
                    entry.LastModified = fi.LastWriteTime;
                }
                else if (Directory.Exists(path))
                {
                    var di = new DirectoryInfo(path);
                    entry.LastModified = di.LastWriteTime;
                }
            }
            catch
            {
                // 元信息获取失败不影响展示
            }
            return entry;
        }
        catch
        {
            return null;
        }
    }
}