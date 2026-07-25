using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ZDesktop.Spikes.M3Interaction;

/// <summary>
/// 闪烁量化 —— 分区拖动松手后重设壁纸，Windows 会走一次淡入过渡。
///
/// <para>「有没有可感知的闪烁」这种主观描述没法用来判断方案是否可接受，
/// 这里把它量成两个数：<b>持续多少毫秒</b>、<b>亮度变化幅度多大</b>。</para>
///
/// <para>做法：松手后立刻以约 60fps 连拍一小块桌面区域，
/// 计算相邻帧的平均亮度差。淡入过程会表现为一串连续的非零差值。</para>
/// </summary>
internal static class FlickerMeter
{
    /// <summary>单帧采样结果</summary>
    private readonly record struct Frame(long ElapsedMs, double MeanLuma);

    /// <summary>
    /// 连拍并量化。
    /// </summary>
    /// <param name="region">采样区域（屏幕物理坐标）</param>
    /// <param name="durationMs">连拍总时长</param>
    public static void Measure(Rectangle region, int durationMs = 2000)
    {
        var frames = new List<Frame>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);

        while (sw.ElapsedMilliseconds < durationMs)
        {
            g.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size);
            frames.Add(new Frame(sw.ElapsedMilliseconds, MeanLuma(bmp)));
            Thread.Sleep(16);
        }

        Report(frames);
    }

    /// <summary>整幅平均亮度。用 LockBits 逐像素扫，GetPixel 太慢会拖累采样率。</summary>
    private static double MeanLuma(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            var total = 0.0;
            var count = 0;
            var stride = data.Stride;

            unsafe
            {
                var scan = (byte*)data.Scan0;
                // 每 4 个像素采一个：精度足够，采样率翻几倍
                for (var y = 0; y < bmp.Height; y += 2)
                {
                    var row = scan + y * stride;
                    for (var x = 0; x < bmp.Width; x += 2)
                    {
                        var p = row + x * 4;
                        total += 0.299 * p[2] + 0.587 * p[1] + 0.114 * p[0];
                        count++;
                    }
                }
            }

            return count == 0 ? 0 : total / count;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static void Report(List<Frame> frames)
    {
        if (frames.Count < 2)
        {
            Console.WriteLine("  采样帧数不足");
            return;
        }

        var deltas = new List<(long Ms, double Delta)>();
        for (var i = 1; i < frames.Count; i++)
            deltas.Add((frames[i].ElapsedMs, Math.Abs(frames[i].MeanLuma - frames[i - 1].MeanLuma)));

        // 亮度差超过这个值才算「肉眼可见的一帧变化」（0-255 标度）
        const double VisibleThreshold = 0.8;

        var visible = deltas.Where(d => d.Delta >= VisibleThreshold).ToList();
        var maxDelta = deltas.Max(d => d.Delta);

        Console.WriteLine($"  采样 {frames.Count} 帧 / {frames[^1].ElapsedMs} ms（约 {frames.Count * 1000.0 / Math.Max(1, frames[^1].ElapsedMs):F0} fps）");
        Console.WriteLine($"  最大帧间亮度差 {maxDelta:F2}（0-255 标度）");

        if (visible.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  未检测到可见的画面跳变");
            Console.ResetColor();
            return;
        }

        var first = visible[0].Ms;
        var last = visible[^1].Ms;
        Console.WriteLine($"  可见跳变 {visible.Count} 帧，出现在 {first}~{last} ms（跨度 {last - first} ms）");

        // 逐帧列出，便于判断是「一次切换」还是「一段淡入」
        foreach (var v in visible.Take(12))
            Console.WriteLine($"    {v.Ms,5} ms   Δ={v.Delta:F2}");
        if (visible.Count > 12) Console.WriteLine($"    …… 其余 {visible.Count - 12} 帧");

        Console.WriteLine();
        Console.WriteLine(visible.Count <= 2
            ? "  判读：跳变集中在 1-2 帧 —— 属于一次性切换，不是渐变淡入"
            : "  判读：跳变跨越多帧 —— 存在渐变过渡，即用户感知到的「闪烁」");
    }
}
