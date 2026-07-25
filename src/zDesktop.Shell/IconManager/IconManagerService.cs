using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using zDesktop.Shell.DesktopIcons;

// 本文件同时引用 System.Drawing.Imaging 与 System.Windows.Media，两者均含 PixelFormat，
// 这里显式别名到 GDI+ 的 PixelFormat（用于 Bitmap 像素格式）。
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace zDesktop.Shell.IconManager;

// ============================================================
//  枚举
// ============================================================

/// <summary>图标配色模式</summary>
public enum ColorizeMode
{
    /// <summary>关闭着色，使用原始图标</summary>
    Off,

    /// <summary>单色着色 — 将图标色调映射到指定颜色</summary>
    SingleColor,

    /// <summary>匹配壁纸 — 自动从当前壁纸提取主色并着色</summary>
    MatchWallpaper,
}

/// <summary>着色作用范围</summary>
public enum ColorizeScope
{
    /// <summary>所有桌面图标</summary>
    All,

    /// <summary>仅文件夹</summary>
    Folders,

    /// <summary>仅应用快捷方式</summary>
    Apps,

    /// <summary>仅系统项（公共桌面 / 非用户快捷方式）</summary>
    System,
}

// ============================================================
//  数据模型
// ============================================================

/// <summary>
/// 图标包元数据 — 描述一个可应用的图标主题包
/// </summary>
public sealed class IconPack
{
    /// <summary>图标包名称（显示用）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>图标包根目录路径（内置未下载包为空）</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>描述说明</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>预览图文件路径（无则 null，由 UI 渲染占位）</summary>
    public string? Preview { get; set; }

    /// <summary>是否为内置包（仅元数据，可能未下载）</summary>
    public bool IsBuiltin { get; set; }

    /// <summary>是否需要下载（内置包通常为 true，已安装包为 false）</summary>
    public bool NeedsDownload { get; set; }
}

/// <summary>
/// 图标配色配置 — 持久化到 icon-colorize.json
/// </summary>
public sealed class ColorizeConfig
{
    /// <summary>着色模式</summary>
    public ColorizeMode Mode { get; set; } = ColorizeMode.Off;

    /// <summary>目标颜色（十六进制 #RRGGBB，用户数据，UI 渲染时转 Brush）</summary>
    public string TargetColor { get; set; } = "#6C5CE7";

    /// <summary>着色强度 0-100（0=无变化，100=完全着色）</summary>
    public int Strength { get; set; } = 60;

    /// <summary>着色作用范围</summary>
    public ColorizeScope Scope { get; set; } = ColorizeScope.All;

    /// <summary>从壁纸提取的主色（MatchWallpaper 模式下自动填充，#RRGGBB）</summary>
    public string? WallpaperColor { get; set; }

    /// <summary>最后应用时间</summary>
    public DateTime AppliedAt { get; set; } = DateTime.Now;
}

/// <summary>桌面快捷方式条目 — 用于图标替换列表</summary>
public sealed record DesktopShortcutEntry(string SourcePath, string DisplayName, ImageSource? Icon);

/// <summary>预览样例图标 — 含原始 ImageSource（Before）</summary>
public sealed record SampleIcon(string SourcePath, ImageSource? Original);

// ============================================================
//  服务
// ============================================================

/// <summary>
/// 图标管理服务 — 图标包管理 / 图标配色（着色）/ 图标替换
///
/// 职责：
/// 1. 图标包：内置包列表（Fluent/Papirus/Numix/Tela/WhiteSur 元数据）、
///    扫描已安装包（%APPDATA%\zDesktop\icon-packs\）、导入 ZIP 包。
/// 2. 图标配色：持久化配置、从壁纸提取主色、按 Strength 生成着色缓存图标
///    （缓存到 %APPDATA%\zDesktop\icon-cache\），不直接修改系统图标。
/// 3. 图标替换：通过 WScript.Shell COM 修改 .lnk 的 IconLocation，可恢复默认。
/// 4. Before/After 预览与配置导入导出。
///
/// 所有 IO / System.Drawing / COM 调用均 try-catch 容错，失败不影响其他功能。
/// </summary>
public sealed class IconManagerService
{
    // ===== 路径常量 =====

    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");

    /// <summary>图标包安装目录：%APPDATA%\zDesktop\icon-packs\</summary>
    private static readonly string PacksDir = Path.Combine(AppDataDir, "icon-packs");

    /// <summary>着色图标缓存目录：%APPDATA%\zDesktop\icon-cache\</summary>
    private static readonly string CacheDir = Path.Combine(AppDataDir, "icon-cache");

    /// <summary>配色配置文件：%APPDATA%\zDesktop\icon-colorize.json</summary>
    private static readonly string ColorizeConfigPath = Path.Combine(AppDataDir, "icon-colorize.json");

    /// <summary>JSON 序列化选项 — 驼峰命名 + 缩进 + 枚举转字符串（便于人工阅读配置）</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>桌面图标扫描器（用于样例预览与替换列表）</summary>
    private readonly DesktopIconStore _iconStore = new();

    /// <summary>内置图标包定义（元数据，实际图标文件可能不存在）</summary>
    private static readonly IconPack[] BuiltinPacks =
    {
        new() { Name = "Fluent", Description = "微软 Fluent 设计系统图标，Windows 11 原生风格", IsBuiltin = true, NeedsDownload = true },
        new() { Name = "Papirus", Description = "开源 Papirus 图标主题，简洁扁平", IsBuiltin = true, NeedsDownload = true },
        new() { Name = "Numix", Description = "Numix 圆形图标主题，经典 Linux 桌面风格", IsBuiltin = true, NeedsDownload = true },
        new() { Name = "Tela", Description = "Tela 现代扁平图标主题，色彩明快", IsBuiltin = true, NeedsDownload = true },
        new() { Name = "WhiteSur", Description = "WhiteSur 仿 macOS Big Sur 风格图标", IsBuiltin = true, NeedsDownload = true },
    };

    /// <summary>构造函数 — 确保工作目录存在</summary>
    public IconManagerService()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            Directory.CreateDirectory(PacksDir);
            Directory.CreateDirectory(CacheDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 初始化目录失败: {ex.Message}");
        }
    }

    // ============================================================
    //  图标包管理
    // ============================================================

    /// <summary>获取内置图标包列表（仅元数据，需下载）</summary>
    public IReadOnlyList<IconPack> GetBuiltinPacks() => BuiltinPacks;

    /// <summary>
    /// 扫描已安装的图标包 — 遍历 icon-packs 目录，每个子目录视为一个包
    /// </summary>
    /// <returns>已安装图标包列表（失败返回空列表）</returns>
    public List<IconPack> GetInstalledPacks()
    {
        var result = new List<IconPack>();
        try
        {
            if (!Directory.Exists(PacksDir)) return result;

            foreach (var dir in Directory.EnumerateDirectories(PacksDir))
            {
                var name = System.IO.Path.GetFileName(dir);
                // 查找预览图（preview.png / preview.jpg / icon.png）
                var preview = FindPreviewImage(dir);
                result.Add(new IconPack
                {
                    Name = name,
                    Path = dir,
                    Description = "用户导入的图标包",
                    Preview = preview,
                    IsBuiltin = false,
                    NeedsDownload = false,
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 扫描已安装图标包失败: {ex.Message}");
        }
        return result;
    }

    /// <summary>在图标包目录中查找预览图</summary>
    private static string? FindPreviewImage(string packDir)
    {
        var candidates = new[] { "preview.png", "preview.jpg", "preview.jpeg", "icon.png", "preview.bmp" };
        foreach (var c in candidates)
        {
            var p = System.IO.Path.Combine(packDir, c);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// 导入图标包 — 解压 ZIP 到 icon-packs 目录
    /// ZIP 根目录名作为包名；若已存在同名包则覆盖
    /// </summary>
    /// <param name="zipPath">ZIP 文件路径</param>
    /// <returns>导入成功后的图标包根目录路径，失败返回 null</returns>
    public string? ImportPack(string zipPath)
    {
        try
        {
            if (!File.Exists(zipPath))
            {
                Console.WriteLine($"[IconManager] ZIP 不存在: {zipPath}");
                return null;
            }

            var packName = System.IO.Path.GetFileNameWithoutExtension(zipPath);
            var destDir = System.IO.Path.Combine(PacksDir, packName);

            // 已存在则先删除（覆盖导入）
            if (Directory.Exists(destDir))
            {
                Directory.Delete(destDir, recursive: true);
            }
            Directory.CreateDirectory(destDir);

            ZipFile.ExtractToDirectory(zipPath, destDir, overwriteFiles: true);
            Console.WriteLine($"[IconManager] 已导入图标包: {packName} → {destDir}");
            return destDir;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 导入图标包失败: {ex.Message}");
            return null;
        }
    }

    // ============================================================
    //  配色配置持久化
    // ============================================================

    /// <summary>加载配色配置，文件不存在或解析失败返回默认配置</summary>
    public ColorizeConfig LoadColorizeConfig()
    {
        try
        {
            if (!File.Exists(ColorizeConfigPath)) return new ColorizeConfig();
            var json = File.ReadAllText(ColorizeConfigPath);
            return JsonSerializer.Deserialize<ColorizeConfig>(json, JsonOptions) ?? new ColorizeConfig();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 加载配色配置失败: {ex.Message}");
            return new ColorizeConfig();
        }
    }

    /// <summary>保存配色配置到 icon-colorize.json</summary>
    public void SaveColorizeConfig(ColorizeConfig config)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            config.AppliedAt = DateTime.Now;
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ColorizeConfigPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 保存配色配置失败: {ex.Message}");
        }
    }

    /// <summary>导出配色配置到指定路径</summary>
    public bool ExportColorizeConfig(string filePath)
    {
        try
        {
            var config = LoadColorizeConfig();
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(filePath, json);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 导出配色配置失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>从指定路径导入配色配置（并持久化）</summary>
    public ColorizeConfig? ImportColorizeConfig(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            var json = File.ReadAllText(filePath);
            var config = JsonSerializer.Deserialize<ColorizeConfig>(json, JsonOptions);
            if (config != null) SaveColorizeConfig(config);
            return config;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 导入配色配置失败: {ex.Message}");
            return null;
        }
    }

    // ============================================================
    //  壁纸主色提取
    // ============================================================

    private const int SPI_GETDESKWALLPAPER = 0x0073;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfoStringGet(int uiAction, int uiParam, StringBuilder pvParam, int fWinIni);

    /// <summary>
    /// 获取当前桌面壁纸文件路径
    /// 优先 SystemParametersInfo，失败回退到注册表
    /// </summary>
    public string? GetCurrentWallpaperPath()
    {
        try
        {
            var sb = new StringBuilder(260);
            if (SystemParametersInfoStringGet(SPI_GETDESKWALLPAPER, sb.Capacity, sb, 0) &&
                !string.IsNullOrWhiteSpace(sb.ToString()))
            {
                return sb.ToString();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] SPI 获取壁纸路径失败: {ex.Message}");
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var wp = key?.GetValue("WallPaper") as string;
            return string.IsNullOrWhiteSpace(wp) ? null : wp;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 注册表获取壁纸路径失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从当前壁纸提取主色 — 缩放采样后取加权平均色（跳过极暗/极亮/接近透明的像素）
    /// </summary>
    /// <returns>主色十六进制 #RRGGBB，失败返回 null</returns>
    public string? ExtractWallpaperColor()
    {
        try
        {
            var path = GetCurrentWallpaperPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Console.WriteLine("[IconManager] 未找到壁纸文件");
                return null;
            }

            using var src = new Bitmap(path);
            // 缩放到 32x32 加速采样，忽略宽高比（仅取色）
            using var small = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            using (var gfx = Graphics.FromImage(small))
            {
                gfx.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                gfx.DrawImage(src, 0, 0, 32, 32);
            }

            long rSum = 0, gSum = 0, bSum = 0, count = 0;

            for (var y = 0; y < small.Height; y++)
            for (var x = 0; x < small.Width; x++)
            {
                var c = small.GetPixel(x, y);
                // 跳过接近透明
                if (c.A < 32) continue;
                // 转 HSV 过滤极暗/极亮（避免主色偏向黑白）
                RgbToHsv(c.R, c.G, c.B, out _, out var s, out var v);
                if (v < 0.12 || v > 0.96 || s < 0.08) continue;

                rSum += c.R;
                gSum += c.G;
                bSum += c.B;
                count++;
            }

            // 全部被过滤则回退到纯平均
            if (count == 0)
            {
                for (var y = 0; y < small.Height; y++)
                for (var x = 0; x < small.Width; x++)
                {
                    var c = small.GetPixel(x, y);
                    if (c.A < 32) continue;
                    rSum += c.R; gSum += c.G; bSum += c.B; count++;
                }
            }

            if (count == 0) return null;

            var r = (int)(rSum / count);
            var g = (int)(gSum / count);
            var b = (int)(bSum / count);
            return $"#{r:X2}{g:X2}{b:X2}";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 提取壁纸主色失败: {ex.Message}");
            return null;
        }
    }

    // ============================================================
    //  着色算法
    // ============================================================

    /// <summary>
    /// 对图标 Bitmap 执行色调映射 — 保留亮度通道(V)，调整色相到目标色，按 Strength 混合
    /// </summary>
    /// <param name="source">原图标 Bitmap</param>
    /// <param name="targetColor">目标色（#RRGGBB）</param>
    /// <param name="strength">强度 0-100</param>
    /// <returns>着色后的新 Bitmap（原图不变）</returns>
    private static Bitmap ColorizeBitmap(Bitmap source, System.Drawing.Color targetColor, int strength)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var s = Math.Clamp(strength, 0, 100) / 100.0;

        RgbToHsv(targetColor.R, targetColor.G, targetColor.B, out var targetH, out var targetSat, out _);

        var rect = new Rectangle(0, 0, source.Width, source.Height);
        var srcData = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dstData = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        var bytes = Math.Abs(srcData.Stride) * source.Height;
        var srcBuffer = new byte[bytes];
        var dstBuffer = new byte[bytes];

        Marshal.Copy(srcData.Scan0, srcBuffer, 0, bytes);

        for (var i = 0; i < bytes; i += 4)
        {
            var a = srcBuffer[i + 3];
            if (a == 0)
            {
                // 完全透明像素直接拷贝
                dstBuffer[i] = srcBuffer[i];
                dstBuffer[i + 1] = srcBuffer[i + 1];
                dstBuffer[i + 2] = srcBuffer[i + 2];
                dstBuffer[i + 3] = a;
                continue;
            }

            var r = srcBuffer[i + 2]; // BGRA
            var g = srcBuffer[i + 1];
            var b = srcBuffer[i + 0];

            RgbToHsv(r, g, b, out _, out var origSat, out var v);

            // 调整色相到目标，饱和度取目标（强度越大越接近目标色），保留亮度
            var nr = r;
            var ng = g;
            var nb = b;
            if (s > 0.001)
            {
                HsvToRgb(targetH, targetSat, v, out var tr, out var tg, out var tb);
                // 按强度混合
                nr = (byte)Math.Round(r * (1 - s) + tr * s);
                ng = (byte)Math.Round(g * (1 - s) + tg * s);
                nb = (byte)Math.Round(b * (1 - s) + tb * s);
            }

            dstBuffer[i + 0] = nb;
            dstBuffer[i + 1] = ng;
            dstBuffer[i + 2] = nr;
            dstBuffer[i + 3] = a;
        }

        Marshal.Copy(dstBuffer, 0, dstData.Scan0, bytes);
        source.UnlockBits(srcData);
        result.UnlockBits(dstData);
        return result;
    }

    /// <summary>RGB → HSV（H:0-360, S:0-1, V:0-1）</summary>
    private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        var rf = r / 255.0;
        var gf = g / 255.0;
        var bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;

        v = max;
        s = max <= 0 ? 0 : delta / max;

        if (delta <= 0.0001)
        {
            h = 0;
        }
        else if (max == rf)
        {
            h = 60.0 * (((gf - bf) / delta) % 6);
        }
        else if (max == gf)
        {
            h = 60.0 * (((bf - rf) / delta) + 2);
        }
        else
        {
            h = 60.0 * (((rf - gf) / delta) + 4);
        }
        if (h < 0) h += 360;
    }

    /// <summary>HSV → RGB</summary>
    private static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
    {
        var c = v * s;
        var hp = h / 60.0;
        var x = c * (1 - Math.Abs(hp % 2 - 1));
        double rf, gf, bf;
        switch (hp)
        {
            case < 1: rf = c; gf = x; bf = 0; break;
            case < 2: rf = x; gf = c; bf = 0; break;
            case < 3: rf = 0; gf = c; bf = x; break;
            case < 4: rf = 0; gf = x; bf = c; break;
            case < 5: rf = x; gf = 0; bf = c; break;
            default: rf = c; gf = 0; bf = x; break;
        }
        var m = v - c;
        r = (byte)Math.Round((rf + m) * 255);
        g = (byte)Math.Round((gf + m) * 255);
        b = (byte)Math.Round((bf + m) * 255);
    }

    /// <summary>十六进制 #RRGGBB → System.Drawing.Color，解析失败回退到品牌紫</summary>
    private static System.Drawing.Color HexToColor(string? hex)
    {
        try
        {
            if (string.IsNullOrEmpty(hex)) return System.Drawing.Color.FromArgb(0x6c, 0x5c, 0xe7);
            var h = hex.TrimStart('#');
            if (h.Length == 6)
                return System.Drawing.Color.FromArgb(
                    Convert.ToByte(h.Substring(0, 2), 16),
                    Convert.ToByte(h.Substring(2, 2), 16),
                    Convert.ToByte(h.Substring(4, 2), 16));
            if (h.Length == 8)
                return System.Drawing.Color.FromArgb(
                    Convert.ToByte(h.Substring(0, 2), 16),
                    Convert.ToByte(h.Substring(2, 2), 16),
                    Convert.ToByte(h.Substring(4, 2), 16),
                    Convert.ToByte(h.Substring(6, 2), 16));
        }
        catch { }
        return System.Drawing.Color.FromArgb(0x6c, 0x5c, 0xe7);
    }

    // ============================================================
    //  图标提取与着色缓存
    // ============================================================

    /// <summary>
    /// 从文件/文件夹路径提取图标为 System.Drawing.Bitmap
    /// 优先 IShellItemImageFactory 高清图标（HBITMAP → Bitmap），失败回退 ExtractAssociatedIcon
    /// </summary>
    private static Bitmap? ExtractIconBitmap(string path)
    {
        // 回退方案：ExtractAssociatedIcon → ToBitmap（32x32，对所有路径均可用）
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var bmp = icon.ToBitmap();
            return bmp;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 提取图标 Bitmap 失败 {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>把源路径哈希为短文件名（避免非法字符，用作缓存键）</summary>
    private static string HashPath(string sourcePath)
    {
        var hash = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
        var dir = System.IO.Path.GetDirectoryName(sourcePath) ?? string.Empty;
        // 用路径长度的稳定哈希避免同名冲突
        unchecked
        {
            var h = 2166136261u;
            foreach (var ch in dir)
            {
                h = (h ^ ch) * 16777619u;
            }
            hash += "_" + (h & 0xFFFFFF).ToString("X6");
        }
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            hash = hash.Replace(c, '_');
        return hash;
    }

    /// <summary>
    /// 对单个图标着色并写入缓存目录
    /// </summary>
    /// <param name="sourcePath">图标源路径（文件/文件夹/.lnk）</param>
    /// <param name="config">配色配置</param>
    /// <returns>缓存 PNG 路径，失败返回 null</returns>
    public string? ColorizeIconToCache(string sourcePath, ColorizeConfig config)
    {
        try
        {
            // Off 模式不产生缓存
            if (config.Mode == ColorizeMode.Off) return null;

            Directory.CreateDirectory(CacheDir);

            using var src = ExtractIconBitmap(sourcePath);
            if (src == null) return null;

            var target = ResolveTargetColor(config);
            var colorized = ColorizeBitmap(src, HexToColor(target), config.Strength);

            var cacheFile = System.IO.Path.Combine(CacheDir, HashPath(sourcePath) + ".png");
            colorized.Save(cacheFile, ImageFormat.Png);
            colorized.Dispose();
            return cacheFile;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 着色缓存失败 {sourcePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>获取已缓存的着色图标路径（不存在返回 null）</summary>
    public string? GetColorizedPath(string sourcePath)
    {
        var p = System.IO.Path.Combine(CacheDir, HashPath(sourcePath) + ".png");
        return File.Exists(p) ? p : null;
    }

    /// <summary>清空着色缓存目录</summary>
    public void ClearCache()
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return;
            foreach (var f in Directory.EnumerateFiles(CacheDir))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 清空缓存失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 解析当前应使用的目标色 — MatchWallpaper 模式取壁纸主色，否则取 TargetColor
    /// </summary>
    private string ResolveTargetColor(ColorizeConfig config)
    {
        if (config.Mode == ColorizeMode.MatchWallpaper)
        {
            if (string.IsNullOrEmpty(config.WallpaperColor))
            {
                var extracted = ExtractWallpaperColor();
                config.WallpaperColor = extracted ?? config.TargetColor;
            }
            return config.WallpaperColor!;
        }
        return config.TargetColor;
    }

    /// <summary>
    /// 应用着色 — 扫描桌面图标，按 Scope 过滤后生成着色缓存
    /// </summary>
    /// <param name="config">配色配置（会被持久化）</param>
    /// <returns>成功着色的图标数量</returns>
    public int ApplyColorize(ColorizeConfig config)
    {
        try
        {
            // Off 模式 — 清空缓存即可（UI 回退到原图标）
            if (config.Mode == ColorizeMode.Off)
            {
                ClearCache();
                SaveColorizeConfig(config);
                return 0;
            }

            // MatchWallpaper 模式重新提取壁纸色
            if (config.Mode == ColorizeMode.MatchWallpaper)
            {
                config.WallpaperColor = ExtractWallpaperColor() ?? config.TargetColor;
            }

            var scanned = _iconStore.Scan();
            var count = 0;
            foreach (var (info, _) in scanned)
            {
                if (!ShouldColorize(info, config.Scope)) continue;
                if (ColorizeIconToCache(info.SourcePath, config) != null) count++;
            }

            SaveColorizeConfig(config);
            Console.WriteLine($"[IconManager] 着色完成：{count} 个图标，模式={config.Mode}");
            return count;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 应用着色失败: {ex.Message}");
            return 0;
        }
    }

    /// <summary>判断图标是否属于指定范围</summary>
    private static bool ShouldColorize(zDesktop.Core.DesktopIcons.DesktopIconInfo info, ColorizeScope scope)
    {
        return scope switch
        {
            ColorizeScope.All => true,
            ColorizeScope.Folders => info.IsDirectory,
            ColorizeScope.Apps => info.IsShortcut && !info.IsDirectory,
            ColorizeScope.System => info.IsCommon && !info.IsShortcut,
            _ => true,
        };
    }

    // ============================================================
    //  预览与样例
    // ============================================================

    /// <summary>
    /// 获取若干样例桌面图标（用于 Before/After 预览）— 含原始 ImageSource
    /// </summary>
    /// <param name="count">需要的样例数量</param>
    public List<SampleIcon> GetSampleIconsForPreview(int count)
    {
        var result = new List<SampleIcon>();
        try
        {
            var scanned = _iconStore.Scan();
            // 优先取快捷方式，其次文件夹
            var ordered = scanned
                .OrderByDescending(s => s.Info.IsShortcut)
                .ThenBy(s => s.Info.DisplayName)
                .Take(count);
            foreach (var (info, icon) in ordered)
            {
                result.Add(new SampleIcon(info.SourcePath, icon));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 获取样例图标失败: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// 生成单个图标的 Before/After 对比图文件
    /// </summary>
    /// <param name="iconPath">图标源路径</param>
    /// <param name="config">配色配置</param>
    /// <returns>(before 路径, after 路径)；任一失败对应项为 null</returns>
    public (string? Before, string? After) GeneratePreview(string iconPath, ColorizeConfig config)
    {
        string? before = null;
        string? after = null;
        try
        {
            Directory.CreateDirectory(CacheDir);

            using var bmp = ExtractIconBitmap(iconPath);
            if (bmp != null)
            {
                before = System.IO.Path.Combine(CacheDir, "preview_before.png");
                bmp.Save(before, ImageFormat.Png);

                if (config.Mode != ColorizeMode.Off)
                {
                    using var colorized = ColorizeBitmap(bmp, HexToColor(ResolveTargetColor(config)), config.Strength);
                    after = System.IO.Path.Combine(CacheDir, "preview_after.png");
                    colorized.Save(after, ImageFormat.Png);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 生成预览失败: {ex.Message}");
        }
        return (before, after);
    }

    /// <summary>
    /// 把文件路径加载为 WPF ImageSource（冻结，跨线程安全）
    /// </summary>
    public static ImageSource? LoadImageSource(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 加载图像失败 {path}: {ex.Message}");
            return null;
        }
    }

    // ============================================================
    //  图标替换（.lnk 快捷方式）
    // ============================================================

    /// <summary>获取桌面快捷方式列表（含图标），用于图标替换区</summary>
    public List<DesktopShortcutEntry> GetDesktopShortcuts()
    {
        var result = new List<DesktopShortcutEntry>();
        try
        {
            var scanned = _iconStore.Scan();
            foreach (var (info, icon) in scanned)
            {
                result.Add(new DesktopShortcutEntry(info.SourcePath, info.DisplayName, icon));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 获取桌面快捷方式失败: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// 修改 .lnk 快捷方式的图标 — 通过 WScript.Shell COM 设置 IconLocation
    /// </summary>
    /// <param name="shortcutPath">.lnk 文件路径</param>
    /// <param name="iconPath">图标文件路径（.ico/.exe/.dll）</param>
    /// <returns>是否成功</returns>
    public bool ReplaceIcon(string shortcutPath, string iconPath)
    {
        try
        {
            if (!File.Exists(shortcutPath) || !File.Exists(iconPath))
            {
                Console.WriteLine("[IconManager] 快捷方式或图标文件不存在");
                return false;
            }

            var location = BuildIconLocation(iconPath);
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                Console.WriteLine("[IconManager] 未找到 WScript.Shell COM 组件");
                return false;
            }

            var shell = Activator.CreateInstance(shellType);
            var shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
            if (shortcut == null) return false;

            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember(
                "IconLocation",
                BindingFlags.SetProperty, null, shortcut, new object[] { location });
            shortcutType.InvokeMember(
                "Save",
                BindingFlags.InvokeMethod, null, shortcut, null);

            Console.WriteLine($"[IconManager] 已替换图标: {shortcutPath} → {location}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 替换图标失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 恢复快捷方式默认图标 — 读取 TargetPath，设置 IconLocation 为目标路径索引 0
    /// </summary>
    /// <param name="shortcutPath">.lnk 文件路径</param>
    /// <returns>是否成功</returns>
    public bool ResetIcon(string shortcutPath)
    {
        try
        {
            if (!File.Exists(shortcutPath)) return false;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;

            var shell = Activator.CreateInstance(shellType);
            var shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
            if (shortcut == null) return false;

            var shortcutType = shortcut.GetType();

            // 读取目标路径，作为默认图标来源
            var target = shortcutType.InvokeMember(
                "TargetPath",
                BindingFlags.GetProperty, null, shortcut, null) as string;

            var location = !string.IsNullOrEmpty(target) && File.Exists(target)
                ? BuildIconLocation(target)
                : string.Empty; // 留空交由系统回退

            shortcutType.InvokeMember(
                "IconLocation",
                BindingFlags.SetProperty, null, shortcut, new object[] { location });
            shortcutType.InvokeMember(
                "Save",
                BindingFlags.InvokeMethod, null, shortcut, null);

            Console.WriteLine($"[IconManager] 已恢复默认图标: {shortcutPath}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconManager] 恢复图标失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>构造 IconLocation 字符串 — .ico 用纯路径，.exe/.dll 追加 ",0"</summary>
    private static string BuildIconLocation(string iconPath)
    {
        var ext = System.IO.Path.GetExtension(iconPath).ToLowerInvariant();
        if (ext == ".ico") return iconPath;
        if (ext is ".exe" or ".dll")
        {
            // 若已包含索引则原样返回
            if (iconPath.Contains(',')) return iconPath;
            return iconPath + ",0";
        }
        return iconPath;
    }
}
