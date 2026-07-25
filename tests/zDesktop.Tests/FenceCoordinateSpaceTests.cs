using Xunit;
using zDesktop.Core.Fences;
using zDesktop.Shell.Desktop;
using zDesktop.Shell.Fences;
using zDesktop.Shell.Interop;

namespace zDesktop.Tests;

/// <summary>
/// T1-2 的坐标空间部分（设计案 v3.1 §四）。
///
/// 测试数据取自 M3-B 探针在真机上的实测值：双屏、副屏位于主屏**左侧**
/// （X 为负）、虚拟屏原点 (-1920,0)、ListView 客户区覆盖整个虚拟屏。
/// 副屏在左侧是最容易写错的配置 —— 任何忘记处理负坐标的实现都会在这里翻车。
/// </summary>
public class FenceCoordinateSpaceTests
{
    private static MonitorInfo Monitor(string key, int l, int t, int r, int b,
                                       int wl, int wt, int wr, int wb, double dpi, bool primary)
        => new()
        {
            Key = key,
            Bounds = new Win32.RECT { Left = l, Top = t, Right = r, Bottom = b },
            WorkArea = new Win32.RECT { Left = wl, Top = wt, Right = wr, Bottom = wb },
            IsPrimary = primary,
            Dpi = dpi,
        };

    /// <summary>M3-B 探针实测的真机配置</summary>
    private static FenceCoordinateSpace RealWorld()
    {
        var monitors = new List<MonitorInfo>
        {
            Monitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1032, 96, primary: true),
            Monitor(@"\\.\DISPLAY2", -1920, 5, 0, 1085, -1920, 5, 0, 1037, 96, primary: false),
        };
        return new FenceCoordinateSpace(monitors, virtualOriginX: -1920, virtualOriginY: 0);
    }

    /// <summary>混合缩放：主屏 100%，副屏 150%</summary>
    private static FenceCoordinateSpace MixedDpi()
    {
        var monitors = new List<MonitorInfo>
        {
            Monitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1032, 96, primary: true),
            Monitor(@"\\.\DISPLAY2", 1920, 0, 4480, 1440, 1920, 0, 4480, 1392, 144, primary: false),
        };
        return new FenceCoordinateSpace(monitors, virtualOriginX: 0, virtualOriginY: 0);
    }

    // ===== 客户区 ↔ 屏幕 =====

    [Fact]
    public void 客户区转屏幕应当加上虚拟屏原点()
    {
        var space = RealWorld();

        // 探针实测：客户区 (1934,2) → 屏幕 (14,2)
        Assert.Equal((14, 2), space.ClientToScreen(new IconPoint(1934, 2)));
    }

    [Fact]
    public void 屏幕转客户区应当是转换的逆运算()
    {
        var space = RealWorld();

        var client = space.ScreenToClient(14, 2);
        Assert.Equal(new IconPoint(1934, 2), client);
        Assert.Equal((14, 2), space.ClientToScreen(client));
    }

    [Fact]
    public void 副屏的负屏幕坐标应当映射到正的客户区坐标()
    {
        // 副屏在左侧，屏幕 X 为负；客户区坐标必须落在 [0, 虚拟屏宽) 内
        var space = RealWorld();

        var client = space.ScreenToClient(-1900, 100);

        Assert.Equal(new IconPoint(20, 100), client);
        Assert.True(client.X >= 0, "副屏图标的客户区 X 不应为负");
    }

    // ===== 显示器归属 =====

    [Fact]
    public void 屏幕坐标应当归属到正确的显示器()
    {
        var space = RealWorld();

        Assert.Equal(@"\\.\DISPLAY1", space.MonitorAtScreen(14, 2)!.Key);
        Assert.Equal(@"\\.\DISPLAY2", space.MonitorAtScreen(-1900, 100)!.Key);
    }

    [Fact]
    public void 显示器之外的坐标应当返回空()
    {
        var space = RealWorld();
        Assert.Null(space.MonitorAtScreen(99999, 99999));
    }

    // ===== 分区矩形换算 =====

    [Fact]
    public void 主屏分区应当换算回原值()
    {
        var space = RealWorld();
        var fence = new Fence
        {
            MonitorKey = @"\\.\DISPLAY1",
            Rect = new FenceRect { X = 40, Y = 60, Width = 420, Height = 300 },
        };

        var iconRect = space.FenceToIconSpace(fence)!.Value;

        // 主屏工作区原点 (0,0)，DPI 96 → 屏幕 (40,60) → 客户区 (1960,60)
        Assert.Equal(1960, iconRect.X);
        Assert.Equal(60, iconRect.Y);
        Assert.Equal(420, iconRect.Width);
        Assert.Equal(300, iconRect.Height);
    }

    [Fact]
    public void 副屏分区应当落在负屏幕坐标区域()
    {
        var space = RealWorld();
        var fence = new Fence
        {
            MonitorKey = @"\\.\DISPLAY2",
            Rect = new FenceRect { X = 100, Y = 50, Width = 400, Height = 300 },
        };

        var iconRect = space.FenceToIconSpace(fence)!.Value;
        var (screenX, screenY) = space.ClientToScreen(new IconPoint(iconRect.X, iconRect.Y));

        // 副屏工作区原点 (-1920,5) → 屏幕应为 (-1820,55)
        Assert.Equal(-1820, screenX);
        Assert.Equal(55, screenY);
        Assert.Equal(@"\\.\DISPLAY2", space.MonitorAtScreen(screenX, screenY)!.Key);
    }

    [Fact]
    public void 分区矩形往返换算应当保持一致()
    {
        var space = RealWorld();
        var monitor = space.MonitorByKey(@"\\.\DISPLAY2")!;
        var original = new FenceRect { X = 100, Y = 50, Width = 400, Height = 300 };

        var iconRect = space.FenceToIconSpace(new Fence { MonitorKey = monitor.Key, Rect = original })!.Value;
        var back = space.IconSpaceToFence(iconRect, monitor);

        Assert.Equal(original.X, back.X, precision: 3);
        Assert.Equal(original.Y, back.Y, precision: 3);
        Assert.Equal(original.Width, back.Width, precision: 3);
        Assert.Equal(original.Height, back.Height, precision: 3);
    }

    [Fact]
    public void 高缩放副屏的分区应当按自身DPI换算尺寸()
    {
        var space = MixedDpi();
        var fence = new Fence
        {
            MonitorKey = @"\\.\DISPLAY2",
            Rect = new FenceRect { X = 100, Y = 50, Width = 400, Height = 300 },
        };

        var iconRect = space.FenceToIconSpace(fence)!.Value;

        // 副屏 144 DPI（150%）：400 DIP → 600 物理像素
        Assert.Equal(600, iconRect.Width);
        Assert.Equal(450, iconRect.Height);
        // 原点：工作区 1920 + 100 DIP×1.5 = 1920+150 = 2070
        Assert.Equal(2070, iconRect.X);
    }

    [Fact]
    public void 显示器已拔掉的分区应当返回空而不是抛异常()
    {
        var space = RealWorld();
        var orphan = new Fence
        {
            MonitorKey = @"\\.\DISPLAY_GONE",
            Rect = new FenceRect { X = 0, Y = 0, Width = 100, Height = 100 },
        };

        Assert.Null(space.FenceToIconSpace(orphan));
    }

    // ===== 命中测试 =====

    [Fact]
    public void 落在分区内的图标应当被判定归属该分区()
    {
        var space = RealWorld();
        var fences = new List<Fence>
        {
            new() { Id = "f1", MonitorKey = @"\\.\DISPLAY1",
                    Rect = new FenceRect { X = 40, Y = 60, Width = 420, Height = 300 } },
        };

        // 客户区 (2000,100) → 屏幕 (80,100)，落在 (40,60)-(460,360) 内
        Assert.Equal("f1", space.FenceAt(new IconPoint(2000, 100), fences)!.Id);
    }

    [Fact]
    public void 落在分区外的图标不应归属任何分区()
    {
        var space = RealWorld();
        var fences = new List<Fence>
        {
            new() { Id = "f1", MonitorKey = @"\\.\DISPLAY1",
                    Rect = new FenceRect { X = 40, Y = 60, Width = 420, Height = 300 } },
        };

        Assert.Null(space.FenceAt(new IconPoint(3800, 900), fences));
    }

    [Fact]
    public void 折叠的分区不应接收拖入()
    {
        // 折叠后只剩标题条，此时把图标拖到它原来的范围里不该被吸进去
        var space = RealWorld();
        var fences = new List<Fence>
        {
            new() { Id = "f1", MonitorKey = @"\\.\DISPLAY1", Collapsed = true,
                    Rect = new FenceRect { X = 40, Y = 60, Width = 420, Height = 300 } },
        };

        Assert.Null(space.FenceAt(new IconPoint(2000, 100), fences));
    }

    [Fact]
    public void 显示器已拔掉的分区不应参与命中测试()
    {
        var space = RealWorld();
        var fences = new List<Fence>
        {
            new() { Id = "orphan", MonitorKey = @"\\.\GONE",
                    Rect = new FenceRect { X = 0, Y = 0, Width = 4000, Height = 4000 } },
        };

        Assert.Null(space.FenceAt(new IconPoint(2000, 100), fences));
    }
}
