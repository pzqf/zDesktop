using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Wallpaper;

/// <summary>
/// 壁纸服务 — 设置桌面壁纸 + 下载必应每日壁纸 + 本地壁纸轮播
///
/// 功能：
/// 1. SetWallpaper — 通过 Win32 SystemParametersInfo 设置壁纸
/// 2. DownloadBingWallpaper — 下载必应每日壁纸
/// 3. GetLocalWallpapers — 扫描本地壁纸文件夹
/// </summary>
public sealed class WallpaperService : IDisposable
{
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");

    private static readonly string BingCacheDir = Path.Combine(AppDataDir, "bing-wallpapers");

    private readonly HttpClient _http;

    // 壁纸样式
    public const int WallpaperStyleStretch = 2;  // 拉伸
    public const int WallpaperStyleFill = 10;     // 填充（保持比例裁剪）
    public const int WallpaperStyleFit = 6;       // 适应
    public const int WallpaperStyleCenter = 0;    // 居中
    public const int WallpaperStyleSpan = 22;     // 跨屏

    public WallpaperService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        Directory.CreateDirectory(BingCacheDir);
    }

    /// <summary>设置桌面壁纸</summary>
    /// <param name="imagePath">图片文件路径</param>
    /// <param name="style">壁纸样式（默认填充）</param>
    public bool SetWallpaper(string imagePath, int style = WallpaperStyleFill)
    {
        if (!File.Exists(imagePath)) return false;

        try
        {
            // 设置壁纸样式（注册表）
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Control Panel\Desktop", true);
            if (key != null)
            {
                key.SetValue("WallpaperStyle", style.ToString());
                key.SetValue("TileWallpaper", "0");
            }

            // 调用 SystemParametersInfo 设置壁纸
            var result = Win32.SystemParametersInfoString(
                SPI_SETDESKWALLPAPER, 0, imagePath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

            Console.WriteLine($"[Wallpaper] 设置壁纸: {imagePath} (result={result})");
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Wallpaper] 设置失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 下载必应每日壁纸
    /// API: https://bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=zh-CN
    /// </summary>
    /// <returns>下载的壁纸本地路径，失败返回 null</returns>
    public async Task<string?> DownloadBingWallpaperAsync()
    {
        try
        {
            const string apiUrl = "https://bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=zh-CN";
            var json = await _http.GetStringAsync(apiUrl);
            var resp = JsonSerializer.Deserialize<BingImageResponse>(json);
            var image = resp?.Images?.FirstOrDefault();
            if (image == null) return null;

            // 检查是否已下载（按日期命名）
            var date = image.StartDate ?? DateTime.Now.ToString("yyyyMMdd");
            var localPath = Path.Combine(BingCacheDir, $"bing_{date}.jpg");
            if (File.Exists(localPath))
            {
                Console.WriteLine($"[Wallpaper] 必应壁纸已存在: {date}");
                return localPath;
            }

            // 下载图片（使用 1920x1080 分辨率）
            var imageUrl = $"https://bing.com{image.UrlBase}_1920x1080.jpg";
            var imageData = await _http.GetByteArrayAsync(imageUrl);
            await File.WriteAllBytesAsync(localPath, imageData);

            Console.WriteLine($"[Wallpaper] 已下载必应壁纸: {date} ({imageData.Length / 1024}KB)");
            return localPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Wallpaper] 必应壁纸下载失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>获取本地缓存的所有必应壁纸</summary>
    public List<string> GetBingWallpapers()
    {
        if (!Directory.Exists(BingCacheDir)) return new();
        return Directory.GetFiles(BingCacheDir, "*.jpg")
            .OrderByDescending(f => f)
            .ToList();
    }

    /// <summary>获取本地壁纸文件夹中的所有图片</summary>
    public List<string> GetLocalWallpapers(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return new();

        var extensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
        return Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f)
            .ToList();
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    // ===== Win32 SPI 常量 =====
    private const int SPI_SETDESKWALLPAPER = 0x0014;
    private const int SPIF_UPDATEINIFILE = 0x01;
    private const int SPIF_SENDCHANGE = 0x02;
}

/// <summary>必应壁纸 API 响应</summary>
public sealed class BingImageResponse
{
    [JsonPropertyName("images")]
    public List<BingImage>? Images { get; set; }
}

public sealed class BingImage
{
    [JsonPropertyName("startdate")]
    public string? StartDate { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("urlbase")]
    public string UrlBase { get; set; } = string.Empty;

    [JsonPropertyName("copyright")]
    public string Copyright { get; set; } = string.Empty;
}
