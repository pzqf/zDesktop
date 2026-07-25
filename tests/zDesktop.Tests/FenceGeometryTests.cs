using Xunit;
using zDesktop.Core.Fences;

namespace zDesktop.Tests;

/// <summary>
/// T1-2（设计案 v3.1 §十）：分区坐标解算。
///
/// 守的是 M2 spike 的核心结论：图标坐标必须落在 Shell 网格格点上，
/// 否则 Explorer 的「与网格对齐」会把它吸附走 —— spike 实测请求 (2030,98)
/// 实际落在 (2010,102)。
/// </summary>
public class FenceGeometryTests
{
    /// <summary>M2 spike 在真机上实测到的网格间距</summary>
    private static readonly GridSpec Grid = new(OriginX: 2, OriginY: 2, Cx: 76, Cy: 100);

    // ===== 对齐 =====

    [Theory]
    [InlineData(2, 2)]      // 已在格点上，保持不变
    [InlineData(3, 78)]     // 越过格点一点点 → 进到下一格
    [InlineData(78, 78)]
    [InlineData(79, 154)]
    public void 向上对齐应当落到不小于原值的最近格点(int value, int expected)
    {
        Assert.Equal(expected, FenceGeometry.AlignUp(value, Grid.OriginX, Grid.Cx));
    }

    [Fact]
    public void 向上对齐应当正确处理小于原点的坐标()
    {
        // 副屏可能位于主屏左侧，坐标为负；整数除法向零取整会算错，必须单独处理
        Assert.Equal(-74, FenceGeometry.AlignUp(-74, 2, 76));
        Assert.Equal(-74, FenceGeometry.AlignUp(-100, 2, 76));
        Assert.Equal(2, FenceGeometry.AlignUp(-73, 2, 76));
    }

    [Fact]
    public void 最近对齐应当吸附到较近的一侧()
    {
        Assert.Equal(2, FenceGeometry.AlignNearest(20, 2, 76));    // 距 2 更近
        Assert.Equal(78, FenceGeometry.AlignNearest(60, 2, 76));   // 距 78 更近
    }

    [Fact]
    public void 吸附应当复现spike实测的偏移()
    {
        // spike：请求 (2030,98) → Explorer 落在 (2010,102)
        // 以观测到的图标原点 (1934,2) 与间距 76x100 反推，格点计算必须给出同样结果
        var grid = new GridSpec(1934, 2, 76, 100);
        var snapped = FenceGeometry.SnapToGrid(new IconPoint(2030, 98), grid);

        Assert.Equal(new IconPoint(2010, 102), snapped);
    }

    // ===== 列数与容量 =====

    [Fact]
    public void 内容区宽度决定每行列数()
    {
        // 起点对齐到 2，可用宽度 304 → 正好 4 列
        var content = new IconRect(X: 2, Y: 2, Width: 304, Height: 400);
        Assert.Equal(4, FenceGeometry.ColumnsFor(content, Grid));
    }

    [Fact]
    public void 分区窄于一个格子时仍应至少一列()
    {
        // 用户可以把分区拖得很窄；返回 0 列会导致后续取模除零
        var content = new IconRect(2, 2, 10, 400);
        Assert.Equal(1, FenceGeometry.ColumnsFor(content, Grid));
    }

    [Fact]
    public void 网格间距非法时应当退化而不是除零()
    {
        var bad = new GridSpec(0, 0, 0, 0);
        var content = new IconRect(0, 0, 300, 300);

        Assert.Equal(1, FenceGeometry.ColumnsFor(content, bad));
        var p = FenceGeometry.SlotPosition(content, bad, 5);
        Assert.Equal(new IconPoint(0, 0), p);
    }

    [Fact]
    public void 容量应当等于行数乘列数()
    {
        // 高 400，起点 y=2，每行 100 → 4 行；宽 304 → 4 列
        var content = new IconRect(2, 2, 304, 400);
        Assert.Equal(16, FenceGeometry.CapacityFor(content, Grid));
    }

    [Fact]
    public void 内容区高度不足一行时容量为零()
    {
        var content = new IconRect(2, 2, 304, 50);
        Assert.Equal(0, FenceGeometry.CapacityFor(content, Grid));
    }

    // ===== 槽位解算 =====

    [Fact]
    public void 槽位应当逐列填充再换行()
    {
        var content = new IconRect(2, 2, 304, 400); // 4 列

        Assert.Equal(new IconPoint(2, 2), FenceGeometry.SlotPosition(content, Grid, 0));
        Assert.Equal(new IconPoint(78, 2), FenceGeometry.SlotPosition(content, Grid, 1));
        Assert.Equal(new IconPoint(230, 2), FenceGeometry.SlotPosition(content, Grid, 3));
        // 第 5 个换到第二行首列
        Assert.Equal(new IconPoint(2, 102), FenceGeometry.SlotPosition(content, Grid, 4));
    }

    [Fact]
    public void 所有槽位坐标都必须落在格点上()
    {
        // 内容区起点故意取非格点值，验证解算会把它推到格点
        var content = new IconRect(37, 53, 400, 500);

        for (var i = 0; i < 20; i++)
        {
            var p = FenceGeometry.SlotPosition(content, Grid, i);
            Assert.Equal(0, (p.X - Grid.OriginX) % Grid.Cx);
            Assert.Equal(0, (p.Y - Grid.OriginY) % Grid.Cy);
        }
    }

    [Fact]
    public void 槽位应当从内容区内部开始而不是外部()
    {
        var content = new IconRect(37, 53, 400, 500);
        var first = FenceGeometry.SlotPosition(content, Grid, 0);

        Assert.True(first.X >= content.X, $"首槽 X={first.X} 落在内容区左边界 {content.X} 之外");
        Assert.True(first.Y >= content.Y, $"首槽 Y={first.Y} 落在内容区上边界 {content.Y} 之外");
    }

    [Fact]
    public void 超出容量的槽位应当继续向下延伸而不是被截断()
    {
        // 是否隐藏/滚动由上层决定，几何层必须给出确定坐标
        var content = new IconRect(2, 2, 304, 200); // 容量 8
        var p = FenceGeometry.SlotPosition(content, Grid, 12);

        Assert.Equal(new IconPoint(2, 302), p);
    }

    [Fact]
    public void 负索引应当按零处理()
    {
        var content = new IconRect(2, 2, 304, 400);
        Assert.Equal(FenceGeometry.SlotPosition(content, Grid, 0),
                     FenceGeometry.SlotPosition(content, Grid, -5));
    }

    // ===== 重排（换分辨率 / 改分区尺寸）=====

    [Fact]
    public void 分区变窄后应当重新折行()
    {
        var wide = new IconRect(2, 2, 304, 400);   // 4 列
        var narrow = new IconRect(2, 2, 152, 400); // 2 列

        // 第 2 个图标：宽的时候在首行第三列，窄的时候换到第二行首列
        Assert.Equal(new IconPoint(154, 2), FenceGeometry.SlotPosition(wide, Grid, 2));
        Assert.Equal(new IconPoint(2, 102), FenceGeometry.SlotPosition(narrow, Grid, 2));
    }

    [Fact]
    public void 网格间距变化后坐标应当整体重算()
    {
        // 换 DPI / 改图标大小 → 间距变化。这正是「不存绝对坐标」的理由
        var content = new IconRect(2, 2, 400, 400);
        var dense = new GridSpec(2, 2, 60, 80);

        var a = FenceGeometry.SlotPosition(content, Grid, 5);
        var b = FenceGeometry.SlotPosition(content, dense, 5);

        Assert.NotEqual(a, b);
        Assert.Equal(0, (b.X - dense.OriginX) % dense.Cx);
        Assert.Equal(0, (b.Y - dense.OriginY) % dense.Cy);
    }

    [Fact]
    public void 批量解算应当按顺序给出互不重叠的坐标()
    {
        var content = new IconRect(2, 2, 304, 400);
        var paths = new[] { @"C:\a.txt", @"C:\b.txt", @"C:\c.txt", @"C:\d.txt", @"C:\e.txt" };

        var layout = FenceGeometry.SolveLayout(content, Grid, paths);

        Assert.Equal(5, layout.Count);
        Assert.Equal(5, layout.Values.Distinct().Count());
        Assert.Equal(new IconPoint(2, 2), layout[@"C:\a.txt"]);
        Assert.Equal(new IconPoint(2, 102), layout[@"C:\e.txt"]);
    }

    // ===== 内容区 =====

    [Fact]
    public void 内容区应当扣掉标题栏与内边距()
    {
        var fence = new IconRect(100, 200, 400, 300);
        var content = FenceGeometry.ContentAreaOf(fence, titleHeight: 32, padding: 8);

        Assert.Equal(108, content.X);
        Assert.Equal(240, content.Y);   // 200 + 32 + 8
        Assert.Equal(384, content.Width);
        Assert.Equal(252, content.Height);
    }

    [Fact]
    public void 分区被拖到极小时内容区尺寸不得为负()
    {
        var fence = new IconRect(0, 0, 10, 10);
        var content = FenceGeometry.ContentAreaOf(fence, titleHeight: 32, padding: 8);

        Assert.True(content.Width >= 0);
        Assert.True(content.Height >= 0);
    }
}
