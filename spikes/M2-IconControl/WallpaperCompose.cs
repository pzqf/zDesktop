using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ZDesktop.Spikes.M2;

/// <summary>
/// M2 spike 第二问：分区背景「合成进壁纸」（候选 A）的开销（设计案 v3.1 §4.3）。
///
/// 候选 A 的思路是把「用户壁纸 + 分区半透明矩形」合成为一张图设为桌面壁纸，
/// 从而彻底绕开 Z 序问题。代价是每次分区增删/移动都要重新合成落盘。
/// 本测只量**合成本身**的耗时（解码 → 绘制 → 编码 → 落盘），
/// 不调用 SPI_SETDESKWALLPAPER —— 那会真的改掉用户壁纸。
/// </summary>
internal static class WallpaperCompose
{
    public static void Run(string tempDir)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("分区背景合成开销（候选 A）");
        Console.ResetColor();

        Directory.CreateDirectory(tempDir);

        // 用典型桌面分辨率造底图，避免依赖用户实际壁纸文件
        foreach (var (w, h, label) in new[] { (1920, 1080, "1080p"), (2560, 1440, "1440p"), (3840, 2160, "4K") })
        {
            var basePath = Path.Combine(tempDir, $"base-{label}.jpg");
            if (!File.Exists(basePath))
            {
                using var seed = new Bitmap(w, h);
                using (var g = Graphics.FromImage(seed))
                using (var brush = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                           Color.FromArgb(28, 32, 48), Color.FromArgb(90, 60, 120), 45f))
                {
                    g.FillRectangle(brush, 0, 0, w, h);
                }
                seed.Save(basePath, ImageFormat.Jpeg);
            }

            // 预热一次，排除首次 JIT 与 GDI+ 初始化的干扰
            Compose(basePath, Path.Combine(tempDir, "warm.jpg"), w, h);

            var sw = Stopwatch.StartNew();
            const int rounds = 5;
            for (var i = 0; i < rounds; i++)
                Compose(basePath, Path.Combine(tempDir, $"out-{label}.jpg"), w, h);
            sw.Stop();

            var avg = sw.Elapsed.TotalMilliseconds / rounds;
            var outSize = new FileInfo(Path.Combine(tempDir, $"out-{label}.jpg")).Length / 1024;
            Console.WriteLine($"  {label,-6} {w}x{h}：合成均耗时 {avg,6:N1} ms   产物 {outSize} KB");
        }

        Console.WriteLine();
        Console.WriteLine("  判读：拖动分区时若每帧都重新合成，>16ms 即掉帧。");
        Console.WriteLine("        设计已规定拖动期间用临时交互层跟手、松手后才合成落盘，");
        Console.WriteLine("        因此该耗时只发生在「松手」这一次，属可接受范围。");
    }

    /// <summary>合成一张「壁纸 + 4 个分区半透明圆角矩形」的图</summary>
    private static void Compose(string basePath, string outPath, int w, int h)
    {
        using var src = Image.FromFile(basePath);
        using var canvas = new Bitmap(w, h);
        using var g = Graphics.FromImage(canvas);

        g.CompositingQuality = CompositingQuality.HighSpeed;
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        g.DrawImage(src, 0, 0, w, h);

        // 4 个分区框
        var rects = new[]
        {
            new Rectangle((int)(w * 0.04), (int)(h * 0.06), (int)(w * 0.20), (int)(h * 0.34)),
            new Rectangle((int)(w * 0.04), (int)(h * 0.44), (int)(w * 0.20), (int)(h * 0.30)),
            new Rectangle((int)(w * 0.28), (int)(h * 0.06), (int)(w * 0.24), (int)(h * 0.40)),
            new Rectangle((int)(w * 0.74), (int)(h * 0.06), (int)(w * 0.22), (int)(h * 0.50)),
        };

        using var fill = new SolidBrush(Color.FromArgb(64, 20, 22, 34));
        using var border = new Pen(Color.FromArgb(90, 140, 130, 240), 1.5f);

        foreach (var r in rects)
        {
            using var path = RoundedRect(r, 16);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var pars = new EncoderParameters(1);
        pars.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
        canvas.Save(outPath, encoder, pars);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
