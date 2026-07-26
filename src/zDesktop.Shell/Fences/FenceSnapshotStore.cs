using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using zDesktop.Core.Fences;

namespace zDesktop.Shell.Fences;

/// <summary>
/// 一次批量操作前的桌面快照。
/// </summary>
public sealed class FenceSnapshot
{
    /// <summary>快照标识（文件名主干），按时间戳生成便于排序</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>人类可读的操作说明，如「一键整理」</summary>
    public string Label { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>操作前的图标坐标（路径 → 客户区物理像素）</summary>
    public Dictionary<string, SnapshotPoint> IconPositions { get; set; } = new();

    /// <summary>操作前的全部归属记录</summary>
    public List<FenceAssignment> Assignments { get; set; } = new();

    /// <summary>
    /// 本次操作**新建**的分区 Id。撤销时精确删掉这几个。
    ///
    /// <para>用户理解的撤销是「回到点应用之前」，那时这些分区并不存在。
    /// 少了这一步，撤销后桌面上会留下 0 归属的空框（真机全流程实测到的问题）。</para>
    ///
    /// <para><b>为什么记「新建的」而不是「操作前有哪些」</b>：后者要靠
    /// 「不在名单里就删」来推断，于是用户在整理之后手动建的分区，
    /// 撤销时会被连坐删掉 —— 那是他自己的东西，撤销无权碰。</para>
    ///
    /// <para><b>为什么可空</b>：<c>null</c> 表示快照产生于该字段存在之前，
    /// 无从判断，此时一个分区都不动；空列表则明确表示「本次没新建分区」。</para>
    /// </summary>
    public List<string>? CreatedFenceIds { get; set; }

    /// <summary>受影响的文件数（供 UI 展示「已整理 N 个文件」）</summary>
    public int AffectedCount { get; set; }
}

/// <summary>可序列化的坐标（IconPoint 是 readonly record struct，直接序列化不便）</summary>
public sealed class SnapshotPoint
{
    public int X { get; set; }
    public int Y { get; set; }

    public static SnapshotPoint From(IconPoint p) => new() { X = p.X, Y = p.Y };
    public IconPoint ToIconPoint() => new(X, Y);
}

/// <summary>
/// 快照存储 —— 设计案 v3.1 §3.1「执行前落盘快照，30 秒内可一键撤销，
/// 快照永久保留可随时回滚」与 §五 <c>snapshots/</c>。
///
/// <para><b>为什么必须先落盘再执行</b>：与还原账本同一个理由 ——
/// 一键整理可能移动几十个图标，执行到一半崩溃或被强杀时，
/// 只有磁盘上的快照能把桌面还原回去。内存里的备份救不了。</para>
/// </summary>
public sealed class FenceSnapshotStore
{
    private static readonly string DefaultDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "zDesktop", "snapshots");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false, // 快照可能上千项，不需要可读性
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>保留的快照数量上限（§五）</summary>
    public const int MaxSnapshots = 20;

    private readonly string _dir;

    public FenceSnapshotStore(string? directory = null)
    {
        _dir = directory ?? DefaultDir;
    }

    public string Directory => _dir;

    /// <summary>
    /// 落盘一份快照。**必须在真正修改桌面之前调用**。
    /// </summary>
    /// <returns>快照 Id；失败返回 null</returns>
    public string? Capture(string label,
        IReadOnlyDictionary<string, IconPoint> iconPositions,
        IEnumerable<FenceAssignment> assignments,
        int affectedCount,
        IEnumerable<string>? createdFenceIds = null)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_dir);

            var snapshot = new FenceSnapshot
            {
                Id = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}",
                Label = label,
                CreatedAt = DateTime.Now,
                Assignments = assignments.Select(Clone).ToList(),
                AffectedCount = affectedCount,
                // 新快照一律带上，哪怕是空的 —— 空列表和「没记录」不是一回事
                CreatedFenceIds = createdFenceIds?.ToList() ?? new List<string>(),
            };

            foreach (var (path, point) in iconPositions)
                snapshot.IconPositions[path] = SnapshotPoint.From(point);

            var file = Path.Combine(_dir, snapshot.Id + ".json");
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(tmp, file, overwrite: true);

            Prune();

            Console.WriteLine($"[Snapshot] 已落盘 {snapshot.Id}（{label}，{iconPositions.Count} 个图标）");
            return snapshot.Id;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Snapshot] 落盘失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>按时间倒序列出快照（不载入完整内容，只读元信息）</summary>
    public List<FenceSnapshot> List()
    {
        var result = new List<FenceSnapshot>();
        if (!System.IO.Directory.Exists(_dir)) return result;

        foreach (var file in System.IO.Directory.GetFiles(_dir, "*.json").OrderByDescending(f => f))
        {
            var s = LoadFile(file);
            if (s != null)
            {
                // 元信息列表不需要携带完整坐标表，清掉以免占内存
                s.IconPositions = new Dictionary<string, SnapshotPoint>();
                s.Assignments = new List<FenceAssignment>();
                s.CreatedFenceIds = null;
                result.Add(s);
            }
        }

        return result;
    }

    /// <summary>载入完整快照；不存在或损坏返回 null</summary>
    public FenceSnapshot? Load(string id)
    {
        var file = Path.Combine(_dir, id + ".json");
        return File.Exists(file) ? LoadFile(file) : null;
    }

    /// <summary>最近一次快照（供「撤销」按钮使用）</summary>
    public FenceSnapshot? Latest()
    {
        if (!System.IO.Directory.Exists(_dir)) return null;

        var newest = System.IO.Directory.GetFiles(_dir, "*.json")
            .OrderByDescending(f => f)
            .FirstOrDefault();

        return newest == null ? null : LoadFile(newest);
    }

    /// <summary>删除指定快照</summary>
    public bool Delete(string id)
    {
        try
        {
            var file = Path.Combine(_dir, id + ".json");
            if (!File.Exists(file)) return false;
            File.Delete(file);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static FenceSnapshot? LoadFile(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<FenceSnapshot>(File.ReadAllText(file), JsonOptions);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Snapshot] 读取失败 {Path.GetFileName(file)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>只保留最近 <see cref="MaxSnapshots"/> 份</summary>
    private void Prune()
    {
        try
        {
            var files = System.IO.Directory.GetFiles(_dir, "*.json")
                .OrderByDescending(f => f)
                .Skip(MaxSnapshots)
                .ToList();

            foreach (var f in files) File.Delete(f);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Snapshot] 清理旧快照失败: {ex.Message}");
        }
    }

    private static FenceAssignment Clone(FenceAssignment a) => new()
    {
        Path = a.Path,
        FenceId = a.FenceId,
        Order = a.Order,
        Manual = a.Manual,
    };
}
