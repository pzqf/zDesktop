using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Desktop;

/// <summary>
/// 系统状态还原账本（设计案 v3.1 §二 原则 4、§五 <c>restore.json</c>）
///
/// **为什么需要它**：<c>taskkill /F</c> 触发的是 TerminateProcess，进程内任何钩子
/// （SetConsoleCtrlHandler / AppDomain.UnhandledException / Dispatcher）都不会执行。
/// 因此「改了什么」必须在**动手之前**落盘，而不是指望退出时的代码跑到。
///
/// 有了账本，三条还原路径共用同一份数据：
/// 1. 下次启动检测到异常退出 → <see cref="RestoreAll"/>
/// 2. 卸载程序调用 <c>zDesktop.App.exe --restore</c>
/// 3. 正常退出 / 崩溃回调
///
/// 账本只记录**我们主动改过**的系统状态，未改过的项不写，避免还原时误伤。
/// </summary>
public sealed class RestoreJournal
{
    private static readonly string DefaultDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _dir;
    private readonly string _journalPath;

    /// <param name="directory">存储目录；null 表示使用 <c>%APPDATA%\zDesktop</c></param>
    private RestoreJournal(string? directory)
    {
        _dir = directory ?? DefaultDir;
        _journalPath = Path.Combine(_dir, "restore.json");
    }

    /// <summary>账本内容</summary>
    public sealed class Entry
    {
        /// <summary>是否隐藏过原生桌面图标层（自渲染实验模式）</summary>
        public bool NativeIconsHidden { get; set; }

        /// <summary>安装/首次运行时的壁纸路径（分区背景合成会改壁纸，M3 起使用）</summary>
        public string? OriginalWallpaperPath { get; set; }

        /// <summary>原壁纸样式（注册表 WallpaperStyle）</summary>
        public string? OriginalWallpaperStyle { get; set; }

        /// <summary>
        /// 桌面「自动排列图标」的原始开关值（M3 分区功能会关闭它，见设计案 §4.2 决策 2）。
        /// null 表示我们从未改过。
        /// </summary>
        public bool? OriginalAutoArrange { get; set; }

        /// <summary>账本最后更新时间</summary>
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    private Entry _entry = new();

    /// <summary>当前账本内容（只读用途）</summary>
    public Entry Current => _entry;

    /// <summary>从磁盘加载账本；不存在或损坏时返回空账本</summary>
    /// <param name="directory">存储目录；null 表示使用 <c>%APPDATA%\zDesktop</c></param>
    public static RestoreJournal Load(string? directory = null)
    {
        var journal = new RestoreJournal(directory);

        try
        {
            if (File.Exists(journal._journalPath))
            {
                var json = File.ReadAllText(journal._journalPath);
                var loaded = JsonSerializer.Deserialize<Entry>(json, JsonOptions);
                if (loaded != null) journal._entry = loaded;
            }
        }
        catch (Exception ex)
        {
            // 损坏时按空账本处理：宁可少还原，也不要因解析失败而阻断启动
            Console.WriteLine($"[RestoreJournal] 读取失败，按空账本处理: {ex.Message}");
        }

        return journal;
    }

    /// <summary>
    /// 立即落盘。**必须在真正修改系统状态之前调用**，否则强杀会让账本与现实不符。
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            _entry.UpdatedAt = DateTime.Now;
            File.WriteAllText(_journalPath, JsonSerializer.Serialize(_entry, JsonOptions));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RestoreJournal] 写入失败: {ex.Message}");
        }
    }

    /// <summary>记录「即将隐藏原生图标层」，先落盘再动手</summary>
    public void MarkNativeIconsHidden()
    {
        _entry.NativeIconsHidden = true;
        Save();
    }

    /// <summary>记录「原生图标层已还原」</summary>
    public void ClearNativeIconsHidden()
    {
        if (!_entry.NativeIconsHidden) return;
        _entry.NativeIconsHidden = false;
        Save();
    }

    /// <summary>是否存在任何待还原项</summary>
    public bool HasPendingRestore()
        => _entry.NativeIconsHidden
        || _entry.OriginalWallpaperPath != null
        || _entry.OriginalAutoArrange != null;

    /// <summary>
    /// 还原账本记录的全部系统状态，成功后清空账本。
    ///
    /// 每项独立 try/catch —— 一项失败不得阻断其余项，
    /// 这是崩溃/卸载路径，必须尽最大努力还原。
    /// </summary>
    public void RestoreAll()
    {
        if (!HasPendingRestore())
        {
            Console.WriteLine("[RestoreJournal] 无待还原项");
            return;
        }

        Console.WriteLine("[RestoreJournal] 开始还原系统状态…");

        if (_entry.NativeIconsHidden)
        {
            try
            {
                DesktopRestore.RestoreNativeDesktopIcons();
                _entry.NativeIconsHidden = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RestoreJournal] 还原图标层失败: {ex.Message}");
            }
        }

        if (_entry.OriginalWallpaperPath != null)
        {
            try
            {
                RestoreWallpaper(_entry.OriginalWallpaperPath, _entry.OriginalWallpaperStyle);
                _entry.OriginalWallpaperPath = null;
                _entry.OriginalWallpaperStyle = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RestoreJournal] 还原壁纸失败: {ex.Message}");
            }
        }

        if (_entry.OriginalAutoArrange != null)
        {
            try
            {
                RestoreAutoArrange(_entry.OriginalAutoArrange.Value);
                _entry.OriginalAutoArrange = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RestoreJournal] 还原自动排列失败: {ex.Message}");
            }
        }

        Save();
        Console.WriteLine("[RestoreJournal] 还原完成");
    }

    /// <summary>还原壁纸（M3 分区背景合成启用后才会有记录）</summary>
    private static void RestoreWallpaper(string path, string? style)
    {
        const int SPI_SETDESKWALLPAPER = 0x0014;
        const int SPIF_UPDATEINIFILE = 0x01;
        const int SPIF_SENDCHANGE = 0x02;

        if (style != null)
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
            key?.SetValue("WallpaperStyle", style);
        }

        Win32.SystemParametersInfoString(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        Console.WriteLine($"[RestoreJournal] 已还原壁纸: {path}");
    }

    /// <summary>还原桌面「自动排列图标」开关（M3 使用）</summary>
    private static void RestoreAutoArrange(bool original)
    {
        // 该标志位于 Shell 的 Bags 中，具体键路径随 Windows 版本变化，
        // M3 实现分区功能时一并落地。此处先留还原入口，避免账本结构后续再改。
        Console.WriteLine($"[RestoreJournal] 自动排列原值 {original} 的还原将在 M3 实现");
    }
}
