using zDesktop.Core.Fences;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Fences;

/// <summary>
/// 分区同步引擎（设计案 v3.1 §4.2 决策 3）。
///
/// <para>两个方向机制不同：</para>
/// <list type="bullet">
/// <item><b>zDesktop → Explorer</b>（<see cref="SyncToExplorer"/>）：事件驱动、我方主动。
/// 分区增删改、图标入区出区、显示器变化、排序变更时，由分区几何解算出坐标写回。</item>
/// <item><b>Explorer → zDesktop</b>（<see cref="PollFromExplorer"/>）：只能轮询。
/// 用户手工拖动图标不会发出任何通知，只能读坐标对账。</item>
/// </list>
///
/// <para><b>轮询只在桌面聚焦时进行</b>（决策 4）：图标坐标只可能因用户拖拽而变，
/// 而拖拽必然发生在桌面获得焦点期间。桌面失焦时完全不轮询，CPU 占用为 0。</para>
/// </summary>
public sealed class FenceSyncEngine
{
    private readonly NativeIconController _icons;
    private readonly DesktopItemResolver _resolver;

    /// <summary>
    /// 上一次由我们写入的坐标。
    ///
    /// 轮询时用它排除「自己刚写的位置」——否则每次写回都会被下一轮轮询
    /// 误判成「用户拖动了图标」，进而反复打上手动标记。
    /// </summary>
    private readonly Dictionary<string, IconPoint> _lastWritten =
        new(StringComparer.OrdinalIgnoreCase);

    public FenceSyncEngine(NativeIconController icons, DesktopItemResolver resolver)
    {
        _icons = icons;
        _resolver = resolver;
    }

    /// <summary>分区内容区的标题栏高度与内边距（物理像素，按 100% 缩放基准）</summary>
    public int TitleHeight { get; set; } = 32;
    public int Padding { get; set; } = 8;

    /// <summary>
    /// 「自动排列图标」是否开启 —— 分区的硬前置条件（决策 2）。
    /// 开启时 Explorer 会强制重排全部图标，任何写入都会被立刻覆盖。
    /// </summary>
    public bool IsBlockedByAutoArrange => _icons.EnsureConnected() && _icons.IsAutoArrange;

    /// <summary>当前 Shell 网格规格。间距运行时查询，原点取首个图标的坐标对齐基准。</summary>
    public GridSpec ReadGrid()
    {
        var (cx, cy) = _icons.ItemSpacing;

        // 网格原点：Explorer 的格点相位。取任一图标坐标对间距取模即可还原，
        // 没有图标时退化为 (0,0)，此时任何坐标都在格上。
        var first = _icons.Count > 0 ? _icons.GetPosition(0) : null;
        var ox = first.HasValue ? Mod(first.Value.X, cx) : 0;
        var oy = first.HasValue ? Mod(first.Value.Y, cy) : 0;

        return new GridSpec(ox, oy, cx, cy);

        static int Mod(int v, int m) => m <= 0 ? 0 : ((v % m) + m) % m;
    }

    // ===== zDesktop → Explorer =====

    /// <summary>
    /// 把全部分区的图标归位写回 Explorer。
    /// </summary>
    /// <returns>成功写入的图标数</returns>
    public int SyncToExplorer(FenceConfig config, FenceAssignmentModel assignments, FenceCoordinateSpace space)
    {
        if (!_icons.EnsureConnected()) return 0;
        if (_icons.IsAutoArrange)
        {
            Console.WriteLine("[FenceSync] 「自动排列图标」已开启，跳过写回（写了也会被立刻覆盖）");
            return 0;
        }

        var grid = ReadGrid();
        if (!grid.IsValid) return 0;

        var pathToIndex = _resolver.ResolveAll(_icons.ReadAll());
        var writes = new List<(int Index, IconPoint Point)>();
        var expected = new Dictionary<string, IconPoint>(StringComparer.OrdinalIgnoreCase);

        foreach (var fence in config.Fences)
        {
            // 折叠的分区不摆放图标（图标由上层隐藏或维持原位）
            if (fence.Collapsed) continue;

            var fenceRect = space.FenceToIconSpace(fence);
            if (fenceRect == null) continue; // 显示器已拔掉，跳过

            var content = FenceGeometry.ContentAreaOf(fenceRect.Value, TitleHeight, Padding);
            var ordered = assignments.OrderedPaths(fence.Id, fence.SortMode, _resolver.Snapshots);
            var layout = FenceGeometry.SolveLayout(content, grid, ordered);

            foreach (var (path, point) in layout)
            {
                if (!pathToIndex.TryGetValue(path, out var index)) continue; // 图标不在桌面上（已删）
                writes.Add((index, point));
                expected[path] = point;
            }
        }

        var written = _icons.SetPositions(writes);

        // 记录期望坐标，供轮询排除自己写入的结果
        foreach (var (path, point) in expected) _lastWritten[path] = point;

        if (written > 0)
            Console.WriteLine($"[FenceSync] 已写回 {written}/{writes.Count} 个图标坐标");

        return written;
    }

    // ===== Explorer → zDesktop =====

    /// <summary>
    /// 轮询读取图标坐标，把用户的手工拖动转成归属变更。
    ///
    /// <para>只处理「归属发生变化」的图标：拖进某分区 → 入区并标记手动；
    /// 拖出所有分区 → 出区并标记手动（决策 5，防止规则把它收回去）。</para>
    /// </summary>
    /// <returns>发生归属变更的图标数</returns>
    public int PollFromExplorer(FenceConfig config, FenceAssignmentModel assignments, FenceCoordinateSpace space)
    {
        if (!_icons.EnsureConnected()) return 0;

        var changes = 0;

        foreach (var icon in _icons.ReadAll())
        {
            var path = _resolver.Resolve(icon.DisplayName);
            if (path == null) continue; // 虚拟项或重名歧义 —— 安全起见不处理

            // 排除自己刚写进去的坐标，否则会把写回误判成用户拖动
            if (_lastWritten.TryGetValue(path, out var written) && written == icon.Position)
                continue;

            var landedIn = space.FenceAt(icon.Position, config.Fences);
            var recorded = assignments.Find(path);
            var recordedFenceId = recorded?.FenceId ?? string.Empty;
            var landedFenceId = landedIn?.Id ?? string.Empty;

            if (string.Equals(recordedFenceId, landedFenceId, StringComparison.Ordinal))
                continue; // 归属没变

            if (landedIn != null)
            {
                // 拖进了某个分区 —— 追加到末尾
                var order = assignments.InFence(landedIn.Id).Count;
                assignments.Assign(path, landedIn.Id, order, manual: true);
            }
            else if (!string.IsNullOrEmpty(recordedFenceId))
            {
                // 从分区里拖了出来
                assignments.Unassign(path, manual: true);
            }
            else
            {
                continue; // 本来就不属于任何分区，拖到哪都不产生变更
            }

            _lastWritten.Remove(path);
            changes++;
        }

        if (changes > 0)
            Console.WriteLine($"[FenceSync] 检测到 {changes} 个图标的归属变更（用户拖拽）");

        return changes;
    }

    /// <summary>清空写入记忆（分区几何变化后调用，避免用旧期望值屏蔽真实拖动）</summary>
    public void ForgetWrittenPositions() => _lastWritten.Clear();
}

/// <summary>
/// 桌面是否处于前台 —— 焦点驱动轮询的判据（设计案 v3.1 §4.2 决策 4、§八 性能预算）。
///
/// <para>桌面失焦时完全停止轮询，这是「空闲态 CPU &lt; 0.1%」这条预算能成立的关键。</para>
/// </summary>
public static class DesktopFocus
{
    /// <summary>当前前台窗口是否属于桌面（Progman / WorkerW / DefView / SysListView32）</summary>
    public static bool IsDesktopForeground()
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        // 沿父链向上找，命中桌面相关类名即可
        var h = fg;
        for (var depth = 0; depth < 4 && h != IntPtr.Zero; depth++)
        {
            var cls = GetClassNameOf(h);
            if (cls is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32")
                return true;

            h = Win32.GetParent(h);
        }

        return false;
    }

    private static string GetClassNameOf(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(64);
        Win32.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
