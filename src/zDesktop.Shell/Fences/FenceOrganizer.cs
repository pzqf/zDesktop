using zDesktop.Core.Fences;

namespace zDesktop.Shell.Fences;

/// <summary>一次整理操作的结果</summary>
/// <param name="SnapshotId">执行前快照 Id；为 null 表示快照失败（此时不应执行）</param>
/// <param name="AssignedCount">新归入分区的文件数</param>
/// <param name="WrittenCount">实际写回 Explorer 的图标数</param>
public readonly record struct OrganizeResult(string? SnapshotId, int AssignedCount, int WrittenCount)
{
    public bool Succeeded => SnapshotId != null;
}

/// <summary>
/// 一键整理与撤销（设计案 v3.1 §3.1）。
///
/// <para><b>硬约束</b>：任何会移动图标的批量操作，
/// 必须<b>先落盘快照、再动手</b>。快照失败即中止整个操作 ——
/// 没有退路的整理不许执行（§二 原则 3：永不在用户未确认前移动文件或改变布局，
/// 且一切修改必须可逆）。</para>
/// </summary>
public sealed class FenceOrganizer
{
    private readonly FenceSyncEngine _sync;
    private readonly NativeIconController _icons;
    private readonly DesktopItemResolver _resolver;
    private readonly FenceSnapshotStore _snapshots;

    public FenceOrganizer(FenceSyncEngine sync, NativeIconController icons,
        DesktopItemResolver resolver, FenceSnapshotStore snapshots)
    {
        _sync = sync;
        _icons = icons;
        _resolver = resolver;
        _snapshots = snapshots;
    }

    /// <summary>
    /// 干跑：只算不改，返回将要归入各分区的文件。
    ///
    /// 首次运行引导的「预览效果」与规则首次生效前的确认都用它（§六）。
    /// </summary>
    public Dictionary<string, List<string>> Preview(FenceConfig config, FenceAssignmentModel assignments)
    {
        _resolver.Refresh();

        // 在副本上试算，绝不污染真实归属
        var trial = new FenceAssignmentModel(assignments.ToList().Select(Clone));
        var before = trial.All.ToDictionary(a => a.Path, a => a.FenceId, StringComparer.OrdinalIgnoreCase);

        trial.ApplyRules(_resolver.Snapshots.Values, config.Fences, DateTime.Now);

        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var a in trial.All)
        {
            if (string.IsNullOrEmpty(a.FenceId)) continue;
            if (before.TryGetValue(a.Path, out var old) && old == a.FenceId) continue; // 本来就在里面

            if (!result.TryGetValue(a.FenceId, out var list))
                result[a.FenceId] = list = new List<string>();
            list.Add(a.Path);
        }

        return result;
    }

    /// <summary>
    /// 执行一键整理：落盘快照 → 套用规则 → 写回坐标。
    /// </summary>
    public OrganizeResult Organize(FenceConfig config, FenceAssignmentModel assignments,
        FenceCoordinateSpace space, string label = "一键整理",
        IEnumerable<string>? fenceIdsBefore = null)
    {
        _resolver.Refresh();

        // 1. 先落盘快照。失败即中止 —— 不给自己留无法撤销的操作
        //    调用方在建完分区之后才进来，所以「现在有的」减去「进来之前有的」
        //    就是本次新建的那几个，撤销时精确删它们。
        var before = new HashSet<string>(
            fenceIdsBefore ?? config.Fences.Select(f => f.Id), StringComparer.Ordinal);
        var created = config.Fences.Select(f => f.Id).Where(id => !before.Contains(id)).ToList();

        var snapshotId = CaptureSnapshot(assignments, label, created);
        if (snapshotId == null)
        {
            Console.WriteLine("[Organizer] 快照落盘失败，已中止整理（不执行无法撤销的操作）");
            return new OrganizeResult(null, 0, 0);
        }

        // 2. 套用规则
        var assigned = assignments.ApplyRules(_resolver.Snapshots.Values, config.Fences, DateTime.Now);

        // 3. 写回坐标
        var written = _sync.SyncToExplorer(config, assignments, space);

        Console.WriteLine($"[Organizer] 整理完成：{assigned} 个文件入区，{written} 个图标归位（快照 {snapshotId}）");
        return new OrganizeResult(snapshotId, assigned, written);
    }

    /// <summary>
    /// 撤销到指定快照：恢复归属记录与图标坐标。
    /// </summary>
    /// <returns>成功还原的图标数；快照不存在返回 -1</returns>
    public int Undo(string snapshotId, FenceAssignmentModel assignments, FenceConfig? config = null)
    {
        var snapshot = _snapshots.Load(snapshotId);
        if (snapshot == null)
        {
            Console.WriteLine($"[Organizer] 快照 {snapshotId} 不存在，无法撤销");
            return -1;
        }

        // 1. 归属记录整体回滚
        var restored = new FenceAssignmentModel(snapshot.Assignments);
        assignments.ReplaceAll(restored.ToList());

        // 2. 删掉本次操作新建的分区。
        //    不做这一步，撤销后桌面上会留下一个 0 归属的空框 ——
        //    用户理解的撤销是「回到点应用之前」，那时它们并不存在。
        //    null 表示快照产生于该字段存在之前，无从判断，就什么都不删。
        if (config != null && snapshot.CreatedFenceIds is { Count: > 0 } createdIds)
        {
            var drop = new HashSet<string>(createdIds, StringComparer.Ordinal);
            var removed = config.Fences.RemoveAll(f => drop.Contains(f.Id));
            if (removed > 0) Console.WriteLine($"[Organizer] 已移除本次整理新建的 {removed} 个分区");
        }

        // 3. 图标坐标回滚
        _resolver.Refresh();
        var pathToIndex = _resolver.ResolveAll(_icons.ReadAll());
        var writes = new List<(int, IconPoint)>();

        foreach (var (path, point) in snapshot.IconPositions)
        {
            if (pathToIndex.TryGetValue(path, out var index))
                writes.Add((index, point.ToIconPoint()));
        }

        var written = _icons.SetPositions(writes);
        _sync.ForgetWrittenPositions();

        Console.WriteLine($"[Organizer] 已撤销到 {snapshotId}：还原 {written}/{writes.Count} 个图标坐标");
        return written;
    }

    /// <summary>撤销最近一次操作</summary>
    public int UndoLatest(FenceAssignmentModel assignments, FenceConfig? config = null)
    {
        var latest = _snapshots.Latest();
        return latest == null ? -1 : Undo(latest.Id, assignments, config);
    }

    /// <summary>捕获当前桌面状态</summary>
    private string? CaptureSnapshot(FenceAssignmentModel assignments, string label,
        IEnumerable<string> fenceIds)
    {
        var positions = new Dictionary<string, IconPoint>(StringComparer.OrdinalIgnoreCase);

        foreach (var icon in _icons.ReadAll())
        {
            var path = _resolver.Resolve(icon.DisplayName);
            if (path != null) positions[path] = icon.Position;
        }

        return _snapshots.Capture(label, positions, assignments.All, positions.Count, fenceIds);
    }

    private static FenceAssignment Clone(FenceAssignment a) => new()
    {
        Path = a.Path,
        FenceId = a.FenceId,
        Order = a.Order,
        Manual = a.Manual,
    };
}
