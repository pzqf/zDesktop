namespace zDesktop.Core.Fences;

/// <summary>一个建议创建的分区及其将要收纳的文件</summary>
/// <param name="Name">分区名</param>
/// <param name="Color">配色</param>
/// <param name="Rect">建议位置（DIP，相对显示器工作区）</param>
/// <param name="Rules">自动入区规则</param>
/// <param name="Files">将要归入的文件路径</param>
public sealed record ProposedFence(
    string Name,
    string Color,
    FenceRect Rect,
    IReadOnlyList<FenceRule> Rules,
    IReadOnlyList<string> Files);

/// <summary>一次整理建议的完整内容</summary>
/// <param name="Fences">建议创建的分区</param>
/// <param name="TotalFiles">涉及的文件总数</param>
/// <param name="UncategorizedCount">没有匹配到任何分区、将保持原位的文件数</param>
public sealed record OrganizeProposal(
    IReadOnlyList<ProposedFence> Fences,
    int TotalFiles,
    int UncategorizedCount);

/// <summary>
/// 首次运行的默认分区方案生成（设计案 v3.1 §六）。
///
/// <para><b>纯函数</b>：给定桌面文件清单与工作区尺寸，算出建议的分区与归属。
/// 不碰文件系统、不写任何配置 —— 引导流程的「预览效果」正是靠它做到
/// 「用户点应用之前，桌面一个像素都不变」（§二 原则 3）。</para>
///
/// <para>只为**确实有文件**的类别建分区：桌面上一张图片都没有却建个空的「图片」分区，
/// 用户第一眼看到的就是一个没用的框。</para>
/// </summary>
public static class FenceProposal
{
    /// <summary>
    /// 默认分区模板。
    ///
    /// <para><b>「应用」和「文件夹」必须排在最前</b>：真实桌面上最多的从来不是文档图片，
    /// 而是应用快捷方式和文件夹。M4 首次真机验证时，这套模板原本只有
    /// 文档/图片/影音/安装包四类，面对一个有 17 个项目的真实桌面**一个分区都没建出来**
    /// —— 因为那些项目几乎全是 .lnk 与文件夹。类别表必须照着真实桌面定，不能拍脑袋。</para>
    /// </summary>
    private static readonly (string Name, string Color, string[] Extensions, bool Directories)[] Templates =
    {
        ("应用", "#6c5ce7", new[] { ".lnk", ".url", ".exe", ".appref-ms" }, false),
        ("文件夹", "#f59e0b", Array.Empty<string>(), true),
        ("文档", "#3b82f6", new[] { ".doc", ".docx", ".pdf", ".txt", ".md", ".xlsx", ".xls", ".pptx", ".ppt", ".csv" }, false),
        ("图片", "#10b981", new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".psd" }, false),
        ("影音", "#ef4444", new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".mp3", ".flac", ".wav", ".m4a" }, false),
        ("压缩包", "#a78bfa", new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".iso", ".msi" }, false),
    };

    /// <summary>类别文件数达到此值才建分区，避免产生只装一个文件的框</summary>
    public const int MinFilesPerFence = 2;

    /// <summary>分区默认尺寸（DIP）</summary>
    private const double FenceWidth = 300;
    private const double FenceHeight = 320;
    private const double Gap = 24;

    /// <summary>距工作区右边缘的留白 —— 桌面图标习惯从左侧排起，分区放右侧不挡原有图标</summary>
    private const double RightMargin = 40;
    private const double TopMargin = 60;

    /// <summary>
    /// 生成整理建议。
    /// </summary>
    /// <param name="files">桌面文件快照</param>
    /// <param name="workAreaWidth">目标显示器工作区宽度（DIP）</param>
    /// <param name="workAreaHeight">目标显示器工作区高度（DIP）</param>
    public static OrganizeProposal Build(
        IReadOnlyCollection<FileSnapshot> files,
        double workAreaWidth,
        double workAreaHeight)
    {
        var buckets = new List<(int TemplateIndex, List<string> Files)>();

        // 一个文件只进第一个命中的类别，避免同时出现在多个分区里
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < Templates.Length; i++)
        {
            var (_, _, extensions, directories) = Templates[i];

            var matched = files
                .Where(f => !claimed.Contains(f.Path))
                .Where(f => directories
                    ? f.IsDirectory
                    : !f.IsDirectory && extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                .Select(f => f.Path)
                .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (matched.Count < MinFilesPerFence) continue;

            foreach (var p in matched) claimed.Add(p);
            buckets.Add((i, matched));
        }

        var rects = LayOut(buckets.Count, workAreaWidth, workAreaHeight);
        var proposed = new List<ProposedFence>();

        for (var i = 0; i < buckets.Count; i++)
        {
            var (templateIndex, bucketFiles) = buckets[i];
            var (name, color, extensions, directories) = Templates[templateIndex];

            // 文件夹分区没有扩展名规则可写，靠首次整理时的显式归属入区
            var rules = directories
                ? new List<FenceRule>()
                : new List<FenceRule> { new() { Kind = FenceRuleKind.Extension, Values = extensions.ToList() } };

            proposed.Add(new ProposedFence(name, color, rects[i], rules, bucketFiles));
        }

        var covered = proposed.Sum(p => p.Files.Count);
        return new OrganizeProposal(proposed, covered, files.Count - covered);
    }

    /// <summary>
    /// 排布建议的分区位置。
    ///
    /// 从工作区右上角起向左排列，排不下再换行；
    /// 工作区太窄时退化为单列并收窄分区，保证不会算出跑到屏幕外的坐标。
    /// </summary>
    private static List<FenceRect> LayOut(int count, double workAreaWidth, double workAreaHeight)
    {
        var result = new List<FenceRect>();
        if (count <= 0) return result;

        var width = FenceWidth;
        var usable = Math.Max(0, workAreaWidth - RightMargin * 2);

        // 一行放不下一个标准宽度的分区时，按可用宽度收窄
        if (usable < width) width = Math.Max(120, usable);

        var perRow = Math.Max(1, (int)((usable + Gap) / (width + Gap)));

        for (var i = 0; i < count; i++)
        {
            var col = i % perRow;
            var row = i / perRow;

            // 从右往左排：最右一列是第 0 个
            var right = workAreaWidth - RightMargin - col * (width + Gap);
            var x = Math.Max(0, right - width);
            var y = TopMargin + row * (FenceHeight + Gap);

            // 纵向排不下时压缩高度，绝不越出工作区
            var height = Math.Min(FenceHeight, Math.Max(120, workAreaHeight - y - TopMargin));

            result.Add(new FenceRect { X = x, Y = y, Width = width, Height = height });
        }

        return result;
    }
}
