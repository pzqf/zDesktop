using System.Text;
using zDesktop.Core.Fences;
using zDesktop.Shell.Desktop;
using zDesktop.Shell.Fences;

namespace ZDesktop.Spikes.M3Sync;

/// <summary>
/// M3-B 真机验证：用**产品代码本身**（NativeIconController / FenceSyncEngine /
/// FenceCoordinateSpace / DesktopItemResolver）在真实桌面上跑一遍分区归位。
///
/// <para>单测只能覆盖纯逻辑；同步引擎真正要面对的是 Explorer，
/// 这一层必须在真机上验。</para>
///
/// <para><b>安全</b>：先快照全部图标坐标，finally 中无条件还原，异常路径也不例外。</para>
/// </summary>
internal static class Program
{
    private static int _pass;
    private static readonly List<string> _fail = new();

    private static void Check(bool ok, string name, string detail = "")
    {
        if (ok) { Ok($"[PASS] {name}"); _pass++; }
        else { Err($"[FAIL] {name}{(detail == "" ? "" : " -- " + detail)}"); _fail.Add($"{name}: {detail}"); }
    }

    private static void Ok(string s) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("  " + s); Console.ResetColor(); }
    private static void Err(string s) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("  " + s); Console.ResetColor(); }
    private static void Info(string s) => Console.WriteLine("  " + s);
    private static void Section(string s)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine(s); Console.ResetColor();
    }

    public static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== M3-B 同步引擎真机验证（使用产品代码）===");

        using var icons = new NativeIconController();
        if (!icons.Connect())
        {
            Err($"[ABORT] 无法连接桌面 ListView：{icons.LastError}");
            return 2;
        }

        var resolver = new DesktopItemResolver();
        resolver.Refresh();

        var engine = new FenceSyncEngine(icons, resolver);
        var space = FenceCoordinateSpace.Current();

        Section("1. 环境");
        Info($"图标数 {icons.Count}   桌面文件 {resolver.AllPaths.Count} 个");
        Info($"网格间距 {icons.ItemSpacing.Cx}x{icons.ItemSpacing.Cy}   " +
             $"自动排列={(icons.IsAutoArrange ? "开 ⚠" : "关")}   与网格对齐={(icons.IsSnapToGrid ? "开" : "关")}");
        Info($"虚拟屏原点 ({space.VirtualOriginX},{space.VirtualOriginY})   显示器 {space.Monitors.Count} 个");

        var grid = engine.ReadGrid();
        Info($"解算网格 原点({grid.OriginX},{grid.OriginY}) 间距 {grid.Cx}x{grid.Cy}");
        Check(grid.IsValid, "网格规格有效");
        Check(!icons.IsAutoArrange, "「自动排列图标」未开启（分区硬前置条件）",
            "开启时任何写入都会被 Explorer 立刻覆盖");

        if (resolver.Unresolved.Count > 0)
            Info($"无法解析路径的显示名 {resolver.Unresolved.Count} 个（虚拟项/重名，属预期）：" +
                 string.Join("、", resolver.Unresolved.Take(5)));

        // 快照
        var snapshot = icons.ReadAll();
        Check(snapshot.Count > 0, "读取到图标快照");
        if (snapshot.Count == 0) return 2;

        var primary = MonitorSet.Primary(space.Monitors.ToList());
        Info($"主显示器 {primary.Key}  工作区 {primary.WorkArea.Width}x{primary.WorkArea.Height}");

        try
        {
            Section("2. 构造分区并写回坐标");

            // 在主屏中部造一个测试分区，避开左上角原有图标
            var fence = new Fence
            {
                Id = "spike-fence",
                MonitorKey = primary.Key,
                Name = "验证分区",
                SortMode = FenceSortMode.Name,
                Rect = new FenceRect { X = 500, Y = 200, Width = 420, Height = 400 },
            };

            var config = new FenceConfig();
            config.Fences.Add(fence);

            // 取前若干个「能解析出路径」的图标入区
            var assignments = new FenceAssignmentModel();
            var picked = new List<string>();
            foreach (var s in snapshot)
            {
                var path = resolver.Resolve(s.DisplayName);
                if (path == null) continue;
                assignments.Assign(path, fence.Id, picked.Count, manual: false);
                picked.Add(path);
                if (picked.Count >= 6) break;
            }

            Info($"选入分区的图标 {picked.Count} 个");
            Check(picked.Count > 0, "至少有一个图标可解析路径并入区",
                "全部无法解析 —— DesktopItemResolver 可能有问题");
            if (picked.Count == 0) return 1;

            var written = engine.SyncToExplorer(config, assignments, space);
            Info($"写回 {written} 个坐标");
            Check(written == picked.Count, "全部入区图标坐标写回成功", $"仅 {written}/{picked.Count}");

            Section("3. 校验落点");

            var fenceRect = space.FenceToIconSpace(fence)!.Value;
            var content = FenceGeometry.ContentAreaOf(fenceRect, engine.TitleHeight, engine.Padding);
            Info($"分区图标空间矩形 ({fenceRect.X},{fenceRect.Y}) {fenceRect.Width}x{fenceRect.Height}");
            Info($"内容区 ({content.X},{content.Y}) {content.Width}x{content.Height}");

            System.Threading.Thread.Sleep(400);
            var after = icons.ReadAll();
            var byPath = new Dictionary<string, IconPoint>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in after)
            {
                var p = resolver.Resolve(s.DisplayName);
                if (p != null) byPath[p] = s.Position;
            }

            var inside = 0;
            var onGrid = 0;
            foreach (var path in picked)
            {
                if (!byPath.TryGetValue(path, out var pos)) continue;
                if (pos.X >= fenceRect.X && pos.X < fenceRect.Right &&
                    pos.Y >= fenceRect.Y && pos.Y < fenceRect.Bottom) inside++;
                if ((pos.X - grid.OriginX) % grid.Cx == 0 && (pos.Y - grid.OriginY) % grid.Cy == 0) onGrid++;
            }

            Info($"落在分区矩形内 {inside}/{picked.Count}   落在格点上 {onGrid}/{picked.Count}");
            Check(inside == picked.Count, "全部图标落在分区范围内",
                $"仅 {inside}/{picked.Count} —— 坐标空间映射可能有误");
            Check(onGrid == picked.Count, "全部图标落在 Shell 格点上",
                $"仅 {onGrid}/{picked.Count} —— 会被「与网格对齐」吸附走");

            Section("4. 轮询不得把自己的写入误判成用户拖动");

            // 这是 _lastWritten 抑制逻辑的核心回归：刚同步完立刻轮询，
            // 归属不应发生任何变化。若这里报出变更，说明每次写回都会污染手动标记。
            var changes = engine.PollFromExplorer(config, assignments, space);
            Info($"轮询报告的归属变更 {changes} 个");
            Check(changes == 0, "刚写回后轮询报告零变更",
                $"报告了 {changes} 个变更 —— 写回会被误判为用户拖拽，进而错误打上手动标记");

            Section("5. 模拟用户拖出分区");

            // 把第一个图标挪到分区外，轮询应当识别为「手动移出」
            var victim = picked[0];
            var outside = FenceGeometry.SnapToGrid(new IconPoint(fenceRect.X - grid.Cx * 3, fenceRect.Y), grid);
            var victimIndex = resolver.ResolveAll(icons.ReadAll()).TryGetValue(victim, out var vi) ? vi : -1;

            if (victimIndex >= 0)
            {
                icons.SetPosition(victimIndex, outside);
                System.Threading.Thread.Sleep(400);

                var moved = engine.PollFromExplorer(config, assignments, space);
                var rec = assignments.Find(victim);

                Info($"拖出后轮询变更 {moved} 个；该文件归属 = " +
                     $"'{rec?.FenceId ?? "<无记录>"}'  手动标记={rec?.Manual}");
                Check(moved >= 1 && rec != null && string.IsNullOrEmpty(rec.FenceId) && rec.Manual,
                    "拖出分区被识别为手动移出并打上手动标记",
                    "决策 5 的核心：用户拖出后规则不得再把它收回去");
            }
            else
            {
                Err("[SKIP] 无法定位被测图标索引，跳过拖出验证");
            }
        }
        finally
        {
            Section("6. 还原全部图标坐标");

            var restored = 0;
            foreach (var s in snapshot)
            {
                if (icons.SetPosition(s.Index, s.Position)) restored++;
            }
            System.Threading.Thread.Sleep(500);

            var now = icons.ReadAll();
            var mismatch = 0;
            foreach (var s in snapshot)
            {
                var cur = now.FirstOrDefault(x => x.Index == s.Index);
                if (cur.Position != s.Position) mismatch++;
            }

            Check(mismatch == 0, "全部图标已还原到原始位置",
                $"{mismatch} 项未还原 —— 可右键桌面刷新，或按名称排序后重排");
        }

        Section("结果");
        Console.WriteLine($"通过 {_pass} / 失败 {_fail.Count}");
        if (_fail.Count > 0)
        {
            foreach (var f in _fail) Err("- " + f);
            return 1;
        }

        Ok("M3-B 同步引擎真机验证全绿");
        return 0;
    }
}
