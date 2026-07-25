using System.IO;
using Xunit;
using zDesktop.Shell.Desktop;

namespace zDesktop.Tests;

/// <summary>
/// T1-5 的还原账本部分（设计案 v3.1 §二 原则 4、§十）。
///
/// 账本存在的理由：<c>taskkill /F</c> 走 TerminateProcess，进程内钩子一律不执行，
/// 所以「改了什么」必须在动手之前落盘。这组测试守的就是「先记账、后动手」这条不变量。
/// </summary>
public class RestoreJournalTests : IDisposable
{
    private readonly string _dir;

    public RestoreJournalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "zdesktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响测试结论 */ }
    }

    private string JournalPath => Path.Combine(_dir, "restore.json");

    [Fact]
    public void 全新账本不应有待还原项()
    {
        var journal = RestoreJournal.Load(_dir);

        Assert.False(journal.HasPendingRestore());
        Assert.False(journal.Current.NativeIconsHidden);
    }

    [Fact]
    public void 标记隐藏图标层应当立即落盘()
    {
        var journal = RestoreJournal.Load(_dir);

        journal.MarkNativeIconsHidden();

        // 关键不变量：调用返回时文件必须已经在磁盘上，
        // 否则此刻被强杀就没人知道要还原
        Assert.True(File.Exists(JournalPath), "记账必须同步落盘，不能等到退出时再写");

        var reloaded = RestoreJournal.Load(_dir);
        Assert.True(reloaded.Current.NativeIconsHidden);
        Assert.True(reloaded.HasPendingRestore());
    }

    [Fact]
    public void 清除标记后应当无待还原项()
    {
        var journal = RestoreJournal.Load(_dir);
        journal.MarkNativeIconsHidden();

        journal.ClearNativeIconsHidden();

        Assert.False(RestoreJournal.Load(_dir).HasPendingRestore());
    }

    [Fact]
    public void 账本损坏时应当降级为空账本而不是抛异常()
    {
        File.WriteAllText(JournalPath, "{ 坏掉的 JSON ][");

        var journal = RestoreJournal.Load(_dir);

        // 损坏时宁可少还原，也不能阻断启动
        Assert.False(journal.HasPendingRestore());
    }

    [Fact]
    public void 壁纸与自动排列记录也应计入待还原()
    {
        var journal = RestoreJournal.Load(_dir);
        journal.Current.OriginalWallpaperPath = @"C:\Users\x\wall.jpg";
        journal.Save();

        var reloaded = RestoreJournal.Load(_dir);
        Assert.True(reloaded.HasPendingRestore());
        Assert.Equal(@"C:\Users\x\wall.jpg", reloaded.Current.OriginalWallpaperPath);

        var journal2 = RestoreJournal.Load(_dir);
        journal2.Current.OriginalWallpaperPath = null;
        journal2.Current.OriginalAutoArrange = true;
        journal2.Save();

        Assert.True(RestoreJournal.Load(_dir).HasPendingRestore());
    }

    [Fact]
    public void 还原后账本应当被清空()
    {
        var journal = RestoreJournal.Load(_dir);
        journal.MarkNativeIconsHidden();

        journal.RestoreAll();

        // 还原完成后不得残留待办，否则下次启动会重复还原
        Assert.False(journal.HasPendingRestore());
        Assert.False(RestoreJournal.Load(_dir).HasPendingRestore());
    }

    [Fact]
    public void 无待还原项时还原应当是安全的空操作()
    {
        var journal = RestoreJournal.Load(_dir);

        var ex = Record.Exception(() => journal.RestoreAll());

        Assert.Null(ex);
    }
}
