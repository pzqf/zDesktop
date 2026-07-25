using System.IO;
using System.Text;
using zDesktop.Shell.Desktop;
using zDesktop.Shell.Interop;

namespace ZDesktop.Spikes.M3Wall;

/// <summary>
/// M3-C 探针：实测本机的壁纸模式与每屏壁纸状态（<b>只读</b>，不改任何设置）。
///
/// 要回答的问题：分区背景要合成进壁纸，就必须知道
/// (1) 当前是「每屏一张」还是「跨屏一张」；
/// (2) Shell 的显示器标识与 <c>\\.\DISPLAYn</c> 如何对应；
/// (3) 每屏当前壁纸是什么、多大 —— 合成时要以它为底图。
/// </summary>
internal static class Program
{
    public static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== M3-C 壁纸状态探针（只读）===\n");

        using var surface = new WallpaperSurface();
        if (!surface.IsAvailable)
        {
            Console.WriteLine($"IDesktopWallpaper 不可用：{surface.LastError}");
            Console.WriteLine("→ 分区背景需降级为「无背景」或改用候选 B");
            return 2;
        }

        Console.WriteLine("IDesktopWallpaper 可用");

        var pos = surface.GetPosition();
        Console.WriteLine($"壁纸摆放方式: {pos}");
        if (pos == WallpaperPosition.Span)
            Console.WriteLine("  ⚠ 跨屏模式 —— 一张图铺满整个虚拟屏，合成时必须按虚拟屏尺寸出图");
        else
            Console.WriteLine("  → 每屏独立 —— 可按屏分别合成，互不影响");

        Console.WriteLine("\n各显示器壁纸状态：");
        var wallpapers = surface.Enumerate();
        if (wallpapers.Count == 0)
            Console.WriteLine("  （枚举为空）");

        foreach (var w in wallpapers)
        {
            Console.WriteLine($"  MonitorId: {w.MonitorId}");
            Console.WriteLine($"    矩形: ({w.Rect.Left},{w.Rect.Top})-({w.Rect.Right},{w.Rect.Bottom})  " +
                              $"{w.Rect.Width}x{w.Rect.Height}");
            if (string.IsNullOrEmpty(w.WallpaperPath))
            {
                Console.WriteLine("    壁纸: <未设置 / 纯色>");
            }
            else
            {
                var exists = File.Exists(w.WallpaperPath);
                var size = exists ? $"{new FileInfo(w.WallpaperPath).Length / 1024} KB" : "文件不存在";
                Console.WriteLine($"    壁纸: {w.WallpaperPath}");
                Console.WriteLine($"          {size}");
            }
        }

        Console.WriteLine("\n与 MonitorSet 的对应关系：");
        var monitors = MonitorSet.Enumerate();
        foreach (var m in monitors)
        {
            var match = wallpapers.FirstOrDefault(w =>
                w.Rect.Left == m.Bounds.Left && w.Rect.Top == m.Bounds.Top &&
                w.Rect.Right == m.Bounds.Right && w.Rect.Bottom == m.Bounds.Bottom);

            Console.WriteLine($"  {m.Key,-16} {m.Bounds.Width}x{m.Bounds.Height} @({m.Bounds.Left},{m.Bounds.Top})");
            Console.WriteLine($"    → {(string.IsNullOrEmpty(match.MonitorId) ? "<按矩形未匹配到>" : match.MonitorId)}");
        }

        Console.WriteLine("\n结论要点：");
        Console.WriteLine("  合成底图尺寸应取该屏 Bounds（而非工作区，壁纸铺满全屏含任务栏区域）");
        Console.WriteLine("  显示器对应关系按矩形匹配（MonitorId 与 \\\\.\\DISPLAYn 是两套标识）");
        return 0;
    }
}
