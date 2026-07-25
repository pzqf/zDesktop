using Xunit;
using zDesktop.Shell.Desktop;
using zDesktop.Shell.Interop;

namespace zDesktop.Tests;

/// <summary>
/// T1-1 的多屏部分（设计案 v3.1 §八）：显示器工作区 → DIP 的换算，
/// 以及「稳定 key、绝不用索引」这条持久化不变量。
/// </summary>
public class MonitorSetTests
{
    private static MonitorInfo Make(string key, int left, int top, int right, int bottom, double dpi, bool primary = false)
        => new()
        {
            Key = key,
            Bounds = new Win32.RECT { Left = left, Top = top, Right = right, Bottom = bottom },
            WorkArea = new Win32.RECT { Left = left, Top = top, Right = right, Bottom = bottom },
            IsPrimary = primary,
            Dpi = dpi,
        };

    [Fact]
    public void 百分百缩放的主屏工作区应当原样映射为DIP()
    {
        var monitor = Make(@"\\.\DISPLAY1", 0, 0, 1920, 1040, 96, primary: true);

        var (left, top, width, height) = monitor.WorkAreaDip;

        Assert.Equal(0, left, precision: 6);
        Assert.Equal(0, top, precision: 6);
        Assert.Equal(1920, width, precision: 6);
        Assert.Equal(1040, height, precision: 6);
    }

    [Fact]
    public void 一百五十缩放的副屏应当按自身DPI换算原点与尺寸()
    {
        // 混合缩放的真实场景：副屏在主屏右侧，自身 150%
        var monitor = Make(@"\\.\DISPLAY2", 1920, 0, 4480, 1440, 144);

        var (left, top, width, height) = monitor.WorkAreaDip;

        Assert.Equal(1280, left, precision: 6);   // 1920 × 96/144
        Assert.Equal(0, top, precision: 6);
        Assert.Equal(1706.666, width, precision: 2);  // 2560 × 96/144
        Assert.Equal(960, height, precision: 6);      // 1440 × 96/144
    }

    [Fact]
    public void 缩放比应当反映在Scale上()
    {
        Assert.Equal(1.0, Make("a", 0, 0, 100, 100, 96).Scale, precision: 6);
        Assert.Equal(1.5, Make("b", 0, 0, 100, 100, 144).Scale, precision: 6);
        Assert.Equal(2.0, Make("c", 0, 0, 100, 100, 192).Scale, precision: 6);
    }

    [Fact]
    public void 取主屏应当优先返回标记为主的显示器()
    {
        var monitors = new List<MonitorInfo>
        {
            Make(@"\\.\DISPLAY2", 1920, 0, 3840, 1080, 96),
            Make(@"\\.\DISPLAY1", 0, 0, 1920, 1040, 96, primary: true),
        };

        Assert.Equal(@"\\.\DISPLAY1", MonitorSet.Primary(monitors).Key);
    }

    [Fact]
    public void 无主屏标记时应当回落到第一个显示器()
    {
        var monitors = new List<MonitorInfo>
        {
            Make(@"\\.\DISPLAY2", 0, 0, 1920, 1080, 96),
            Make(@"\\.\DISPLAY3", 1920, 0, 3840, 1080, 96),
        };

        Assert.Equal(@"\\.\DISPLAY2", MonitorSet.Primary(monitors).Key);
    }

    [Fact]
    public void 枚举真实显示器应当至少返回一个且key非空()
    {
        // 与真实系统交互：即使枚举失败也有 SPI_GETWORKAREA 兜底，绝不返回空集合
        var monitors = MonitorSet.Enumerate();

        Assert.NotEmpty(monitors);
        Assert.All(monitors, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Key), "显示器 key 不能为空，否则组件归属无法持久化");
            Assert.True(m.WorkArea.Width > 0, "工作区宽度必须为正");
            Assert.True(m.WorkArea.Height > 0, "工作区高度必须为正");
            Assert.True(m.Dpi > 0, "DPI 必须为正，否则换算会除零");
        });
    }

    [Fact]
    public void 真实枚举结果中key应当唯一()
    {
        // key 是组件/分区归属的持久化依据，重复会导致还原到错误的屏幕
        var monitors = MonitorSet.Enumerate();
        var keys = monitors.Select(m => m.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
