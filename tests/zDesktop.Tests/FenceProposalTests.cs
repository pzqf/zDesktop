using Xunit;
using zDesktop.Core.Fences;

namespace zDesktop.Tests;

/// <summary>
/// M4 首次运行的默认分区方案（设计案 v3.1 §六）。
///
/// 守两条：**方案必须是纯计算**（用户点应用前不动桌面），
/// 以及**不产生没用的空分区**（桌面上一张图片都没有却建个「图片」框，
/// 用户第一眼看到的就是个摆设）。
/// </summary>
public class FenceProposalTests
{
    private static readonly DateTime Now = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Local);

    private const string DeskDir = @"C:\Users\x\Desktop\";

    private static FileSnapshot F(string name) => FileSnapshot.Of(DeskDir + name, Now);

    /// <summary>文件夹项</summary>
    private static FileSnapshot D(string name) => FileSnapshot.Of(DeskDir + name, Now, isDirectory: true);

    private static List<FileSnapshot> Files(params string[] names) => names.Select(F).ToList();

    // ===== 分类 =====

    [Fact]
    public void 应当按类别归集文件()
    {
        var files = Files("报告.docx", "预算.xlsx", "照片1.png", "照片2.jpg", "片子.mp4", "音乐.mp3");

        var p = FenceProposal.Build(files, 1920, 1032);

        Assert.Equal(3, p.Fences.Count);
        Assert.Contains(p.Fences, f => f.Name == "文档" && f.Files.Count == 2);
        Assert.Contains(p.Fences, f => f.Name == "图片" && f.Files.Count == 2);
        Assert.Contains(p.Fences, f => f.Name == "影音" && f.Files.Count == 2);
    }

    [Fact]
    public void 文件数不足的类别不应建分区()
    {
        // 只有一张图片时建「图片」分区没有意义
        var files = Files("报告.docx", "预算.xlsx", "孤零零.png");

        var p = FenceProposal.Build(files, 1920, 1032);

        Assert.Single(p.Fences);
        Assert.Equal("文档", p.Fences[0].Name);
    }

    [Fact]
    public void 没有任何可归类文件时应返回空方案()
    {
        var p = FenceProposal.Build(Files("怪文件.xyz", "另一个.abc"), 1920, 1032);

        Assert.Empty(p.Fences);
        Assert.Equal(0, p.TotalFiles);
        Assert.Equal(2, p.UncategorizedCount);
    }

    [Fact]
    public void 空桌面应返回空方案而不是抛异常()
    {
        var p = FenceProposal.Build(new List<FileSnapshot>(), 1920, 1032);

        Assert.Empty(p.Fences);
        Assert.Equal(0, p.TotalFiles);
        Assert.Equal(0, p.UncategorizedCount);
    }

    [Fact]
    public void 扩展名匹配应当忽略大小写()
    {
        var files = Files("A.PNG", "B.Jpg");

        var p = FenceProposal.Build(files, 1920, 1032);

        Assert.Single(p.Fences);
        Assert.Equal("图片", p.Fences[0].Name);
    }

    [Fact]
    public void 未归类文件数应当准确()
    {
        var files = Files("报告.docx", "预算.docx", "未知.xyz", "另一个.qqq");

        var p = FenceProposal.Build(files, 1920, 1032);

        Assert.Equal(2, p.TotalFiles);
        Assert.Equal(2, p.UncategorizedCount);
    }

    [Fact]
    public void 每个分区都应带上对应的扩展名规则()
    {
        var p = FenceProposal.Build(Files("a.png", "b.png"), 1920, 1032);

        var rule = Assert.Single(p.Fences[0].Rules);
        Assert.Equal(FenceRuleKind.Extension, rule.Kind);
        Assert.Contains(".png", rule.Values);
    }

    // ===== 排布 =====

    [Fact]
    public void 分区应当排在工作区内()
    {
        var files = Files("a.docx", "b.docx", "c.png", "d.png", "e.mp4", "f.mp3", "g.zip", "h.rar");

        var p = FenceProposal.Build(files, 1920, 1032);

        Assert.Equal(4, p.Fences.Count);
        foreach (var f in p.Fences)
        {
            Assert.True(f.Rect.X >= 0, $"{f.Name} 的 X={f.Rect.X} 越出左边界");
            Assert.True(f.Rect.Y >= 0, $"{f.Name} 的 Y={f.Rect.Y} 越出上边界");
            Assert.True(f.Rect.Right <= 1920, $"{f.Name} 右边缘 {f.Rect.Right} 越出工作区");
            Assert.True(f.Rect.Bottom <= 1032, $"{f.Name} 下边缘 {f.Rect.Bottom} 越出工作区");
        }
    }

    [Fact]
    public void 分区之间不应重叠()
    {
        var files = Files("a.docx", "b.docx", "c.png", "d.png", "e.mp4", "f.mp3", "g.zip", "h.rar");

        var p = FenceProposal.Build(files, 1920, 1032);

        for (var i = 0; i < p.Fences.Count; i++)
        {
            for (var j = i + 1; j < p.Fences.Count; j++)
            {
                var a = p.Fences[i].Rect;
                var b = p.Fences[j].Rect;
                var overlap = a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
                Assert.False(overlap, $"{p.Fences[i].Name} 与 {p.Fences[j].Name} 重叠");
            }
        }
    }

    [Fact]
    public void 分区应当靠右排布以避开左侧原有图标()
    {
        // 桌面图标习惯从左侧排起，分区放右侧才不会一上来就压在用户已有图标上
        var p = FenceProposal.Build(Files("a.docx", "b.docx"), 1920, 1032);

        Assert.True(p.Fences[0].Rect.X > 1920 / 2,
            $"首个分区 X={p.Fences[0].Rect.X} 落在左半屏，会压住原有图标");
    }

    [Fact]
    public void 极窄工作区仍应算出屏内坐标()
    {
        // 不能因为屏幕小就算出跑到屏幕外的分区
        var files = Files("a.docx", "b.docx", "c.png", "d.png");

        var p = FenceProposal.Build(files, 400, 300);

        foreach (var f in p.Fences)
        {
            Assert.True(f.Rect.X >= 0 && f.Rect.Right <= 400, $"{f.Name} 横向越界: {f.Rect}");
            Assert.True(f.Rect.Width > 0 && f.Rect.Height > 0, $"{f.Name} 尺寸非正: {f.Rect}");
        }
    }

    [Fact]
    public void 方案生成不应产生任何副作用()
    {
        // 纯计算：同样输入连算两次，结果必须完全一致（§二 原则 3 的基础）
        var files = Files("a.docx", "b.docx", "c.png", "d.png");

        var p1 = FenceProposal.Build(files, 1920, 1032);
        var p2 = FenceProposal.Build(files, 1920, 1032);

        Assert.Equal(p1.Fences.Count, p2.Fences.Count);
        for (var i = 0; i < p1.Fences.Count; i++)
        {
            Assert.Equal(p1.Fences[i].Name, p2.Fences[i].Name);
            Assert.Equal(p1.Fences[i].Rect.X, p2.Fences[i].Rect.X);
            Assert.Equal(p1.Fences[i].Rect.Y, p2.Fences[i].Rect.Y);
            Assert.Equal(p1.Fences[i].Files, p2.Fences[i].Files);
        }
    }

    [Fact]
    public void 分区内文件应当按名称排序()
    {
        var p = FenceProposal.Build(Files("z.png", "a.png", "m.png"), 1920, 1032);

        Assert.Equal(
            new[] { @"C:\Users\x\Desktop\a.png", @"C:\Users\x\Desktop\m.png", @"C:\Users\x\Desktop\z.png" },
            p.Fences[0].Files);
    }

    // ===== 真实桌面构成（M4 首次真机验证的教训）=====

    [Fact]
    public void 快捷方式应当归入应用分区()
    {
        // 真实桌面上最多的是 .lnk，而不是文档图片。
        // 最初的模板只有文档/图片/影音/安装包四类，面对真实桌面一个分区都建不出来。
        var p = FenceProposal.Build(Files("Chrome.lnk", "VSCode.lnk", "WeChat.lnk"), 1920, 1032);

        var fence = Assert.Single(p.Fences);
        Assert.Equal("应用", fence.Name);
        Assert.Equal(3, fence.Files.Count);
    }

    [Fact]
    public void 文件夹应当归入文件夹分区()
    {
        var p = FenceProposal.Build(new List<FileSnapshot> { D("项目"), D("素材"), D("归档") }, 1920, 1032);

        var fence = Assert.Single(p.Fences);
        Assert.Equal("文件夹", fence.Name);
        Assert.Equal(3, fence.Files.Count);
    }

    [Fact]
    public void 文件夹分区不应带扩展名规则()
    {
        // 文件夹没有扩展名可匹配，硬塞规则会让后续自动整理误吸文件
        var p = FenceProposal.Build(new List<FileSnapshot> { D("a"), D("b") }, 1920, 1032);

        Assert.Empty(p.Fences[0].Rules);
    }

    [Fact]
    public void 同名扩展的文件夹不应被当成文件归类()
    {
        // 名为 backup.zip 的文件夹不该进「压缩包」分区
        var p = FenceProposal.Build(new List<FileSnapshot> { D("backup.zip"), D("other.zip") }, 1920, 1032);

        Assert.Equal("文件夹", Assert.Single(p.Fences).Name);
    }

    [Fact]
    public void 一个文件只应进一个分区()
    {
        var p = FenceProposal.Build(Files("a.lnk", "b.lnk", "c.docx", "d.docx"), 1920, 1032);

        var all = p.Fences.SelectMany(f => f.Files).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void 典型真实桌面应当能产出分区()
    {
        // 以实测的真实桌面构成为样本：大量快捷方式 + 若干文件夹 + 零星文档
        var items = new List<FileSnapshot>
        {
            F("Google Chrome.lnk"), F("Visual Studio.lnk"), F("WeChat.lnk"), F("WPS Office.lnk"),
            F("Epic Games.lnk"), F("Typora.lnk"),
            D("feng zou"), D("项目"),
            F("报告.docx"),
        };

        var p = FenceProposal.Build(items, 1920, 1032);

        Assert.Contains(p.Fences, f => f.Name == "应用" && f.Files.Count == 6);
        Assert.Contains(p.Fences, f => f.Name == "文件夹" && f.Files.Count == 2);
        // 只有一个文档，不该为它单独建分区
        Assert.DoesNotContain(p.Fences, f => f.Name == "文档");
    }
}
