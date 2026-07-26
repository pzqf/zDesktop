namespace zDesktop.Core.Widgets;

/// <summary>
/// 一条定长采样序列：新样本从右边进，满了就把最老的挤出去。
///
/// <para><b>为什么单独成类</b>：原先是「一个数组 + 一个共享的计数器」，
/// CPU 和内存两条曲线共用那个计数器，每次采样它被加了两次 ——
/// CPU 的值落在偶数下标、内存落在奇数下标，中间空着的 0 被照样画出来，
/// 于是两条曲线都成了规则锯齿。数组和它的有效长度必须绑在一起，
/// 才不会有第二个地方去维护那个长度。</para>
/// </summary>
public sealed class SampleSeries
{
    private readonly float[] _values;

    /// <param name="capacity">保留多少个最近样本</param>
    public SampleSeries(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _values = new float[capacity];
    }

    /// <summary>容量上限</summary>
    public int Capacity => _values.Length;

    /// <summary>已有样本数（未填满时小于容量）</summary>
    public int Count { get; private set; }

    /// <summary>第 i 个样本，0 是最老的</summary>
    public float this[int index] => _values[index];

    /// <summary>推入一个新样本；满了就丢掉最老的那个</summary>
    public void Push(float value)
    {
        if (Count < _values.Length)
        {
            _values[Count++] = value;
            return;
        }

        Array.Copy(_values, 1, _values, 0, _values.Length - 1);
        _values[^1] = value;
    }

    /// <summary>按从老到新的顺序枚举已有样本</summary>
    public IEnumerable<float> Values
    {
        get
        {
            for (var i = 0; i < Count; i++) yield return _values[i];
        }
    }
}
