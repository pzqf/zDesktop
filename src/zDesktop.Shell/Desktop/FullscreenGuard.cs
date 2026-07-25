using System.Windows.Threading;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Desktop;

/// <summary>
/// 全屏应用检测（设计案 v3.1 §二 原则 6、§八 性能预算）
///
/// 检测到全屏应用/游戏时通知外部让位：覆盖层隐藏、所有定时器停摆，
/// 保证全屏期间 zDesktop 零存在感、CPU 占用为 0。
///
/// 实现说明：用 <c>SHQueryUserNotificationState</c> 而非「比较前台窗口矩形与屏幕矩形」，
/// 后者会把最大化的普通窗口误判为全屏。该 API 由 Shell 维护，判定更准。
///
/// 轮询频率 2 秒 —— 全屏状态切换不需要即时响应，此频率对 CPU 的影响可忽略；
/// 一旦进入让位状态，外部会停掉其余全部定时器，本定时器是唯一保留的。
/// </summary>
public sealed class FullscreenGuard : IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _isFullscreen;

    /// <summary>全屏状态变化事件（true = 进入全屏，应让位）</summary>
    public event Action<bool>? FullscreenChanged;

    /// <summary>当前是否处于全屏应用状态</summary>
    public bool IsFullscreen => _isFullscreen;

    public FullscreenGuard()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        Poll(); // 启动时立即判定一次，避免在全屏游戏中启动后仍显示覆盖层
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Poll()
    {
        var current = QueryFullscreen();
        if (current == _isFullscreen) return;

        _isFullscreen = current;
        Console.WriteLine($"[FullscreenGuard] 全屏状态: {(current ? "进入（让位）" : "退出（恢复）")}");
        FullscreenChanged?.Invoke(current);
    }

    /// <summary>
    /// 查询是否有全屏应用占用屏幕。
    /// API 调用失败时返回 false —— 宁可显示覆盖层，也不要因检测失败而永久隐藏。
    /// </summary>
    private static bool QueryFullscreen()
    {
        try
        {
            // S_OK == 0
            if (Win32.SHQueryUserNotificationState(out var state) != 0) return false;

            return state == Win32.QUNS_RUNNING_D3D_FULL_SCREEN  // 全屏 D3D 游戏
                || state == Win32.QUNS_BUSY                      // 全屏应用（视频播放器等）
                || state == Win32.QUNS_PRESENTATION_MODE;        // 演示模式
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FullscreenGuard] 检测失败，按非全屏处理: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
