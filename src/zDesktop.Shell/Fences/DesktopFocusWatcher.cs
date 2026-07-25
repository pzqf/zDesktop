using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Fences;

/// <summary>
/// 桌面焦点监视 —— 焦点驱动轮询的开关（设计案 v3.1 §4.2 决策 4）。
///
/// <para><b>为什么用事件而不是定时器</b>：§八 规定空闲态（桌面未聚焦）
/// CPU &lt; 0.1% 且「无图标轮询」。哪怕 1 秒一次的焦点查询也会让空闲态
/// 常驻一个定时器，与预算相抵触。<c>SetWinEventHook</c> 只在前台窗口
/// **真的切换**时才回调，静止时零开销。</para>
///
/// <para>钩子用 <c>WINEVENT_OUTOFCONTEXT</c> 注册：回调在本进程执行，
/// 不向目标进程注入 DLL —— 对杀软友好（§十二 误报风险）。</para>
/// </summary>
public sealed class DesktopFocusWatcher : IDisposable
{
    private IntPtr _hook;

    /// <summary>必须持有委托引用，否则会被 GC 回收导致回调时崩溃</summary>
    private readonly Win32.WinEventProc _proc;

    private bool _isDesktopFocused;

    /// <summary>桌面焦点状态变化（true = 桌面获得焦点，应开始轮询）</summary>
    public event Action<bool>? FocusChanged;

    /// <summary>当前桌面是否处于前台</summary>
    public bool IsDesktopFocused => _isDesktopFocused;

    public DesktopFocusWatcher()
    {
        _proc = OnWinEvent;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;

        _hook = Win32.SetWinEventHook(
            Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);

        if (_hook == IntPtr.Zero)
            Console.WriteLine("[FocusWatcher] SetWinEventHook 失败，分区将无法感知用户拖动");

        // 启动时先判定一次当前状态
        Evaluate();
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        Win32.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        Evaluate();
    }

    private void Evaluate()
    {
        bool focused;
        try
        {
            focused = DesktopFocus.IsDesktopForeground();
        }
        catch
        {
            // 钩子回调里不允许抛异常逃逸，会直接干掉消息循环
            return;
        }

        if (focused == _isDesktopFocused) return;

        _isDesktopFocused = focused;
        Console.WriteLine($"[FocusWatcher] 桌面焦点: {(focused ? "获得（开始轮询）" : "失去（停止轮询）")}");
        FocusChanged?.Invoke(focused);
    }

    public void Dispose() => Stop();
}
