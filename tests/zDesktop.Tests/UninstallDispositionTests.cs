using Xunit;
using zDesktop.Shell.Fences;

namespace zDesktop.Tests;

/// <summary>
/// 卸载资产处置（设计案 v3.1 §6.2）。
///
/// 这一环决定用户对产品的最后印象：分区消失后若图标散落一地，
/// 用户会认定「这软件把我桌面搞乱了」，哪怕我们一个文件都没删。
/// </summary>
public class UninstallDispositionTests
{
    [Theory]
    [InlineData("工作文件", "工作文件")]
    [InlineData("  带空格  ", "带空格")]
    [InlineData("含:非法/字符*的名字", "含非法字符的名字")]
    public void 文件夹名应当剔除非法字符(string input, string expected)
    {
        Assert.Equal(expected, UninstallDisposition.SanitizeFolderName(input));
    }

    [Fact]
    public void 名称全是非法字符时应回退为默认名()
    {
        // 空文件夹名会让 Directory.CreateDirectory 抛异常，必须兜底
        Assert.Equal("分区", UninstallDisposition.SanitizeFolderName(@"://*?"));
        Assert.Equal("分区", UninstallDisposition.SanitizeFolderName("   "));
        Assert.Equal("分区", UninstallDisposition.SanitizeFolderName(""));
    }

    [Fact]
    public void 默认方式应当是保持现状()
    {
        // 三个选项里只有「保持现状」什么都不改，是伤害最小的默认值
        Assert.Equal(0, (int)DispositionMode.KeepAsIs);
    }
}
