using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Monitoring;

/// <summary>
/// 系统性能采样器 — 通过 Win32 API 计算 CPU 和内存使用率
///
/// CPU 算法：两次采样间隔内，CPU 使用率 = 1 - Δidle / (Δkernel + Δuser)
/// 内存算法：GlobalMemoryStatusEx 直接返回 dwMemoryLoad
/// </summary>
public sealed class SystemPerformanceSampler
{
    private long _lastIdle;
    private long _lastKernel;
    private long _lastUser;
    private bool _hasLastSample;

    /// <summary>采样结果</summary>
    public readonly record struct Sample(float CpuUsage, float MemoryUsage, ulong MemoryTotalBytes, ulong MemoryAvailableBytes);

    /// <summary>
    /// 采集一次 — 首次调用返回 0% CPU（无基准），后续返回真实使用率
    /// 建议每 1 秒调用一次
    /// </summary>
    public Sample SampleOnce()
    {
        // ----- 内存 -----
        var mem = Win32.MEMORYSTATUSEX.Create();
        Win32.GlobalMemoryStatusEx(ref mem);
        var memUsage = mem.dwMemoryLoad;

        // ----- CPU -----
        float cpuUsage = 0;
        if (Win32.GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            var idle = idleFt.Value;
            var kernel = kernelFt.Value;
            var user = userFt.Value;

            if (_hasLastSample)
            {
                var dIdle = idle - _lastIdle;
                var dKernel = kernel - _lastKernel;
                var dUser = user - _lastUser;
                var dTotal = dKernel + dUser; // kernel 已包含 idle

                if (dTotal > 0)
                {
                    cpuUsage = Math.Clamp(1f - (float)dIdle / dTotal, 0f, 1f) * 100f;
                }
            }

            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
            _hasLastSample = true;
        }

        return new Sample(cpuUsage, memUsage, mem.ullTotalPhys, mem.ullAvailPhys);
    }
}
