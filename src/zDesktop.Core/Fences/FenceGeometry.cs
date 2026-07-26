namespace zDesktop.Core.Fences;

/// <summary>图标坐标（物理像素，ListView 客户区坐标系）</summary>
public readonly record struct IconPoint(int X, int Y)
{
    public override string ToString() => $"({X},{Y})";
}

/// <summary>图标空间中的矩形（物理像素）</summary>
public readonly record struct IconRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(IconPoint p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;
}

/// <summary>
/// Shell 图标网格规格。
///
/// <para>间距必须用 <c>LVM_GETITEMSPACING</c> **运行时查询**（M2 spike 实测本机 76×100），
/// 它随 DPI 与图标大小变化，硬编码必然在别的机器上错位。</para>
/// </summary>
public readonly record struct GridSpec(int OriginX, int OriginY, int Cx, int Cy)
{
    /// <summary>间距是否有效（防御除零）</summary>
    public bool IsValid => Cx > 0 && Cy > 0;
}

/// <summary>
/// 分区内图标坐标解算（设计案 v3.1 §4.2 决策 1）。
///
/// <para><b>为什么需要它</b>：持久化的是「路径 → (分区, 序号)」而不是绝对坐标，
/// 所以每次分区移动/改尺寸/换分辨率/换缩放，都要由「分区几何 + 序号 + 网格」
/// 重新算出绝对坐标再写回 Explorer。</para>
///
/// <para><b>为什么必须对齐格点</b>：M2 spike 实测，Explorer 的「将图标与网格对齐」
/// 开启时会把写入的坐标吸附到最近格点 —— 请求 (2030,98) 实际落在 (2010,102)。
/// 我们不强关该选项（那是改用户可见设置），而是主动把坐标算在格点上。</para>
///
/// 本类为纯函数，无副作用、不依赖 WPF/Win32，对应验收门槛 T1-2。
/// </summary>
public static class FenceGeometry
{
    /// <summary>
    /// 求不小于 <paramref name="value"/> 的最小格点坐标。
    ///
    /// 用于把分区内容区的左/上边缘推到格点上，作为第一个槽位的起点。
    /// </summary>
    public static int AlignUp(int value, int origin, int step)
    {
        if (step <= 0) return value;

        var delta = value - origin;
        // C# 的整数除法向零取整，负数需单独处理，否则 -1/76 == 0 会算成「已对齐」
        var cells = delta >= 0
            ? (delta + step - 1) / step
            : -((-delta) / step);

        return origin + cells * step;
    }

    /// <summary>求最接近 <paramref name="value"/> 的格点坐标（用于把用户拖拽落点吸附到格）</summary>
    public static int AlignNearest(int value, int origin, int step)
    {
        if (step <= 0) return value;
        var delta = (double)(value - origin) / step;
        return origin + (int)Math.Round(delta, MidpointRounding.AwayFromZero) * step;
    }

    /// <summary>把一个点吸附到最近格点</summary>
    public static IconPoint SnapToGrid(IconPoint p, GridSpec grid)
        => grid.IsValid
            ? new IconPoint(AlignNearest(p.X, grid.OriginX, grid.Cx), AlignNearest(p.Y, grid.OriginY, grid.Cy))
            : p;

    /// <summary>
    /// 内容区一行能放几个图标。
    /// 至少返回 1 —— 分区被拖得比一个格子还窄时，仍然要能放下图标而不是算出 0 列导致除零。
    /// </summary>
    public static int ColumnsFor(IconRect content, GridSpec grid)
    {
        if (!grid.IsValid) return 1;

        var firstX = AlignUp(content.X, grid.OriginX, grid.Cx);
        if (firstX >= content.Right) return 1;

        // 从首个格点起，还能容纳多少个整格
        var usable = content.Right - firstX;
        return Math.Max(1, usable / grid.Cx);
    }

    /// <summary>内容区不出现纵向溢出时能放下的图标总数</summary>
    public static int CapacityFor(IconRect content, GridSpec grid)
    {
        if (!grid.IsValid) return int.MaxValue;

        var firstY = AlignUp(content.Y, grid.OriginY, grid.Cy);
        if (firstY >= content.Bottom) return 0;

        var rows = Math.Max(0, (content.Bottom - firstY) / grid.Cy);
        return rows * ColumnsFor(content, grid);
    }

    /// <summary>
    /// 求第 <paramref name="slotIndex"/> 个槽位的图标坐标。
    ///
    /// 超出容量时继续向下延伸（不 clamp）—— 是否隐藏/滚动由调用方决定，
    /// 几何层只负责算出确定的位置。
    /// </summary>
    public static IconPoint SlotPosition(IconRect content, GridSpec grid, int slotIndex)
    {
        if (slotIndex < 0) slotIndex = 0;
        if (!grid.IsValid) return new IconPoint(content.X, content.Y);

        var cols = ColumnsFor(content, grid);
        var col = slotIndex % cols;
        var row = slotIndex / cols;

        var firstX = AlignUp(content.X, grid.OriginX, grid.Cx);
        var firstY = AlignUp(content.Y, grid.OriginY, grid.Cy);

        return new IconPoint(firstX + col * grid.Cx, firstY + row * grid.Cy);
    }

    /// <summary>
    /// 批量解算：按给定顺序把路径映射到坐标。
    ///
    /// 调用方保证 <paramref name="orderedPaths"/> 已按 Order/排序模式排好。
    /// </summary>
    public static Dictionary<string, IconPoint> SolveLayout(
        IconRect content, GridSpec grid, IReadOnlyList<string> orderedPaths)
    {
        var result = new Dictionary<string, IconPoint>(orderedPaths.Count, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < orderedPaths.Count; i++)
            result[orderedPaths[i]] = SlotPosition(content, grid, i);

        return result;
    }

    /// <summary>
    /// 求装下指定数量图标所需的分区尺寸。
    ///
    /// <para><b>为什么必须有这个</b>：分区尺寸若用固定常量，图标一多就会溢出到框外。
    /// 实测一个 300×320 的「应用」分区容量只有 4 个，而桌面上有 28 个快捷方式，
    /// 最后一行底部超出分区 1180 像素 —— 看起来就是「图标跑到框外面去了」。</para>
    ///
    /// <para><b>为什么要留一格余量</b>：槽位必须落在 Shell 全局格点上，
    /// 而内容区左上角通常不在格点上，对齐会吃掉最多一整格。
    /// 不留余量就会出现「按 3 列算的宽度实际只排得下 2 列」。</para>
    ///
    /// <para>原生图标无法滚动（它们由 Explorer 渲染，我们只能改坐标不能裁剪），
    /// 所以设计案 §3.1 写的「超出容器高度时容器内滚动」在 Plan A 下不可实现，
    /// 只能靠把分区撑到足够大。</para>
    /// </summary>
    /// <param name="itemCount">要装下的图标数</param>
    /// <param name="grid">Shell 图标网格</param>
    /// <param name="titleHeight">标题栏高度</param>
    /// <param name="padding">内边距</param>
    /// <param name="maxWidth">可用宽度上限</param>
    /// <param name="maxHeight">可用高度上限</param>
    public static (int Width, int Height, int Columns) RequiredSize(
        int itemCount, GridSpec grid, int titleHeight, int padding, int maxWidth, int maxHeight)
    {
        if (itemCount <= 0 || !grid.IsValid)
            return (Math.Min(maxWidth, 240), Math.Min(maxHeight, titleHeight + padding * 2 + 100), 1);

        // 对齐余量：内容区起点一般不在格点上，两个方向各留一格
        var chromeW = padding * 2 + grid.Cx;
        var chromeH = titleHeight + padding * 2 + grid.Cy;

        var maxCols = Math.Max(1, (maxWidth - chromeW) / grid.Cx);
        var maxRows = Math.Max(1, (maxHeight - chromeH) / grid.Cy);

        // 先按近似方形起步，视觉上比又高又窄的一条好看得多
        var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(itemCount)));

        // 行数放不下就加宽
        while (cols < maxCols && (int)Math.Ceiling(itemCount / (double)cols) > maxRows)
            cols++;

        cols = Math.Clamp(cols, 1, maxCols);
        var rows = Math.Max(1, (int)Math.Ceiling(itemCount / (double)cols));

        var width = Math.Min(maxWidth, chromeW + cols * grid.Cx);
        var height = Math.Min(maxHeight, chromeH + rows * grid.Cy);

        return (width, height, cols);
    }

    /// <summary>
    /// 由分区矩形求内容区（扣掉标题栏与内边距）。
    /// </summary>
    /// <param name="fence">分区矩形（物理像素，图标空间）</param>
    /// <param name="titleHeight">标题栏高度（物理像素）</param>
    /// <param name="padding">内边距（物理像素）</param>
    public static IconRect ContentAreaOf(IconRect fence, int titleHeight, int padding)
    {
        var x = fence.X + padding;
        var y = fence.Y + titleHeight + padding;
        var w = Math.Max(0, fence.Width - padding * 2);
        var h = Math.Max(0, fence.Height - titleHeight - padding * 2);
        return new IconRect(x, y, w, h);
    }
}
