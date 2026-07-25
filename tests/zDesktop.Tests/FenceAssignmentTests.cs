using Xunit;
using zDesktop.Core.Fences;

namespace zDesktop.Tests;

/// <summary>
/// T1-3（设计案 v3.1 §十）：归属模型的状态迁移 —— 入区/出区/手动标记/孤儿清理。
///
/// 重点守 §4.2 决策 5：**手动放置优先于一切自动规则**。
/// 这条不变量一旦破了，用户会看到「我明明拖出来了，它又自己跑回去」。
/// </summary>
public class FenceAssignmentTests
{
    private static readonly DateTime Now = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Local);

    private static Fence MakeFence(string id, params FenceRule[] rules)
        => new() { Id = id, Name = id, Rules = rules.ToList() };

    private static FenceRule ExtRule(params string[] exts)
        => new() { Kind = FenceRuleKind.Extension, Values = exts.ToList() };

    private static FileSnapshot File(string name, DateTime? modified = null)
        => FileSnapshot.Of(@"C:\Users\x\Desktop\" + name, modified ?? Now);

    // ===== 入区 / 出区 =====

    [Fact]
    public void 新文件入区应当建立归属()
    {
        var m = new FenceAssignmentModel();

        Assert.True(m.Assign(@"C:\a.docx", "f1", 0, manual: false));

        var a = m.Find(@"C:\a.docx");
        Assert.NotNull(a);
        Assert.Equal("f1", a!.FenceId);
        Assert.False(a.Manual);
    }

    [Fact]
    public void 路径比较应当忽略大小写()
    {
        var m = new FenceAssignmentModel();
        m.Assign(@"C:\A.docx", "f1", 0, false);

        Assert.True(m.IsAssigned(@"c:\a.docx"));
    }

    [Fact]
    public void 拖入另一分区应当更新归属()
    {
        var m = new FenceAssignmentModel();
        m.Assign(@"C:\a.docx", "f1", 0, false);

        Assert.True(m.Assign(@"C:\a.docx", "f2", 3, manual: true));

        var a = m.Find(@"C:\a.docx")!;
        Assert.Equal("f2", a.FenceId);
        Assert.Equal(3, a.Order);
        Assert.True(a.Manual);
    }

    [Fact]
    public void 自动路径不得清除已有的手动标记()
    {
        // 用户手动放好之后，规则再次命中不能把「这是我亲手放的」这件事抹掉
        var m = new FenceAssignmentModel();
        m.Assign(@"C:\a.docx", "f1", 0, manual: true);

        m.Assign(@"C:\a.docx", "f1", 1, manual: false);

        Assert.True(m.Find(@"C:\a.docx")!.Manual);
    }

    [Fact]
    public void 手动拖出分区应当保留手动标记而不是删除记录()
    {
        var m = new FenceAssignmentModel();
        m.Assign(@"C:\a.docx", "f1", 0, false);

        Assert.True(m.Unassign(@"C:\a.docx", manual: true));

        var a = m.Find(@"C:\a.docx");
        Assert.NotNull(a);
        Assert.Equal(string.Empty, a!.FenceId);
        Assert.True(a.Manual);
    }

    [Fact]
    public void 非手动解除归属应当直接删除记录()
    {
        var m = new FenceAssignmentModel();
        m.Assign(@"C:\a.docx", "f1", 0, false);

        m.Unassign(@"C:\a.docx", manual: false);

        Assert.Null(m.Find(@"C:\a.docx"));
    }

    // ===== 手动优先（决策 5 核心）=====

    [Fact]
    public void 手动移出的文件不得被规则收回去()
    {
        var m = new FenceAssignmentModel();
        var fences = new List<Fence> { MakeFence("docs", ExtRule(".docx")) };

        // 规则先把它收进去
        m.ApplyRules(new[] { File("报告.docx") }, fences, Now);
        Assert.Equal("docs", m.Find(@"C:\Users\x\Desktop\报告.docx")!.FenceId);

        // 用户亲手拖了出来
        m.Unassign(@"C:\Users\x\Desktop\报告.docx", manual: true);

        // 再跑规则 —— 绝不能又把它塞回去
        var assigned = m.ApplyRules(new[] { File("报告.docx") }, fences, Now);

        Assert.Equal(0, assigned);
        Assert.Equal(string.Empty, m.Find(@"C:\Users\x\Desktop\报告.docx")!.FenceId);
    }

    [Fact]
    public void 手动放入别处的文件不得被规则搬走()
    {
        var m = new FenceAssignmentModel();
        var fences = new List<Fence> { MakeFence("docs", ExtRule(".docx")) };

        m.Assign(@"C:\Users\x\Desktop\报告.docx", "我的分区", 0, manual: true);
        m.ApplyRules(new[] { File("报告.docx") }, fences, Now);

        Assert.Equal("我的分区", m.Find(@"C:\Users\x\Desktop\报告.docx")!.FenceId);
    }

    [Fact]
    public void 规则不得搬动已归属的文件()
    {
        // 规则只负责收纳散落的新文件，不负责重新安排已安置好的
        var m = new FenceAssignmentModel();
        var fences = new List<Fence> { MakeFence("docs", ExtRule(".docx")) };

        m.Assign(@"C:\Users\x\Desktop\报告.docx", "别的分区", 0, manual: false);
        var assigned = m.ApplyRules(new[] { File("报告.docx") }, fences, Now);

        Assert.Equal(0, assigned);
        Assert.Equal("别的分区", m.Find(@"C:\Users\x\Desktop\报告.docx")!.FenceId);
    }

    // ===== 规则匹配 =====

    [Fact]
    public void 扩展名规则应当忽略大小写且容忍缺少点号()
    {
        var rule = new FenceRule { Kind = FenceRuleKind.Extension, Values = new() { "docx", ".PDF" } };

        Assert.True(FenceAssignmentModel.Matches(File("a.docx"), rule, Now));
        Assert.True(FenceAssignmentModel.Matches(File("b.DOCX"), rule, Now));
        Assert.True(FenceAssignmentModel.Matches(File("c.pdf"), rule, Now));
        Assert.False(FenceAssignmentModel.Matches(File("d.txt"), rule, Now));
    }

    [Fact]
    public void 正则规则应当匹配文件名()
    {
        var rule = new FenceRule { Kind = FenceRuleKind.NameRegex, Values = new() { @"^截图.*" } };

        Assert.True(FenceAssignmentModel.Matches(File("截图 2026-07-25.png"), rule, Now));
        Assert.False(FenceAssignmentModel.Matches(File("报告.docx"), rule, Now));
    }

    [Fact]
    public void 非法正则应当判为不匹配而不是抛异常()
    {
        // 规则由用户输入，写错了不能让整理流程崩掉
        var rule = new FenceRule { Kind = FenceRuleKind.NameRegex, Values = new() { "[未闭合" } };

        var ex = Record.Exception(() => FenceAssignmentModel.Matches(File("a.txt"), rule, Now));

        Assert.Null(ex);
        Assert.False(FenceAssignmentModel.Matches(File("a.txt"), rule, Now));
    }

    [Fact]
    public void 修改时间规则应当只匹配窗口内的文件()
    {
        var rule = new FenceRule { Kind = FenceRuleKind.ModifiedWithinDays, Values = new() { "7" } };

        Assert.True(FenceAssignmentModel.Matches(File("新.txt", Now.AddDays(-3)), rule, Now));
        Assert.False(FenceAssignmentModel.Matches(File("旧.txt", Now.AddDays(-30)), rule, Now));
    }

    [Fact]
    public void 一个文件命中多个分区时只进第一个()
    {
        var m = new FenceAssignmentModel();
        var fences = new List<Fence>
        {
            MakeFence("first", ExtRule(".txt")),
            MakeFence("second", ExtRule(".txt")),
        };

        m.ApplyRules(new[] { File("a.txt") }, fences, Now);

        Assert.Equal("first", m.Find(@"C:\Users\x\Desktop\a.txt")!.FenceId);
    }

    [Fact]
    public void 批量入区的序号应当递增而不是全为零()
    {
        var m = new FenceAssignmentModel();
        var fences = new List<Fence> { MakeFence("docs", ExtRule(".txt")) };

        m.ApplyRules(new[] { File("a.txt"), File("b.txt"), File("c.txt") }, fences, Now);

        var orders = m.InFence("docs").Select(a => a.Order).ToList();
        Assert.Equal(new[] { 0, 1, 2 }, orders);
    }

    [Fact]
    public void 无规则的分区不应吸入任何文件()
    {
        var m = new FenceAssignmentModel();
        var fences = new List<Fence> { MakeFence("空规则") };

        Assert.Equal(0, m.ApplyRules(new[] { File("a.txt") }, fences, Now));
    }

    // ===== 排序 =====

    [Fact]
    public void 按名称排序应当忽略持久化的序号()
    {
        var m = new FenceAssignmentModel();
        m.Assign(@"C:\z.txt", "f", 0, false);
        m.Assign(@"C:\a.txt", "f", 1, false);

        var files = new Dictionary<string, FileSnapshot>
        {
            [@"C:\z.txt"] = FileSnapshot.Of(@"C:\z.txt", Now),
            [@"C:\a.txt"] = FileSnapshot.Of(@"C:\a.txt", Now),
        };

        var ordered = m.OrderedPaths("f", FenceSortMode.Name, files);

        Assert.Equal(new[] { @"C:\a.txt", @"C:\z.txt" }, ordered);
    }

    [Fact]
    public void 手动排序应当保持序号顺序()
    {
        var m = new FenceAssignmentModel();
        m.Assign(@"C:\z.txt", "f", 0, false);
        m.Assign(@"C:\a.txt", "f", 1, false);

        var ordered = m.OrderedPaths("f", FenceSortMode.Manual, new Dictionary<string, FileSnapshot>());

        Assert.Equal(new[] { @"C:\z.txt", @"C:\a.txt" }, ordered);
    }

    [Fact]
    public void 按修改时间排序应当新的在前()
    {
        var m = new FenceAssignmentModel();
        m.Assign(@"C:\old.txt", "f", 0, false);
        m.Assign(@"C:\new.txt", "f", 1, false);

        var files = new Dictionary<string, FileSnapshot>
        {
            [@"C:\old.txt"] = FileSnapshot.Of(@"C:\old.txt", Now.AddDays(-10)),
            [@"C:\new.txt"] = FileSnapshot.Of(@"C:\new.txt", Now),
        };

        Assert.Equal(new[] { @"C:\new.txt", @"C:\old.txt" },
            m.OrderedPaths("f", FenceSortMode.Modified, files));
    }

    [Fact]
    public void 紧凑化应当消除序号空洞()
    {
        var m = new FenceAssignmentModel();
        m.Assign(@"C:\a.txt", "f", 0, false);
        m.Assign(@"C:\b.txt", "f", 7, false);
        m.Assign(@"C:\c.txt", "f", 99, false);

        m.Compact("f");

        Assert.Equal(new[] { 0, 1, 2 }, m.InFence("f").Select(a => a.Order));
    }

    // ===== 孤儿清理 =====

    [Fact]
    public void 指向已删除文件的归属应当被清理()
    {
        var m = new FenceAssignmentModel();
        m.Assign(@"C:\alive.txt", "f", 0, false);
        m.Assign(@"C:\deleted.txt", "f", 1, false);

        var pruned = m.PruneOrphans(new[] { @"C:\alive.txt" });

        Assert.Equal(1, pruned);
        Assert.True(m.IsAssigned(@"C:\alive.txt"));
        Assert.False(m.IsAssigned(@"C:\deleted.txt"));
    }

    [Fact]
    public void 重命名应当跟随更新归属()
    {
        var m = new FenceAssignmentModel();
        m.Assign(@"C:\old.txt", "f", 5, manual: true);

        Assert.True(m.RenamePath(@"C:\old.txt", @"C:\new.txt"));

        Assert.False(m.IsAssigned(@"C:\old.txt"));
        var a = m.Find(@"C:\new.txt")!;
        Assert.Equal("f", a.FenceId);
        Assert.Equal(5, a.Order);
        Assert.True(a.Manual);
    }

    [Fact]
    public void 重命名不存在的路径应当无副作用()
    {
        var m = new FenceAssignmentModel();
        Assert.False(m.RenamePath(@"C:\ghost.txt", @"C:\new.txt"));
        Assert.Equal(0, m.Count);
    }

    [Fact]
    public void 分区被删除后自动归属应当移除()
    {
        var m = new FenceAssignmentModel();
        m.Assign(@"C:\a.txt", "gone", 0, manual: false);

        m.PruneMissingFences(new[] { "alive" });

        Assert.False(m.IsAssigned(@"C:\a.txt"));
    }

    [Fact]
    public void 分区被删除后手动归属应当降级保留而不是丢失()
    {
        // 用户表达过的意图要保住：分区没了，但「这文件是我手动安置的」仍然成立
        var m = new FenceAssignmentModel();
        m.Assign(@"C:\a.txt", "gone", 0, manual: true);

        m.PruneMissingFences(new[] { "alive" });

        var a = m.Find(@"C:\a.txt");
        Assert.NotNull(a);
        Assert.Equal(string.Empty, a!.FenceId);
        Assert.True(a.Manual);
    }

    // ===== 往返 =====

    [Fact]
    public void 导出再导入应当保持一致()
    {
        var m = new FenceAssignmentModel();
        m.Assign(@"C:\a.txt", "f1", 2, manual: true);
        m.Assign(@"C:\b.txt", "f2", 0, manual: false);

        var restored = new FenceAssignmentModel(m.ToList());

        Assert.Equal(2, restored.Count);
        Assert.True(restored.IsManual(@"C:\a.txt"));
        Assert.Equal("f2", restored.Find(@"C:\b.txt")!.FenceId);
    }
}
