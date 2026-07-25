using Xunit;
using zDesktop.Core.Layout;

namespace zDesktop.Tests;

/// <summary>
/// M5：组件与分区的避让（设计案 v3.1 §3.1 主线二）。
///
/// 组件压在分区上会遮住分区里的图标 —— 那些是**原生图标**，
/// 用户点不到就等于零破坏契约被破坏。这组测试守的是这条边界。
/// </summary>
public class WidgetAvoidanceTests
{
    private const double W = 1920;
    private const double H = 1032;

    private static LayoutBox Fence(double x, double y, double w = 400, double h = 300) => new(x, y, w, h);
    private static LayoutBox Widget(double x, double y, double w = 280, double h = 200) => new(x, y, w, h);

    // ===== 重叠判定 =====

    [Fact]
    public void 完全不重叠时不应移动()
    {
        var widget = Widget(1500, 100);
        var fences = new[] { Fence(100, 100) };

        Assert.Equal(widget, WidgetAvoidance.Resolve(widget, fences, W, H));
    }

    [Fact]
    public void 轻微重叠不应干预()
    {
        // 用户可能就是想让组件压一点分区边角，不该粗暴弹开
        // 组件 280x200=56000；重叠 20x200=4000，占 7% < 30%
        var widget = Widget(480, 100);
        var fences = new[] { Fence(100, 100, 400, 300) }; // 右边缘 500

        Assert.Equal(widget, WidgetAvoidance.Resolve(widget, fences, W, H));
    }

    [Fact]
    public void 重叠超过阈值应当被推开()
    {
        // 组件几乎完全落在分区内
        var widget = Widget(150, 150);
        var fences = new[] { Fence(100, 100, 400, 300) };

        var result = WidgetAvoidance.Resolve(widget, fences, W, H);

        Assert.NotEqual(widget, result);
        Assert.True(result.OverlapArea(fences[0]) / result.Area <= WidgetAvoidance.OverlapThreshold,
            $"推开后仍重叠过多：{result}");
    }

    [Fact]
    public void 空分区列表应当原样返回()
    {
        var widget = Widget(150, 150);
        Assert.Equal(widget, WidgetAvoidance.Resolve(widget, Array.Empty<LayoutBox>(), W, H));
    }

    [Fact]
    public void 零面积组件不应触发计算()
    {
        var widget = new LayoutBox(100, 100, 0, 0);
        Assert.Equal(widget, WidgetAvoidance.Resolve(widget, new[] { Fence(100, 100) }, W, H));
    }

    // ===== 推开方向 =====

    [Fact]
    public void 应当选位移最小的一侧推开()
    {
        // 组件中心偏分区右侧 → 往右推最省力
        var fence = Fence(400, 300, 400, 300);           // 400..800, 300..600
        var widget = Widget(700, 380, 280, 200);          // 大部分在分区内，靠右

        var result = WidgetAvoidance.Resolve(widget, new[] { fence }, W, H);

        Assert.True(result.X >= fence.Right, $"应当被推到分区右侧，实际 {result}");
        Assert.Equal(widget.Y, result.Y); // 纵向不该动
    }

    [Fact]
    public void 推开后应当留出间隙()
    {
        var fence = Fence(400, 300, 400, 300);
        var widget = Widget(700, 380);

        var result = WidgetAvoidance.Resolve(widget, new[] { fence }, W, H);

        Assert.True(result.X >= fence.Right + WidgetAvoidance.Margin - 0.01,
            $"未留出 {WidgetAvoidance.Margin} 间隙：{result}");
    }

    [Fact]
    public void 推开方向应当优先保证组件留在工作区内()
    {
        // 分区贴着右边缘，往右推会出屏，应改往左推
        var fence = Fence(W - 400, 300, 400, 300);
        var widget = Widget(W - 350, 380, 280, 200);

        var result = WidgetAvoidance.Resolve(widget, new[] { fence }, W, H);

        Assert.True(result.X >= 0 && result.Right <= W, $"推出工作区：{result}");
        Assert.True(result.Y >= 0 && result.Bottom <= H, $"推出工作区：{result}");
    }

    [Fact]
    public void 结果永远不应越出工作区()
    {
        // 分区几乎铺满整屏，无论往哪推都会碰壁，此时必须夹回工作区
        var fence = Fence(0, 0, W, H);
        var widget = Widget(500, 400);

        var result = WidgetAvoidance.Resolve(widget, new[] { fence }, W, H);

        Assert.True(result.X >= 0, $"X 越界：{result}");
        Assert.True(result.Y >= 0, $"Y 越界：{result}");
        Assert.True(result.Right <= W, $"右越界：{result}");
        Assert.True(result.Bottom <= H, $"下越界：{result}");
    }

    // ===== 多分区 =====

    [Fact]
    public void 推开后撞上另一分区应当继续避让()
    {
        // 两个相邻分区，组件卡在第一个里，往右推会撞上第二个
        var fences = new[] { Fence(300, 300, 300, 300), Fence(620, 300, 300, 300) };
        var widget = Widget(350, 350, 280, 200);

        var result = WidgetAvoidance.Resolve(widget, fences, W, H);

        foreach (var f in fences)
        {
            Assert.True(result.OverlapArea(f) / result.Area <= WidgetAvoidance.OverlapThreshold,
                $"仍与分区 {f} 重叠过多：{result}");
        }
    }

    [Fact]
    public void 被多个分区夹住时也必须终止而不是死循环()
    {
        // 四面被围，不可能找到完全无重叠的位置；要求的是「返回一个结果」而不是挂死
        var fences = new[]
        {
            Fence(0, 0, W, 400),
            Fence(0, 400, W, 400),
            Fence(0, 800, W, 232),
        };
        var widget = Widget(500, 300);

        var result = WidgetAvoidance.Resolve(widget, fences, W, H);

        Assert.True(result.X >= 0 && result.Right <= W);
        Assert.True(result.Y >= 0 && result.Bottom <= H);
    }

    [Fact]
    public void 应当优先避让重叠最多的那个分区()
    {
        var fences = new[]
        {
            Fence(100, 100, 400, 300),  // 重叠很少
            Fence(300, 300, 500, 400),  // 重叠很多
        };
        var widget = Widget(400, 400, 280, 200);

        var result = WidgetAvoidance.Resolve(widget, fences, W, H);

        Assert.True(result.OverlapArea(fences[1]) / result.Area <= WidgetAvoidance.OverlapThreshold,
            $"未避开重叠最多的分区：{result}");
    }

    // ===== 判定接口 =====

    [Fact]
    public void 夹在分区与另一组件之间时必须真的挪开()
    {
        // 真机回归：分区 (40,40,500,400) 盖住时钟与日历，时钟先被推到下方，
        // 日历随后被「分区」与「已让开的时钟」夹住。
        // 旧的迭代推挤算法在两者之间振荡，4 轮后正好回到原点，
        // 结果日历一动不动、仍压在分区上。
        var fence = new LayoutBox(40, 40, 500, 400);
        var clock = new LayoutBox(80, 558, 280, 150);
        var calendar = new LayoutBox(80, 250, 280, 300);

        var result = WidgetAvoidance.Resolve(calendar, new[] { fence, clock }, W, H);

        Assert.True(result.OverlapArea(fence) / result.Area <= WidgetAvoidance.OverlapThreshold,
            $"仍压在分区上：{result}");
        Assert.True(result.OverlapArea(clock) / result.Area <= WidgetAvoidance.OverlapThreshold,
            $"与时钟叠在一起：{result}");
    }

    [Fact]
    public void 被同一分区推开的多个组件不应叠在一起()
    {
        // 只避让分区的话，两个组件会被推到同一个位置
        var fence = new LayoutBox(40, 40, 500, 400);
        var first = new LayoutBox(80, 80, 280, 150);
        var second = new LayoutBox(80, 250, 280, 300);

        var firstResolved = WidgetAvoidance.Resolve(first, new[] { fence }, W, H);
        var secondResolved = WidgetAvoidance.Resolve(second, new[] { fence, firstResolved }, W, H);

        Assert.True(secondResolved.OverlapArea(firstResolved) / secondResolved.Area
                    <= WidgetAvoidance.OverlapThreshold,
            $"两个组件重叠：{firstResolved} 与 {secondResolved}");
    }

    [Fact]
    public void 需要避让的判定应当与解算一致()
    {
        var fence = Fence(400, 300, 400, 300);

        Assert.True(WidgetAvoidance.NeedsAvoidance(Widget(450, 350), new[] { fence }));
        Assert.False(WidgetAvoidance.NeedsAvoidance(Widget(1500, 100), new[] { fence }));
    }

    [Fact]
    public void 重叠面积计算应当正确()
    {
        var a = new LayoutBox(0, 0, 100, 100);
        var b = new LayoutBox(50, 50, 100, 100);

        Assert.Equal(2500, a.OverlapArea(b));
        Assert.Equal(2500, b.OverlapArea(a));
        Assert.Equal(0, a.OverlapArea(new LayoutBox(200, 200, 50, 50)));
    }
}
