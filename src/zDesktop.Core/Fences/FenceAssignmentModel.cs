using System.Text.RegularExpressions;

namespace zDesktop.Core.Fences;

/// <summary>
/// 桌面文件的元数据快照。
///
/// 规则匹配只依赖这几项，不直接碰文件系统 —— 保证归属逻辑可纯函数化单测（T1-3）。
/// </summary>
public sealed record FileSnapshot(string Path, string Name, string Extension, DateTime ModifiedAt)
{
    public static FileSnapshot Of(string path, DateTime modifiedAt)
        => new(path,
               System.IO.Path.GetFileName(path),
               System.IO.Path.GetExtension(path),
               modifiedAt);
}

/// <summary>
/// 分区归属模型（设计案 v3.1 §4.2 决策 1 与决策 5）。
///
/// <para>真相源是「路径 → (分区, 序号, 是否手动)」。绝对坐标不入库，
/// 每次由 <see cref="FenceGeometry"/> 按分区几何实时解算。</para>
///
/// <para>核心不变量 —— <b>手动放置优先于一切自动规则</b>：
/// 一旦用户亲手把图标拖进/拖出某分区，该文件被打上 <see cref="FenceAssignment.Manual"/>，
/// 此后自动规则不得再移动它。这条防的是「我明明拖出来了，它又自己跑回去」，
/// 是桌面整理类工具最容易得罪用户的行为。</para>
///
/// 纯逻辑、无 IO、无 WPF/Win32 依赖，对应验收门槛 T1-3。
/// </summary>
public sealed class FenceAssignmentModel
{
    private readonly Dictionary<string, FenceAssignment> _byPath;

    public FenceAssignmentModel(IEnumerable<FenceAssignment>? existing = null)
    {
        _byPath = new Dictionary<string, FenceAssignment>(StringComparer.OrdinalIgnoreCase);
        if (existing == null) return;

        foreach (var a in existing)
        {
            if (!string.IsNullOrEmpty(a.Path))
                _byPath[a.Path] = a;
        }
    }

    /// <summary>当前全部归属记录</summary>
    public IReadOnlyCollection<FenceAssignment> All => _byPath.Values;

    public int Count => _byPath.Count;

    /// <summary>查询某文件的归属；未归属返回 null</summary>
    public FenceAssignment? Find(string path)
        => _byPath.TryGetValue(path, out var a) ? a : null;

    /// <summary>该文件是否已归属某分区</summary>
    public bool IsAssigned(string path) => _byPath.ContainsKey(path);

    /// <summary>该文件是否被用户手动放置过</summary>
    public bool IsManual(string path)
        => _byPath.TryGetValue(path, out var a) && a.Manual;

    // ===== 入区 / 出区 =====

    /// <summary>
    /// 把文件归入分区。
    /// </summary>
    /// <param name="manual">
    /// true 表示用户亲手拖拽所致 —— 会打上手动标记，此后自动规则不再动它。
    /// false 表示规则/一键整理所致。
    /// </param>
    /// <returns>归属是否发生了变化</returns>
    public bool Assign(string path, string fenceId, int order, bool manual)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(fenceId)) return false;

        if (_byPath.TryGetValue(path, out var existing))
        {
            var changed = existing.FenceId != fenceId || existing.Order != order;
            existing.FenceId = fenceId;
            existing.Order = order;
            // 手动标记只增不减：自动路径不得清掉用户已表达的意图
            if (manual) existing.Manual = true;
            return changed;
        }

        _byPath[path] = new FenceAssignment
        {
            Path = path,
            FenceId = fenceId,
            Order = order,
            Manual = manual,
        };
        return true;
    }

    /// <summary>
    /// 解除归属（用户把图标拖出分区范围）。
    ///
    /// <paramref name="manual"/> 为 true 时保留一条「已手动移出」的记录（FenceId 为空），
    /// 使自动规则不会立刻把它收回去 —— 这正是决策 5 要防的行为。
    /// </summary>
    public bool Unassign(string path, bool manual)
    {
        if (!_byPath.TryGetValue(path, out var existing)) return false;

        if (manual)
        {
            existing.FenceId = string.Empty;
            existing.Order = 0;
            existing.Manual = true;
            return true;
        }

        _byPath.Remove(path);
        return true;
    }

    /// <summary>取某分区内的全部归属，按 Order 升序</summary>
    public List<FenceAssignment> InFence(string fenceId)
        => _byPath.Values
            .Where(a => string.Equals(a.FenceId, fenceId, StringComparison.Ordinal))
            .OrderBy(a => a.Order)
            .ToList();

    /// <summary>
    /// 按排序模式求某分区内的路径顺序，供 <see cref="FenceGeometry.SolveLayout"/> 使用。
    /// </summary>
    public List<string> OrderedPaths(string fenceId, FenceSortMode mode, IReadOnlyDictionary<string, FileSnapshot> files)
    {
        var items = InFence(fenceId);

        IEnumerable<FenceAssignment> sorted = mode switch
        {
            FenceSortMode.Name => items.OrderBy(a => Lookup(a.Path)?.Name ?? a.Path, StringComparer.CurrentCultureIgnoreCase),
            FenceSortMode.Type => items
                .OrderBy(a => Lookup(a.Path)?.Extension ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => Lookup(a.Path)?.Name ?? a.Path, StringComparer.CurrentCultureIgnoreCase),
            FenceSortMode.Modified => items.OrderByDescending(a => Lookup(a.Path)?.ModifiedAt ?? DateTime.MinValue),
            _ => items, // Manual：保持 Order
        };

        return sorted.Select(a => a.Path).ToList();

        FileSnapshot? Lookup(string p) => files.TryGetValue(p, out var f) ? f : null;
    }

    /// <summary>把某分区内的 Order 重排为紧凑的 0..n-1，消除删除/移动留下的空洞</summary>
    public void Compact(string fenceId)
    {
        var i = 0;
        foreach (var a in InFence(fenceId))
            a.Order = i++;
    }

    // ===== 自动规则 =====

    /// <summary>
    /// 对尚未归属、且未被手动标记过的文件套用分区规则。
    ///
    /// 返回新产生归属的文件数。已归属的文件不会被重新分配 ——
    /// 规则只负责「收纳散落的新文件」，不负责搬动已安置好的文件。
    /// </summary>
    public int ApplyRules(IEnumerable<FileSnapshot> files, IReadOnlyList<Fence> fences, DateTime now)
    {
        var assigned = 0;
        // 每个分区的下一个可用序号，避免规则批量入区时 Order 全是 0
        var nextOrder = fences.ToDictionary(
            f => f.Id,
            f => InFence(f.Id).Count == 0 ? 0 : InFence(f.Id).Max(a => a.Order) + 1,
            StringComparer.Ordinal);

        foreach (var file in files)
        {
            // 已归属，或用户手动表达过意图 —— 一律跳过
            if (_byPath.TryGetValue(file.Path, out var existing))
            {
                if (existing.Manual || !string.IsNullOrEmpty(existing.FenceId)) continue;
            }

            foreach (var fence in fences)
            {
                if (fence.Rules.Count == 0) continue;
                if (!MatchesAny(file, fence.Rules, now)) continue;

                var order = nextOrder.TryGetValue(fence.Id, out var n) ? n : 0;
                nextOrder[fence.Id] = order + 1;

                if (Assign(file.Path, fence.Id, order, manual: false)) assigned++;
                break; // 命中第一个匹配的分区即止，避免一个文件进多个分区
            }
        }

        return assigned;
    }

    /// <summary>文件是否命中规则集中的任意一条</summary>
    public static bool MatchesAny(FileSnapshot file, IReadOnlyList<FenceRule> rules, DateTime now)
        => rules.Any(r => Matches(file, r, now));

    /// <summary>单条规则匹配</summary>
    public static bool Matches(FileSnapshot file, FenceRule rule, DateTime now)
    {
        switch (rule.Kind)
        {
            case FenceRuleKind.Extension:
                return rule.Values.Any(v =>
                    !string.IsNullOrEmpty(v) &&
                    string.Equals(Normalize(v), file.Extension, StringComparison.OrdinalIgnoreCase));

            case FenceRuleKind.NameRegex:
                var pattern = rule.Values.FirstOrDefault();
                if (string.IsNullOrEmpty(pattern)) return false;
                try
                {
                    // 用户可能写出灾难性回溯的正则，超时兜底而不是卡死整理流程
                    return Regex.IsMatch(file.Name, pattern,
                        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
                }
                catch (ArgumentException)
                {
                    return false; // 正则本身非法
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }

            case FenceRuleKind.ModifiedWithinDays:
                if (!int.TryParse(rule.Values.FirstOrDefault(), out var days) || days <= 0) return false;
                return file.ModifiedAt >= now.AddDays(-days);

            default:
                return false;
        }

        static string Normalize(string ext) => ext.StartsWith('.') ? ext : "." + ext;
    }

    // ===== 维护 =====

    /// <summary>
    /// 清理孤儿记录 —— 指向已不存在文件的归属。
    ///
    /// 进程未运行期间发生的删除/移动无法追踪，只能在启动时按现存文件集对账。
    /// </summary>
    public int PruneOrphans(IEnumerable<string> existingPaths)
    {
        var alive = new HashSet<string>(existingPaths, StringComparer.OrdinalIgnoreCase);
        var dead = _byPath.Keys.Where(p => !alive.Contains(p)).ToList();

        foreach (var p in dead) _byPath.Remove(p);
        return dead.Count;
    }

    /// <summary>
    /// 跟随文件重命名/移动更新归属（由 FileSystemWatcher 的 Renamed 事件驱动）。
    /// 目标路径已有记录时以旧记录为准覆盖，避免出现两条指向同一文件的归属。
    /// </summary>
    public bool RenamePath(string oldPath, string newPath)
    {
        if (string.IsNullOrEmpty(newPath)) return false;
        if (!_byPath.TryGetValue(oldPath, out var a)) return false;

        _byPath.Remove(oldPath);
        a.Path = newPath;
        _byPath[newPath] = a;
        return true;
    }

    /// <summary>删除指向已不存在分区的归属（分区被删除后调用）</summary>
    public int PruneMissingFences(IEnumerable<string> existingFenceIds)
    {
        var alive = new HashSet<string>(existingFenceIds, StringComparer.Ordinal);
        var dead = _byPath.Values
            .Where(a => !string.IsNullOrEmpty(a.FenceId) && !alive.Contains(a.FenceId))
            .ToList();

        foreach (var a in dead)
        {
            // 分区没了，但用户的手动意图要保留：降级为「手动移出」而非直接删记录
            if (a.Manual)
            {
                a.FenceId = string.Empty;
                a.Order = 0;
            }
            else
            {
                _byPath.Remove(a.Path);
            }
        }

        return dead.Count;
    }

    /// <summary>导出为可持久化的列表</summary>
    public List<FenceAssignment> ToList() => _byPath.Values.ToList();

    /// <summary>
    /// 整体替换全部归属 —— 撤销到快照时使用。
    ///
    /// 必须整体替换而非逐条合并：撤销的语义是「回到那一刻的状态」，
    /// 逐条合并会把操作后新增的归属残留下来。
    /// </summary>
    public void ReplaceAll(IEnumerable<FenceAssignment> assignments)
    {
        _byPath.Clear();
        foreach (var a in assignments)
        {
            if (!string.IsNullOrEmpty(a.Path)) _byPath[a.Path] = a;
        }
    }
}
