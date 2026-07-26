using System.IO;
using Xunit;
using zDesktop.Core.Fences;
using zDesktop.Shell.Fences;

namespace zDesktop.Tests;

/// <summary>
/// T1-6（设计案 v3.1 §十）：撤销日志 —— 任意动作序列可完整回滚。
///
/// 守的是 §二 原则 3：一切修改必须可逆。一键整理可能移动几十个图标，
/// 没有可靠快照就等于让用户做一个无法后悔的操作。
/// </summary>
public class FenceSnapshotTests : IDisposable
{
    private readonly string _dir;

    public FenceSnapshotTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "zdesktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    private static Dictionary<string, IconPoint> Positions(params (string Path, int X, int Y)[] items)
        => items.ToDictionary(i => i.Path, i => new IconPoint(i.X, i.Y), StringComparer.OrdinalIgnoreCase);

    // ===== 落盘与读回 =====

    [Fact]
    public void 快照应当可落盘并完整读回()
    {
        var store = new FenceSnapshotStore(_dir);
        var assignments = new List<FenceAssignment>
        {
            new() { Path = @"C:\a.txt", FenceId = "f1", Order = 2, Manual = true },
        };

        var id = store.Capture("一键整理", Positions((@"C:\a.txt", 100, 200)), assignments, 1);

        Assert.NotNull(id);
        var loaded = store.Load(id!);
        Assert.NotNull(loaded);
        Assert.Equal("一键整理", loaded!.Label);
        Assert.Equal(new IconPoint(100, 200), loaded.IconPositions[@"C:\a.txt"].ToIconPoint());
        Assert.Equal("f1", loaded.Assignments[0].FenceId);
        Assert.True(loaded.Assignments[0].Manual);
    }

    [Fact]
    public void 快照应当立即落盘到磁盘()
    {
        // 与还原账本同一不变量：执行到一半崩溃时，只有磁盘上的快照能救回桌面
        var store = new FenceSnapshotStore(_dir);
        var id = store.Capture("测试", Positions((@"C:\a.txt", 1, 2)), Array.Empty<FenceAssignment>(), 1);

        Assert.True(File.Exists(Path.Combine(_dir, id + ".json")));
    }

    [Fact]
    public void 快照不应残留临时文件()
    {
        var store = new FenceSnapshotStore(_dir);
        store.Capture("测试", Positions((@"C:\a.txt", 1, 2)), Array.Empty<FenceAssignment>(), 1);

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void 快照应当深拷贝归属而不是持有引用()
    {
        // 否则后续对归属的修改会把「操作前的状态」一起改掉，撤销就撤了个寂寞
        var store = new FenceSnapshotStore(_dir);
        var live = new FenceAssignment { Path = @"C:\a.txt", FenceId = "before", Order = 0 };

        var id = store.Capture("测试", Positions((@"C:\a.txt", 1, 2)), new[] { live }, 1);
        live.FenceId = "after";

        Assert.Equal("before", store.Load(id!)!.Assignments[0].FenceId);
    }

    // ===== 列举与保留策略 =====

    [Fact]
    public void 最近一次快照应当可取回()
    {
        var store = new FenceSnapshotStore(_dir);
        store.Capture("第一次", Positions((@"C:\a.txt", 1, 1)), Array.Empty<FenceAssignment>(), 1);
        Thread.Sleep(5);
        store.Capture("第二次", Positions((@"C:\b.txt", 2, 2)), Array.Empty<FenceAssignment>(), 1);

        Assert.Equal("第二次", store.Latest()!.Label);
    }

    [Fact]
    public void 无快照时取最近一次应当返回空()
    {
        Assert.Null(new FenceSnapshotStore(_dir).Latest());
    }

    [Fact]
    public void 列举应当按时间倒序()
    {
        var store = new FenceSnapshotStore(_dir);
        store.Capture("旧", Positions((@"C:\a.txt", 1, 1)), Array.Empty<FenceAssignment>(), 1);
        Thread.Sleep(5);
        store.Capture("新", Positions((@"C:\b.txt", 2, 2)), Array.Empty<FenceAssignment>(), 1);

        var list = store.List();
        Assert.Equal("新", list[0].Label);
        Assert.Equal("旧", list[1].Label);
    }

    [Fact]
    public void 超过上限的旧快照应当被清理()
    {
        var store = new FenceSnapshotStore(_dir);
        for (var i = 0; i < FenceSnapshotStore.MaxSnapshots + 5; i++)
        {
            store.Capture($"第{i}次", Positions((@"C:\a.txt", i, i)), Array.Empty<FenceAssignment>(), 1);
            Thread.Sleep(2);
        }

        Assert.True(store.List().Count <= FenceSnapshotStore.MaxSnapshots,
            $"快照数 {store.List().Count} 超过上限 {FenceSnapshotStore.MaxSnapshots}");
    }

    [Fact]
    public void 损坏的快照文件应当被跳过而不是让列举崩溃()
    {
        var store = new FenceSnapshotStore(_dir);
        store.Capture("好的", Positions((@"C:\a.txt", 1, 1)), Array.Empty<FenceAssignment>(), 1);
        File.WriteAllText(Path.Combine(_dir, "99999999-999999-999.json"), "{ 坏 ][");

        var list = store.List();

        Assert.Contains(list, s => s.Label == "好的");
    }

    [Fact]
    public void 载入不存在的快照应当返回空()
    {
        Assert.Null(new FenceSnapshotStore(_dir).Load("不存在"));
    }

    // ===== 新建分区的记录（撤销要精确删掉它们）=====

    [Fact]
    public void 首次整理没有旧分区时新建列表应当照样落盘()
    {
        // 真机全流程实测到的坑：首次整理之前一个分区都没有，
        // 若用「列表为空 = 没记录」兜旧快照，恰好把最该清理的这一次排除掉，
        // 撤销后桌面上留下一个 0 归属的空框。
        var store = new FenceSnapshotStore(_dir);

        var id = store.Capture("首次整理", Positions((@"C:\a.txt", 1, 1)),
            Array.Empty<FenceAssignment>(), 1, new[] { "newfence" });

        var loaded = store.Load(id!);
        Assert.Equal(new[] { "newfence" }, loaded!.CreatedFenceIds);
    }

    [Fact]
    public void 没新建分区的整理应当记空列表而不是空值()
    {
        var store = new FenceSnapshotStore(_dir);

        var id = store.Capture("一键整理", Positions((@"C:\a.txt", 1, 1)),
            Array.Empty<FenceAssignment>(), 1, Array.Empty<string>());

        var loaded = store.Load(id!);
        Assert.NotNull(loaded!.CreatedFenceIds);   // 「本次没新建」
        Assert.Empty(loaded.CreatedFenceIds!);
    }

    [Fact]
    public void 该字段出现之前的旧快照应当读成空值以免误删分区()
    {
        // 旧快照无从判断哪些分区是当时新建的，撤销时就一个都别动
        var store = new FenceSnapshotStore(_dir);
        File.WriteAllText(Path.Combine(_dir, "20200101-000000-000.json"),
            """{"id":"20200101-000000-000","label":"旧","iconPositions":{},"assignments":[]}""");

        var loaded = store.Load("20200101-000000-000");

        Assert.NotNull(loaded);
        Assert.Null(loaded!.CreatedFenceIds);
    }

    // ===== 归属整体回滚 =====

    [Fact]
    public void 整体替换应当丢弃操作后新增的归属()
    {
        // 撤销的语义是「回到那一刻」，不是「合并」。
        // 逐条合并会把操作后新增的归属残留下来，桌面回不到原样。
        var model = new FenceAssignmentModel();
        model.Assign(@"C:\old.txt", "f1", 0, false);

        var snapshot = model.ToList();

        model.Assign(@"C:\new.txt", "f1", 1, false);
        Assert.Equal(2, model.Count);

        model.ReplaceAll(snapshot);

        Assert.Equal(1, model.Count);
        Assert.True(model.IsAssigned(@"C:\old.txt"));
        Assert.False(model.IsAssigned(@"C:\new.txt"));
    }

    [Fact]
    public void 整体替换应当还原手动标记()
    {
        var model = new FenceAssignmentModel();
        model.Assign(@"C:\a.txt", "f1", 0, manual: false);
        var snapshot = model.ToList().Select(a => new FenceAssignment
        {
            Path = a.Path, FenceId = a.FenceId, Order = a.Order, Manual = a.Manual,
        }).ToList();

        model.Assign(@"C:\a.txt", "f2", 5, manual: true);
        Assert.True(model.IsManual(@"C:\a.txt"));

        model.ReplaceAll(snapshot);

        Assert.False(model.IsManual(@"C:\a.txt"));
        Assert.Equal("f1", model.Find(@"C:\a.txt")!.FenceId);
    }
}
