using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using zDesktop.Core.Fences;
using zDesktop.Shell.Desktop;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Fences;

/// <summary>
/// 分区背景合成器 —— 设计案 v3.1 §4.3 候选 A。
///
/// <para>把「用户壁纸 + 分区半透明矩形」合成为一张图设为桌面壁纸。
/// 零 Z 序问题、零常驻渲染开销、退出还原原壁纸即可。
/// M2 spike 实测合成耗时 1080p 10.1ms / 4K 37.8ms，按「松手才合成」的约定可接受。</para>
///
/// <para><b>必须防住的两件事</b>（M3-C 探针在真机上发现）：</para>
/// <list type="number">
/// <item><b>自我叠加</b>：若每次都以「当前壁纸」为底图，我们自己的输出就会成为下一次的底图，
/// 分区框被反复叠加画上去。因此底图永远取<b>记录在还原账本里的原图</b>，
/// 只有当前壁纸不是我们的产物时才更新该记录。</item>
/// <item><b>第三方壁纸工具</b>：本机实测有元气桌面在管理壁纸（<c>E:\元气壁纸缓存\</c>）。
/// 它轮换壁纸后，我们需要以新壁纸为底图重新合成，而不是把它覆盖掉。</item>
/// </list>
/// </summary>
public sealed class FenceCompositor : IDisposable
{
    /// <summary>合成产物目录。放 LocalAppData —— 是缓存，不该跟随漫游配置同步。</summary>
    private static readonly string DefaultCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "zDesktop", "fence-bg");

    private readonly string _cacheDir;
    private readonly WallpaperSurface _surface;
    private readonly RestoreJournal _journal;

    public FenceCompositor(RestoreJournal journal, string? cacheDir = null)
    {
        _journal = journal;
        _cacheDir = cacheDir ?? DefaultCacheDir;
        _surface = new WallpaperSurface();
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>壁纸接口是否可用；不可用时分区降级为无背景（§七 失败降级矩阵）</summary>
    public bool IsAvailable => _surface.IsAvailable;

    public string? LastError => _surface.LastError;

    /// <summary>标题栏高度与圆角（物理像素，按 100% 缩放基准，合成时按屏 DPI 缩放）</summary>
    public int TitleHeight { get; set; } = 32;
    public int CornerRadius { get; set; } = 16;

    /// <summary>该路径是否为我们自己的合成产物</summary>
    public bool IsOurOutput(string? path)
        => !string.IsNullOrEmpty(path)
        && path.StartsWith(_cacheDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 取某屏合成用的底图路径。
    ///
    /// 当前壁纸不是我们的产物时，说明用户或第三方工具换了壁纸 —— 以它为新底图并记账；
    /// 是我们的产物时，取账本里记录的原图，绝不拿自己的输出当底图。
    /// </summary>
    public string? ResolveSourceWallpaper(string monitorId)
    {
        var current = _surface.GetWallpaper(monitorId);

        if (!string.IsNullOrEmpty(current) && !IsOurOutput(current))
        {
            _journal.RememberWallpaper(monitorId, current);
            return current;
        }

        return _journal.GetRememberedWallpaper(monitorId);
    }

    /// <summary>
    /// 为一个显示器合成并应用分区背景。
    /// </summary>
    /// <param name="monitorId">Shell 显示器设备路径</param>
    /// <param name="monitorRect">该屏矩形（物理像素）—— 底图尺寸取全屏而非工作区，壁纸铺满含任务栏区域</param>
    /// <param name="fenceRectsPhysical">该屏上各分区的矩形（相对该屏左上角的物理像素）与配色</param>
    /// <returns>是否成功应用</returns>
    public bool ComposeAndApply(string monitorId, Win32.RECT monitorRect,
        IReadOnlyList<(IconRect Rect, string Color, bool Collapsed)> fenceRectsPhysical)
    {
        if (!_surface.IsAvailable) return false;

        try
        {
            var source = ResolveSourceWallpaper(monitorId);
            var outPath = Path.Combine(_cacheDir, $"bg-{Sanitize(monitorId)}.jpg");

            Compose(source, monitorRect.Width, monitorRect.Height, fenceRectsPhysical, outPath);

            return _surface.SetWallpaper(monitorId, outPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FenceCompositor] 合成失败 ({monitorId}): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 执行合成。底图为空/损坏时退化为纯深色底 —— 分区框仍然可见，
    /// 不因为读不到壁纸就让整个分区功能失效。
    /// </summary>
    private void Compose(string? sourcePath, int width, int height,
        IReadOnlyList<(IconRect Rect, string Color, bool Collapsed)> fences, string outPath)
    {
        using var canvas = new Bitmap(Math.Max(1, width), Math.Max(1, height));
        using (var g = Graphics.FromImage(canvas))
        {
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            DrawBackdrop(g, sourcePath, width, height);

            foreach (var (rect, color, collapsed) in fences)
            {
                // 折叠的分区只画标题条
                var h = collapsed ? Math.Min(TitleHeight, rect.Height) : rect.Height;
                DrawFence(g, new IconRect(rect.X, rect.Y, rect.Width, h), color, collapsed);
            }
        }

        SaveJpeg(canvas, outPath);
    }

    private static void DrawBackdrop(Graphics g, string? sourcePath, int width, int height)
    {
        if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
        {
            try
            {
                using var src = Image.FromFile(sourcePath);
                // 按 Fill 语义等比铺满并居中裁剪，与 Windows 默认摆放一致
                var scale = Math.Max((double)width / src.Width, (double)height / src.Height);
                var dw = (int)Math.Ceiling(src.Width * scale);
                var dh = (int)Math.Ceiling(src.Height * scale);
                g.DrawImage(src, (width - dw) / 2, (height - dh) / 2, dw, dh);
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FenceCompositor] 底图不可用，退化为纯色: {ex.Message}");
            }
        }

        using var fallback = new SolidBrush(Color.FromArgb(15, 17, 23));
        g.FillRectangle(fallback, 0, 0, width, height);
    }

    private void DrawFence(Graphics g, IconRect rect, string colorHex, bool collapsed)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var accent = ParseColor(colorHex);
        var bounds = new Rectangle(rect.X, rect.Y, rect.Width, rect.Height);

        using var path = RoundedRect(bounds, Math.Min(CornerRadius, Math.Min(rect.Width, rect.Height) / 2));
        using var fill = new SolidBrush(Color.FromArgb(collapsed ? 96 : 64, 20, 22, 34));
        using var border = new Pen(Color.FromArgb(110, accent.R, accent.G, accent.B), 1.5f);

        g.FillPath(fill, path);
        g.DrawPath(border, path);

        // 标题栏底色条，给标题文字一个可读的背景
        if (rect.Height >= TitleHeight)
        {
            var titleRect = new Rectangle(rect.X, rect.Y, rect.Width, TitleHeight);
            using var titleBrush = new LinearGradientBrush(titleRect,
                Color.FromArgb(70, accent.R, accent.G, accent.B),
                Color.FromArgb(20, accent.R, accent.G, accent.B),
                LinearGradientMode.Vertical);

            using var clip = RoundedRect(bounds, Math.Min(CornerRadius, Math.Min(rect.Width, rect.Height) / 2));
            var saved = g.Save();
            g.SetClip(clip);
            g.FillRectangle(titleBrush, titleRect);
            g.Restore(saved);
        }
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            if (!string.IsNullOrEmpty(hex) && hex.StartsWith('#') && hex.Length == 7)
            {
                return Color.FromArgb(
                    Convert.ToInt32(hex.Substring(1, 2), 16),
                    Convert.ToInt32(hex.Substring(3, 2), 16),
                    Convert.ToInt32(hex.Substring(5, 2), 16));
            }
        }
        catch
        {
            // 配色由用户/配置提供，格式错了用品牌色兜底而不是崩掉合成
        }
        return Color.FromArgb(108, 92, 231);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0) { path.AddRectangle(r); return path; }

        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void SaveJpeg(Bitmap bmp, string outPath)
    {
        var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var pars = new EncoderParameters(1);
        pars.Param[0] = new EncoderParameter(Encoder.Quality, 90L);

        // 先写临时文件再替换：Windows 可能正持有上一张图的句柄
        var tmp = outPath + ".tmp";
        bmp.Save(tmp, encoder, pars);
        File.Move(tmp, outPath, overwrite: true);
    }

    private static string Sanitize(string monitorId)
    {
        var chars = monitorId.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var s = new string(chars);
        return s.Length <= 64 ? s : s[^64..];
    }

    /// <summary>还原全部显示器的原始壁纸并清理缓存</summary>
    public void RestoreAll()
    {
        _journal.RestoreAll();

        try
        {
            if (Directory.Exists(_cacheDir))
                Directory.Delete(_cacheDir, recursive: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FenceCompositor] 清理缓存失败: {ex.Message}");
        }
    }

    public void Dispose() => _surface.Dispose();
}
