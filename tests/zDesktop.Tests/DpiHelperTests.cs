using Xunit;
using zDesktop.Shell.Interop;

namespace zDesktop.Tests;

/// <summary>
/// T1-1（设计案 v3.1 §十）：DPI 换算在 100%/125%/150%/175% 下往返一致。
///
/// 这组测试守的是 v2.3 实现里那个真实缺陷：SPI_GETWORKAREA 返回物理像素，
/// 被直接赋给 WPF 的 DIP 属性，150% 缩放下覆盖层变成屏幕的 1.5 倍。
/// </summary>
public class DpiHelperTests
{
    /// <summary>常见缩放档位对应的 DPI 值</summary>
    public static TheoryData<double> CommonDpis => new() { 96, 120, 144, 168, 192 };

    [Theory]
    [MemberData(nameof(CommonDpis))]
    public void 物理转DIP再转回来应当还原(double dpi)
    {
        const double physical = 1920;

        var dip = DpiHelper.ToDip(physical, dpi);
        var roundTrip = DpiHelper.ToPhysical(dip, dpi);

        Assert.Equal(physical, roundTrip, precision: 6);
    }

    [Theory]
    [MemberData(nameof(CommonDpis))]
    public void DIP转物理再转回来应当还原(double dpi)
    {
        const double dip = 1280;

        var physical = DpiHelper.ToPhysical(dip, dpi);
        var roundTrip = DpiHelper.ToDip(physical, dpi);

        Assert.Equal(dip, roundTrip, precision: 6);
    }

    [Fact]
    public void 一百五十缩放下物理像素应当缩小为三分之二()
    {
        // 144 DPI = 150% 缩放：2560 物理像素 = 1706.67 DIP
        var dip = DpiHelper.ToDip(2560, 144);

        Assert.Equal(2560 * 96.0 / 144.0, dip, precision: 6);
        Assert.True(dip < 2560, "150% 缩放下 DIP 必须小于物理像素，否则窗口会超出屏幕");
    }

    [Fact]
    public void 百分百缩放下换算应当是恒等的()
    {
        Assert.Equal(1920, DpiHelper.ToDip(1920, 96), precision: 6);
        Assert.Equal(1920, DpiHelper.ToPhysical(1920, 96), precision: 6);
    }

    [Theory]
    [InlineData(96, 1.0)]
    [InlineData(120, 1.25)]
    [InlineData(144, 1.5)]
    [InlineData(192, 2.0)]
    public void 缩放比应当由DPI正确导出(double dpi, double expectedScale)
    {
        Assert.Equal(expectedScale, DpiHelper.ScaleFromDpi(dpi), precision: 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 非法DPI应当退化为不换算(double badDpi)
    {
        // 取不到 DPI 时宁可不换算，也不能除零或得出负尺寸
        Assert.Equal(1.0, DpiHelper.ScaleFromDpi(badDpi), precision: 6);
        Assert.Equal(500, DpiHelper.ToDip(500, badDpi), precision: 6);
        Assert.Equal(500, DpiHelper.ToPhysical(500, badDpi), precision: 6);
    }

    [Fact]
    public void 矩形换算应当同时转换原点与尺寸()
    {
        // 副屏常见场景：原点非零（位于主屏右侧），且自身是 150% 缩放
        var rect = new Win32.RECT { Left = 1920, Top = 0, Right = 4480, Bottom = 1440 };

        var (left, top, width, height) = DpiHelper.RectToDip(rect, 144);

        Assert.Equal(1920 * 96.0 / 144.0, left, precision: 6);
        Assert.Equal(0, top, precision: 6);
        Assert.Equal(2560 * 96.0 / 144.0, width, precision: 6);
        Assert.Equal(1440 * 96.0 / 144.0, height, precision: 6);
    }

    [Fact]
    public void 矩形的宽高应当由左右上下算出而非直接取右下()
    {
        var rect = new Win32.RECT { Left = 100, Top = 50, Right = 900, Bottom = 650 };

        Assert.Equal(800, rect.Width);
        Assert.Equal(600, rect.Height);
    }
}
