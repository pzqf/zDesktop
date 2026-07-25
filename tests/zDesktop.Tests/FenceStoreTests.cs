using System.IO;
using Xunit;
using zDesktop.Core.Fences;
using zDesktop.Shell.Fences;

namespace zDesktop.Tests;

/// <summary>
/// T1-5 的分区配置部分（设计案 v3.1 §五、§七 失败降级矩阵）。
/// </summary>
public class FenceStoreTests : IDisposable
{
    private readonly string _dir;

    public FenceStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "zdesktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    private static Fence MakeFence(string id) => new()
    {
        Id = id,
        MonitorKey = @"\\.\DISPLAY1",
        Name = "工作",
        Color = "#6c5ce7",
        Rect = new FenceRect { X = 40, Y = 60, Width = 420, Height = 300 },
        SortMode = FenceSortMode.Name,
        Rules = new List<FenceRule>
        {
            new() { Kind = FenceRuleKind.Extension, Values = new() { ".docx", ".xlsx" } },
        },
    };

    [Fact]
    public void 文件不存在时应当返回空配置而不是null()
    {
        // 「还没有分区」是正常状态，调用方不该被迫写空判断
        var config = new FenceStore(_dir).Load();

        Assert.NotNull(config);
        Assert.Empty(config.Fences);
        Assert.Empty(config.Assignments);
    }

    [Fact]
    public void 保存后加载应当还原全部字段()
    {
        var store = new FenceStore(_dir);
        var config = new FenceConfig();
        config.Fences.Add(MakeFence("f1"));
        config.Assignments.Add(new FenceAssignment
        {
            Path = @"C:\Users\x\Desktop\报告.docx", FenceId = "f1", Order = 3, Manual = true,
        });

        store.Save(config);
        var loaded = store.Load();

        var fence = Assert.Single(loaded.Fences);
        Assert.Equal("f1", fence.Id);
        Assert.Equal("工作", fence.Name);
        Assert.Equal(@"\\.\DISPLAY1", fence.MonitorKey);
        Assert.Equal(40, fence.Rect.X);
        Assert.Equal(420, fence.Rect.Width);
        Assert.Equal(FenceSortMode.Name, fence.SortMode);
        Assert.Equal(FenceRuleKind.Extension, fence.Rules[0].Kind);
        Assert.Contains(".docx", fence.Rules[0].Values);

        var a = Assert.Single(loaded.Assignments);
        Assert.Equal("f1", a.FenceId);
        Assert.Equal(3, a.Order);
        Assert.True(a.Manual);
    }

    [Fact]
    public void 配置损坏时应当备份并按空配置启动()
    {
        var path = Path.Combine(_dir, "fences.json");
        File.WriteAllText(path, "{ 坏掉的 JSON ][");

        var config = new FenceStore(_dir).Load();

        Assert.Empty(config.Fences);
        Assert.True(File.Exists(path + ".bak"), "损坏的配置必须先备份再降级，否则用户数据直接丢失");
    }

    [Fact]
    public void 缺少Id的分区应当被剔除()
    {
        var store = new FenceStore(_dir);
        var config = new FenceConfig();
        config.Fences.Add(MakeFence("good"));
        config.Fences.Add(new Fence { Id = "  ", Name = "坏的" });

        store.Save(config);

        Assert.Single(store.Load().Fences);
    }

    [Fact]
    public void 尺寸过小的分区应当被修复为可操作尺寸()
    {
        // 尺寸为 0 的分区点不中也拖不动，用户会以为程序坏了
        var store = new FenceStore(_dir);
        var config = new FenceConfig();
        var f = MakeFence("f1");
        f.Rect = new FenceRect { X = 0, Y = 0, Width = 0, Height = 0 };
        config.Fences.Add(f);

        store.Save(config);
        var loaded = store.Load();

        Assert.True(loaded.Fences[0].Rect.Width >= 40);
        Assert.True(loaded.Fences[0].Rect.Height >= 40);
    }

    [Fact]
    public void 指向已删除分区的自动归属应当被清理()
    {
        var store = new FenceStore(_dir);
        var config = new FenceConfig();
        config.Fences.Add(MakeFence("alive"));
        config.Assignments.Add(new FenceAssignment { Path = @"C:\a.txt", FenceId = "gone", Manual = false });

        store.Save(config);

        Assert.Empty(store.Load().Assignments);
    }

    [Fact]
    public void 指向已删除分区的手动归属应当降级保留()
    {
        // 分区没了，但「这文件是我手动安置的」这个意图必须留住
        var store = new FenceStore(_dir);
        var config = new FenceConfig();
        config.Fences.Add(MakeFence("alive"));
        config.Assignments.Add(new FenceAssignment { Path = @"C:\a.txt", FenceId = "gone", Manual = true });

        store.Save(config);
        var loaded = store.Load();

        var a = Assert.Single(loaded.Assignments);
        Assert.Equal(string.Empty, a.FenceId);
        Assert.True(a.Manual);
    }

    [Fact]
    public void 保存不应留下临时文件()
    {
        // 采用「写临时文件再替换」以防写一半断电，替换后不得残留 .tmp
        var store = new FenceStore(_dir);
        var config = new FenceConfig();
        config.Fences.Add(MakeFence("f1"));

        store.Save(config);

        Assert.False(File.Exists(Path.Combine(_dir, "fences.json.tmp")));
        Assert.True(File.Exists(Path.Combine(_dir, "fences.json")));
    }

    [Fact]
    public void 重复保存应当覆盖而不是追加()
    {
        var store = new FenceStore(_dir);

        var c1 = new FenceConfig();
        c1.Fences.Add(MakeFence("f1"));
        store.Save(c1);

        var c2 = new FenceConfig();
        c2.Fences.Add(MakeFence("f2"));
        store.Save(c2);

        var loaded = store.Load();
        Assert.Single(loaded.Fences);
        Assert.Equal("f2", loaded.Fences[0].Id);
    }

    [Fact]
    public void 保存后版本号应当是当前版本()
    {
        var store = new FenceStore(_dir);
        store.Save(new FenceConfig { Version = 0 });

        Assert.Equal(FenceConfig.CurrentVersion, store.Load().Version);
    }
}
