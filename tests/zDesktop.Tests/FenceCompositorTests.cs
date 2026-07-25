using System.IO;
using Xunit;
using zDesktop.Shell.Desktop;
using zDesktop.Shell.Fences;

namespace zDesktop.Tests;

/// <summary>
/// M3-C 合成器的关键不变量（设计案 v3.1 §4.3）。
///
/// 守的是探针在真机上暴露的两个坑：**自我叠加**与**第三方壁纸工具**。
/// 本机实测有元气桌面在管理壁纸（E:\元气壁纸缓存\），这不是假想场景。
/// </summary>
public class FenceCompositorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _cacheDir;

    public FenceCompositorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "zdesktop-tests", Guid.NewGuid().ToString("N"));
        _cacheDir = Path.Combine(_dir, "fence-bg");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    private FenceCompositor MakeCompositor(out RestoreJournal journal)
    {
        journal = RestoreJournal.Load(_dir);
        return new FenceCompositor(journal, _cacheDir);
    }

    // ===== 自我叠加防护 =====

    [Fact]
    public void 缓存目录下的图片应当被识别为自己的产物()
    {
        using var c = MakeCompositor(out _);

        Assert.True(c.IsOurOutput(Path.Combine(_cacheDir, "bg-DISPLAY1.jpg")));
    }

    [Fact]
    public void 用户壁纸不应被识别为自己的产物()
    {
        using var c = MakeCompositor(out _);

        // 真机实测路径：元气桌面的壁纸缓存
        Assert.False(c.IsOurOutput(@"E:\元气壁纸缓存\img\11618d82d0f411a84bf007425085ccbb.jpg"));
        Assert.False(c.IsOurOutput(@"C:\Windows\Web\Wallpaper\Windows\img0.jpg"));
    }

    [Fact]
    public void 空路径不应被识别为自己的产物()
    {
        using var c = MakeCompositor(out _);

        Assert.False(c.IsOurOutput(null));
        Assert.False(c.IsOurOutput(string.Empty));
    }

    // ===== 底图记账 =====

    [Fact]
    public void 首次记录的壁纸应当可回读()
    {
        var journal = RestoreJournal.Load(_dir);
        journal.RememberWallpaper("MON1", @"E:\wall\a.jpg");

        Assert.Equal(@"E:\wall\a.jpg", RestoreJournal.Load(_dir).GetRememberedWallpaper("MON1"));
    }

    [Fact]
    public void 第三方工具换壁纸后记录应当跟随更新()
    {
        // 元气桌面轮换壁纸后，底图要跟着换成新的用户壁纸，
        // 否则分区会画在一张早已不用的旧图上
        var journal = RestoreJournal.Load(_dir);
        journal.RememberWallpaper("MON1", @"E:\wall\old.jpg");
        journal.RememberWallpaper("MON1", @"E:\wall\new.jpg");

        Assert.Equal(@"E:\wall\new.jpg", RestoreJournal.Load(_dir).GetRememberedWallpaper("MON1"));
    }

    [Fact]
    public void 每屏的壁纸记录应当相互独立()
    {
        var journal = RestoreJournal.Load(_dir);
        journal.RememberWallpaper("MON1", @"E:\wall\a.jpg");
        journal.RememberWallpaper("MON2", @"E:\wall\b.jpg");

        var reloaded = RestoreJournal.Load(_dir);
        Assert.Equal(@"E:\wall\a.jpg", reloaded.GetRememberedWallpaper("MON1"));
        Assert.Equal(@"E:\wall\b.jpg", reloaded.GetRememberedWallpaper("MON2"));
    }

    [Fact]
    public void 记录壁纸后应当产生待还原项()
    {
        // 改过壁纸就必须留下还原线索，否则强杀后用户的壁纸回不来
        var journal = RestoreJournal.Load(_dir);
        Assert.False(journal.HasPendingRestore());

        journal.RememberWallpaper("MON1", @"E:\wall\a.jpg");

        Assert.True(RestoreJournal.Load(_dir).HasPendingRestore());
    }

    [Fact]
    public void 记录壁纸应当立即落盘()
    {
        // 与 MarkNativeIconsHidden 同样的不变量：先记账再动手，
        // 否则此刻被强杀就没人知道要还原什么
        var journal = RestoreJournal.Load(_dir);
        journal.RememberWallpaper("MON1", @"E:\wall\a.jpg");

        Assert.True(File.Exists(Path.Combine(_dir, "restore.json")));
    }

    [Fact]
    public void 清除某屏记录不应影响其他屏()
    {
        var journal = RestoreJournal.Load(_dir);
        journal.RememberWallpaper("MON1", @"E:\wall\a.jpg");
        journal.RememberWallpaper("MON2", @"E:\wall\b.jpg");

        journal.ForgetWallpaper("MON1");

        var reloaded = RestoreJournal.Load(_dir);
        Assert.Null(reloaded.GetRememberedWallpaper("MON1"));
        Assert.Equal(@"E:\wall\b.jpg", reloaded.GetRememberedWallpaper("MON2"));
    }

    [Fact]
    public void 空壁纸路径不应被记录()
    {
        var journal = RestoreJournal.Load(_dir);
        journal.RememberWallpaper("MON1", string.Empty);

        Assert.False(journal.HasPendingRestore());
    }
}
