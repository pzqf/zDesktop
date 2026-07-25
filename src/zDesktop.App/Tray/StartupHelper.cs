using Microsoft.Win32;

namespace zDesktop.App.Tray;

/// <summary>
/// 开机自启管理 — 通过注册表 HKCU\...\Run 实现
///
/// 优点：无需管理员权限，用户级生效
/// 注册表项：HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
/// 值名：zDesktop，值数据：当前 exe 完整路径
/// </summary>
public static class StartupHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "zDesktop";

    /// <summary>当前 exe 的完整路径（用于注册表值）</summary>
    private static string ExePath =>
        System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
        ?? Environment.ProcessPath!;

    /// <summary>检查是否已设置开机自启</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        var value = key?.GetValue(AppName) as string;
        return !string.IsNullOrEmpty(value);
    }

    /// <summary>启用开机自启</summary>
    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key?.SetValue(AppName, ExePath);
        Console.WriteLine($"[StartupHelper] 已启用开机自启: {ExePath}");
    }

    /// <summary>禁用开机自启</summary>
    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(AppName) != null)
        {
            key.DeleteValue(AppName, false);
            Console.WriteLine("[StartupHelper] 已禁用开机自启");
        }
    }

    /// <summary>切换开关状态，返回切换后的状态</summary>
    public static bool Toggle()
    {
        if (IsEnabled())
        {
            Disable();
            return false;
        }
        else
        {
            Enable();
            return true;
        }
    }
}
