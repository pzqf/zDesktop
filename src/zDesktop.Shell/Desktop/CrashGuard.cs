using System.Runtime.InteropServices;

namespace zDesktop.Shell.Desktop;

/// <summary>
/// 崩溃保护 — 捕获未处理异常和系统退出信号，确保恢复原生桌面
///
/// 注册的捕获源：
/// 1. AppDomain.UnhandledException — .NET 未捕获异常
/// 2. TaskScheduler.UnobservedTaskException — Task 未观察异常
/// 3. SetConsoleCtrlHandler — Ctrl+C / 系统关机 / 任务管理器结束
/// 4. Dispatcher.UnhandledException — WPF UI 线程未捕获异常
/// </summary>
public static class CrashGuard
{
    /// <summary>紧急恢复回调 — 由 App 层注入（恢复原生桌面 + 清理标记）</summary>
    private static Action? _emergencyRestore;

    /// <summary>
    /// 注册所有崩溃/退出捕获
    /// </summary>
    /// <param name="emergencyRestore">紧急恢复回调（恢复原生桌面 + 清理标记文件）</param>
    public static void Install(Action emergencyRestore)
    {
        _emergencyRestore = emergencyRestore;

        // 1. .NET 未捕获异常
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.WriteLine($"[CrashGuard] 未处理异常: {e.ExceptionObject}");
            EmergencyExit();
        };

        // 2. Task 未观察异常
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.WriteLine($"[CrashGuard] Task 未观察异常: {e.Exception}");
            e.SetObserved(); // 标记已处理，防止进程崩溃
        };

        // 3. 控制台信号（Ctrl+C / 关机 / 注销）
        SetConsoleCtrlHandler(ConsoleCtrlHandler, true);

        // 4. WPF UI 线程未捕获异常
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
        {
            System.Windows.Application.Current.Dispatcher.UnhandledException += (_, e) =>
            {
                Console.WriteLine($"[CrashGuard] UI 线程异常: {e.Exception}");
                e.Handled = true; // 阻止默认崩溃
                EmergencyExit();
            };
        }));
    }

    /// <summary>紧急退出 — 恢复原生桌面后终止进程</summary>
    private static void EmergencyExit()
    {
        try
        {
            _emergencyRestore?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CrashGuard] 紧急恢复失败: {ex.Message}");
        }
    }

    /// <summary>控制台信号处理器</summary>
    private static bool ConsoleCtrlHandler(int ctrlType)
    {
        // CTRL_C_EVENT=0, CTRL_BREAK_EVENT=1, CTRL_CLOSE_EVENT=2,
        // CTRL_LOGOFF_EVENT=5, CTRL_SHUTDOWN_EVENT=6
        Console.WriteLine($"[CrashGuard] 收到控制台信号: {ctrlType}");
        EmergencyExit();
        return false; // 让默认处理继续
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);

    private delegate bool ConsoleCtrlDelegate(int ctrlType);
}
