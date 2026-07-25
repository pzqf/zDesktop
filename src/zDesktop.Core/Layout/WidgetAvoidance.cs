namespace zDesktop.Core.Layout;

/// <summary>矩形（DIP，相对显示器工作区）</summary>
public readonly record struct LayoutBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double Area => Math.Max(0, Width) * Math.Max(0, Height);

    /// <summary>与另一矩形的重叠面积</summary>
    public double OverlapArea(LayoutBox other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        return w <= 0 || h <= 0 ? 0 : w * h;
    }

    public LayoutBox MoveTo(double x, double y) => new(x, y, Width, Height);

    public override string ToString() => $"({X:F0},{Y:F0} {Width:F0}x{Height:F0})";
}

/// <summary>
/// 桌面组件与分区的避让（设计案 v3.1 §3.1 主线二）。
///
/// <para><b>为什么需要</b>：组件在覆盖层（图标层之上），分区背景在图标层之下，
/// 两者都占桌面空间且会视觉重叠。组件压在分区上会遮住分区里的图标 ——
/// 那些图标是原生图标，用户点不到就是零破坏契约被破坏。</para>
///
/// <para><b>规则</b>：组件与分区重叠面积超过组件自身面积的 30% 时，
/// 推到分区外缘；选位移最小的那一侧，让避让看起来像「贴边」而不是「弹飞」。
/// 轻微重叠（≤30%）不干预 —— 用户可能就是想让组件压一点边角。</para>
///
/// 纯函数，无 WPF 依赖。
/// </summary>
public static class WidgetAvoidance
{
    /// <summary>触发避让的重叠比例阈值</summary>
    public const double OverlapThreshold = 0.30;

    /// <summary>推出后与分区边缘的间隙（DIP）</summary>
    public const double Margin = 8;

    /// <summary>
    /// 求组件避让后的位置。不需要避让时原样返回。
    /// </summary>
    /// <param name="widget">组件当前矩形</param>
    /// <param name="fences">本屏上的分区矩形</param>
    /// <param name="workAreaWidth">工作区宽（DIP），用于夹住结果不越界</param>
    /// <param name="workAreaHeight">工作区高（DIP）</param>
    public static LayoutBox Resolve(LayoutBox widget, IReadOnlyList<LayoutBox> obstacles,
        double workAreaWidth, double workAreaHeight)
    {
        if (widget.Area <= 0 || obstacles.Count == 0) return widget;
        if (FindOffender(widget, obstacles) == null) return widget;

        // 不用「逐个推开」的迭代：推离 A 会撞上 B、推离 B 又退回 A，
        // 两个障碍物之间会来回振荡，迭代设上限后往往正好回到原点，
        // 表现就是「明明压着却没移动」（实测：日历卡在分区与时钟之间不动）。
        //
        // 改为一次性生成候选位置再整体评分：候选来自「贴住每个障碍物四条边」，
        // 按「总重叠最小、位移最小」挑选。这样天然考虑了所有障碍物，不会振荡。
        var best = widget;
        var bestScore = Score(widget, obstacles);

        foreach (var candidate in Candidates(widget, obstacles, workAreaWidth, workAreaHeight))
        {
            var score = Score(candidate, obstacles);

            // 总重叠更小者优先；重叠相当（差 <1%）时取位移更小的
            var overlapBetter = score.Overlap < bestScore.Overlap - 0.01;
            var overlapSimilar = Math.Abs(score.Overlap - bestScore.Overlap) <= 0.01;
            var closer = Displacement(widget, candidate) < Displacement(widget, best);

            if (overlapBetter || (overlapSimilar && closer))
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>生成候选位置：贴住每个障碍物的四条外缘，且完整落在工作区内</summary>
    private static IEnumerable<LayoutBox> Candidates(LayoutBox widget, IReadOnlyList<LayoutBox> obstacles,
        double workAreaWidth, double workAreaHeight)
    {
        var maxX = Math.Max(0, workAreaWidth - widget.Width);
        var maxY = Math.Max(0, workAreaHeight - widget.Height);

        foreach (var o in obstacles)
        {
            var positions = new (double X, double Y)[]
            {
                (o.X - widget.Width - Margin, widget.Y),  // 贴左
                (o.Right + Margin,            widget.Y),  // 贴右
                (widget.X, o.Y - widget.Height - Margin), // 贴上
                (widget.X, o.Bottom + Margin),            // 贴下
            };

            foreach (var (x, y) in positions)
            {
                // 越界的候选直接丢弃，而不是夹回来 ——
                // 夹回来会产生一个「贴着屏幕边缘但仍压在障碍物上」的伪解
                if (x < -0.01 || y < -0.01 || x > maxX + 0.01 || y > maxY + 0.01) continue;
                yield return widget.MoveTo(Clamp(x, 0, maxX), Clamp(y, 0, maxY));
            }
        }

        // 所有候选都越界时的兜底：夹到工作区四角，至少保证组件可见可操作
        yield return widget.MoveTo(0, 0);
        yield return widget.MoveTo(maxX, 0);
        yield return widget.MoveTo(0, maxY);
        yield return widget.MoveTo(maxX, maxY);
    }

    /// <summary>候选位置的重叠评分：与全部障碍物的重叠面积占组件面积的比例之和</summary>
    private static (double Overlap, int Count) Score(LayoutBox box, IReadOnlyList<LayoutBox> obstacles)
    {
        var total = 0.0;
        var count = 0;

        foreach (var o in obstacles)
        {
            var ratio = box.OverlapArea(o) / box.Area;
            total += ratio;
            if (ratio > OverlapThreshold) count++;
        }

        return (total, count);
    }

    private static double Displacement(LayoutBox from, LayoutBox to)
        => Math.Abs(to.X - from.X) + Math.Abs(to.Y - from.Y);

    /// <summary>组件是否与任一分区重叠过多</summary>
    public static bool NeedsAvoidance(LayoutBox widget, IReadOnlyList<LayoutBox> fences)
        => FindOffender(widget, fences) != null;

    private static LayoutBox? FindOffender(LayoutBox widget, IReadOnlyList<LayoutBox> fences)
    {
        if (widget.Area <= 0) return null;

        LayoutBox? worst = null;
        var worstOverlap = 0.0;

        foreach (var fence in fences)
        {
            var overlap = widget.OverlapArea(fence);
            if (overlap / widget.Area <= OverlapThreshold) continue;

            if (overlap > worstOverlap)
            {
                worstOverlap = overlap;
                worst = fence;
            }
        }

        return worst;
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;
}
