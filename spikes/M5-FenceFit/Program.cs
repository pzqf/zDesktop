using System.Text;
using zDesktop.Core.Fences;
using zDesktop.Shell.Desktop;
using zDesktop.Shell.Fences;

namespace ZDesktop.Spikes.M5;

/// <summary>
/// 量化「分区装不下自己的图标」问题（<b>只读</b>，不改任何东西）。
///
/// <para>用户反馈「应用分区里的图标超出框太多」。先测清楚：
/// 建议的分区尺寸能装几个、实际要装几个、溢出多少行。</para>
/// </summary>
internal static class Program
{
    public static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== M5 分区容量体检（只读）===\n");

        using var icons = new NativeIconController();
        if (!icons.Connect())
        {
            Console.WriteLine($"无法连接桌面：{icons.LastError}");
            return 2;
        }

        var (cx, cy) = icons.ItemSpacing;
        Console.WriteLine($"Shell 图标网格间距: {cx} x {cy} 物理像素");

        var resolver = new DesktopItemResolver();
        resolver.Refresh();
        Console.WriteLine($"桌面可解析项目: {resolver.AllPaths.Count} 个\n");

        var monitors = MonitorSet.Enumerate();
        var primary = MonitorSet.Primary(monitors);
        var (_, _, workW, workH) = primary.WorkAreaDip;
        Console.WriteLine($"主屏工作区: {workW:F0} x {workH:F0} DIP\n");

        var proposal = FenceProposal.Build(resolver.Snapshots.Values.ToList(), workW, workH);
        Console.WriteLine($"建议分区数: {proposal.Fences.Count}\n");

        // 与 FenceSyncEngine 的默认值保持一致
        const int titleHeight = 32;
        const int padding = 8;

        var anyOverflow = false;

        foreach (var f in proposal.Fences)
        {
            var fenceRect = new IconRect(0, 0, (int)f.Rect.Width, (int)f.Rect.Height);
            var content = FenceGeometry.ContentAreaOf(fenceRect, titleHeight, padding);
            var grid = new GridSpec(0, 0, cx, cy);

            var cols = FenceGeometry.ColumnsFor(content, grid);
            var capacity = FenceGeometry.CapacityFor(content, grid);
            var needed = f.Files.Count;
            var rowsNeeded = (int)Math.Ceiling(needed / (double)Math.Max(1, cols));
            var rowsAvailable = cols > 0 ? capacity / cols : 0;

            // 最后一个图标的落点，用来量溢出多少
            var last = FenceGeometry.SlotPosition(content, grid, Math.Max(0, needed - 1));
            var overflowPx = Math.Max(0, last.Y + cy - fenceRect.Bottom);

            Console.WriteLine($"分区「{f.Name}」");
            Console.WriteLine($"  尺寸        {f.Rect.Width:F0} x {f.Rect.Height:F0} DIP");
            Console.WriteLine($"  内容区      {content.Width} x {content.Height}");
            Console.WriteLine($"  列 x 行     {cols} x {rowsAvailable}  → 容量 {capacity}");
            Console.WriteLine($"  实际图标    {needed} 个，需要 {rowsNeeded} 行");

            if (needed > capacity)
            {
                anyOverflow = true;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  ⚠ 溢出      超出 {needed - capacity} 个图标，" +
                                  $"最后一行底部超出分区 {overflowPx} 像素");
                Console.ResetColor();

                // 装下所有图标需要多高
                var requiredHeight = titleHeight + padding * 2 + rowsNeeded * cy;
                Console.WriteLine($"  需要高度    {requiredHeight} DIP（当前 {f.Rect.Height:F0}）");
                Console.WriteLine($"  工作区可用  {workH:F0} DIP");

                if (requiredHeight > workH - f.Rect.Y)
                {
                    var maxRows = (int)((workH - f.Rect.Y - titleHeight - padding * 2) / cy);
                    var colsNeeded = (int)Math.Ceiling(needed / (double)Math.Max(1, maxRows));
                    Console.WriteLine($"  → 单列高度不够，需要加宽到 {colsNeeded} 列");
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✓ 装得下");
                Console.ResetColor();
            }

            Console.WriteLine();
        }

        Console.WriteLine(anyOverflow
            ? "结论：建议的分区尺寸装不下内容 —— 需要按图标数自适应。"
            : "结论：所有建议分区都装得下。");

        // ===== 实际配置校验：桌面上现有分区里的图标是否真的都在框内 =====
        Console.WriteLine("\n--- 现有分区的实际图标落点 ---");

        var store = new FenceStore();
        var config = store.Load();
        if (config.Fences.Count == 0)
        {
            Console.WriteLine("当前没有已保存的分区，跳过");
            return anyOverflow ? 1 : 0;
        }

        var assignments = new FenceAssignmentModel(config.Assignments);
        var space = FenceCoordinateSpace.Current();
        var pathToIndex = resolver.ResolveAll(icons.ReadAll());
        var positions = icons.ReadAll().ToDictionary(s => s.Index, s => s.Position);

        var anyOutside = false;

        foreach (var fence in config.Fences)
        {
            var rect = space.FenceToIconSpace(fence);
            if (rect == null) continue;

            var inFence = assignments.InFence(fence.Id);
            var outside = 0;
            var checkedCount = 0;

            foreach (var a in inFence)
            {
                if (!pathToIndex.TryGetValue(a.Path, out var idx)) continue;
                if (!positions.TryGetValue(idx, out var pos)) continue;

                checkedCount++;
                // 图标占一格，右下角也要在框内才算没超出
                var fitsX = pos.X >= rect.Value.X && pos.X + cx <= rect.Value.Right;
                var fitsY = pos.Y >= rect.Value.Y && pos.Y + cy <= rect.Value.Bottom;
                if (!fitsX || !fitsY)
                {
                    outside++;
                    var overRight = pos.X + cx - rect.Value.Right;
                    var overBottom = pos.Y + cy - rect.Value.Bottom;
                    Console.WriteLine($"    超框: {System.IO.Path.GetFileName(a.Path)}  " +
                                      $"落点 {pos}  框 ({rect.Value.X},{rect.Value.Y})-" +
                                      $"({rect.Value.Right},{rect.Value.Bottom})  " +
                                      $"右超 {overRight}  下超 {overBottom}");
                }
            }

            Console.WriteLine($"分区「{fence.Name}」{fence.Rect.Width:F0}x{fence.Rect.Height:F0}：" +
                              $"归属 {inFence.Count} 个，可定位 {checkedCount} 个");

            if (outside > 0)
            {
                anyOutside = true;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  ⚠ {outside} 个图标超出框外");
                Console.ResetColor();
            }
            else if (checkedCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✓ 全部 {checkedCount} 个图标都在框内");
                Console.ResetColor();
            }
        }

        return anyOverflow || anyOutside ? 1 : 0;
    }
}
