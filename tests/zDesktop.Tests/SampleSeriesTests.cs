using Xunit;
using zDesktop.Core.Widgets;

namespace zDesktop.Tests;

/// <summary>
/// 采样序列 —— 系统监控折线图的数据源。
///
/// 真机上曾表现为两条曲线都成了规则锯齿：两条序列共用一个计数器，
/// 每次采样它被加了两次，CPU 落在偶数下标、内存落在奇数下标，
/// 中间空着的 0 被照样画出来。这类「数组和长度分家」的错误，
/// 单测一验就出。
/// </summary>
public class SampleSeriesTests
{
    [Fact]
    public void 两条序列各记各的互不干扰()
    {
        var cpu = new SampleSeries(4);
        var mem = new SampleSeries(4);

        cpu.Push(10); mem.Push(90);
        cpu.Push(20); mem.Push(80);

        Assert.Equal(new float[] { 10, 20 }, cpu.Values);
        Assert.Equal(new float[] { 90, 80 }, mem.Values);
    }

    [Fact]
    public void 未填满时不应留出空洞()
    {
        // 空洞会被折线图当成 0 画出来，正是锯齿的来源
        var s = new SampleSeries(60);

        s.Push(50);
        s.Push(60);

        Assert.Equal(2, s.Count);
        Assert.Equal(new float[] { 50, 60 }, s.Values);
    }

    [Fact]
    public void 满了之后应当挤掉最老的样本()
    {
        var s = new SampleSeries(3);

        foreach (var v in new float[] { 1, 2, 3, 4, 5 }) s.Push(v);

        Assert.Equal(3, s.Count);
        Assert.Equal(new float[] { 3, 4, 5 }, s.Values);
    }

    [Fact]
    public void 计数不应超过容量()
    {
        var s = new SampleSeries(2);

        for (var i = 0; i < 100; i++) s.Push(i);

        Assert.Equal(2, s.Count);
        Assert.Equal(2, s.Capacity);
    }

    [Fact]
    public void 容量必须为正()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SampleSeries(0));
    }
}
