using System.IO;
using System.Runtime.InteropServices;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Desktop;

/// <summary>
/// 原生桌面图标层恢复器 — 解决异常退出后原生图标被隐藏的问题
///
/// 原理：
/// 1. zDesktop 启动时写入运行标记文件，正常退出时删除
/// 2. 下次启动时检测标记文件存在 → 上次异常退出 → 主动恢复原生图标层
/// 3. 通过查找 SHELLDLL_DefView HWND 并 ShowWindow 恢复显示
/// </summary>
public static class DesktopRestore
{
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");

    private static readonly string RunningFlagFile = Path.Combine(AppDataDir, ".running");

    /// <summary>
    /// 检查上次是否异常退出（标记文件存在但进程已不在）
    /// 应在应用启动最开始调用
    /// </summary>
    /// <returns>true 表示上次异常退出，需要恢复原生桌面</returns>
    public static bool WasAbnormalExit()
    {
        try
        {
            return File.Exists(RunningFlagFile);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 写入运行标记 — 表示 zDesktop 正在运行
    /// 应在应用启动、桌面图标层被隐藏后调用
    /// </summary>
    public static void MarkRunning()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(RunningFlagFile, DateTime.Now.ToString("o"));
        }
        catch
        {
            // 标记文件不是关键路径，忽略写入失败
        }
    }

    /// <summary>
    /// 清除运行标记 — 表示 zDesktop 正常退出
    /// 应在 OnExit 中调用
    /// </summary>
    public static void ClearRunningFlag()
    {
        try
        {
            if (File.Exists(RunningFlagFile))
                File.Delete(RunningFlagFile);
        }
        catch
        {
            // 忽略删除失败
        }
    }

    /// <summary>
    /// 恢复原生桌面图标层显示 — 查找 SHELLDLL_DefView 并 ShowWindow
    /// 不依赖 zDesktop 进程内的 HWND 缓存，可在进程启动时独立调用
    /// </summary>
    public static void RestoreNativeDesktopIcons()
    {
        try
        {
            var defView = DesktopWindowFinder.FindDesktopIconView();
            if (defView != IntPtr.Zero)
            {
                Win32.ShowWindow(defView, Win32.SW_SHOWNORMAL);
                Console.WriteLine("[DesktopRestore] 已恢复原生桌面图标层");
            }
            else
            {
                Console.WriteLine("[DesktopRestore] 未找到 SHELLDLL_DefView，原生桌面可能已正常");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DesktopRestore] 恢复失败: {ex.Message}");
        }
    }
}
