using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using zDesktop.Shell.Monitoring;

namespace zDesktop.Shell.ControlCenter;

/// <summary>系统状态概览采样结果（CPU / 内存）</summary>
/// <param name="CpuUsage">CPU 使用率百分比（0-100），首次采样为 0</param>
/// <param name="MemoryUsage">内存使用率百分比（0-100）</param>
/// <param name="MemoryTotalBytes">物理内存总量（字节）</param>
/// <param name="MemoryAvailableBytes">可用物理内存（字节）</param>
public readonly record struct SystemStatusSample(
    float CpuUsage, float MemoryUsage, ulong MemoryTotalBytes, ulong MemoryAvailableBytes);

/// <summary>单个磁盘分区状态</summary>
/// <param name="Name">盘符（如 "C:\"）</param>
/// <param name="TotalBytes">总容量（字节）</param>
/// <param name="AvailableBytes">可用空间（字节）</param>
/// <param name="UsagePercent">占用百分比（0-100）</param>
/// <param name="IsReady">分区是否就绪可读</param>
public readonly record struct DriveStatus(
    string Name, long TotalBytes, long AvailableBytes, double UsagePercent, bool IsReady);

/// <summary>网络连接状态</summary>
/// <param name="IsAvailable">是否存在可用网络</param>
/// <param name="Description">可读描述（如 "已连接 · Wi-Fi" / "离线"）</param>
public readonly record struct NetworkStatus(bool IsAvailable, string Description);

/// <summary>快捷开关元数据（供 UI 渲染磁贴）</summary>
/// <param name="Key">开关唯一标识</param>
/// <param name="Name">显示名称</param>
/// <param name="Icon">图标字符（emoji/符号）</param>
public readonly record struct ToggleDescriptor(string Key, string Name, string Icon);

/// <summary>快捷开关当前状态</summary>
/// <param name="IsOn">是否处于开启态</param>
/// <param name="StatusText">状态文案（如 "已开启" / "深色" / "省电"）</param>
public readonly record struct ToggleStatus(bool IsOn, string StatusText);

/// <summary>系统工具入口元数据</summary>
/// <param name="Key">工具唯一标识</param>
/// <param name="Name">显示名称</param>
/// <param name="Icon">图标字符</param>
public readonly record struct ToolDescriptor(string Key, string Name, string Icon);

/// <summary>
/// 系统控制中心服务 — 汇聚系统状态概览、快捷开关、系统工具入口
///
/// 职责：
/// 1. 系统状态：CPU/内存（复用 <see cref="SystemPerformanceSampler"/>）、磁盘（DriveInfo）、网络（NetworkInterface）
/// 2. 快捷开关：WiFi / 蓝牙 / 夜间模式 / 勿扰 / 专注模式 / 省电模式 / 性能模式
///    —— 每个开关提供 GetStatus 与 Toggle；部分功能 Windows API 有限，采用 best-effort + 容错
/// 3. 系统工具：通过 Process.Start 启动控制面板、任务管理器等系统自带工具
///
/// 所有外部命令 / 注册表 / 进程调用均 try-catch，失败仅记录日志，不影响整体功能。
/// </summary>
public sealed class ControlCenterService
{
    /// <summary>系统性能采样器（CPU/内存），复用既有实现</summary>
    private readonly SystemPerformanceSampler _sampler;

    /// <summary>专注模式开关状态（zDesktop 自定义，非系统功能）</summary>
    private bool _focusModeEnabled;

    // ===== 快捷开关元数据（顺序即 UI 渲染顺序）=====
    private static readonly ToggleDescriptor[] _toggles =
    {
        new("wifi", "WiFi", "📶"),
        new("bluetooth", "蓝牙", "🔵"),
        new("night-light", "夜间模式", "🌙"),
        new("do-not-disturb", "勿扰", "🔕"),
        new("focus-mode", "专注模式", "🎯"),
        new("power-saver", "省电模式", "🔋"),
        new("performance", "性能模式", "⚡"),
    };

    // ===== 系统工具可执行文件映射 =====
    private static readonly Dictionary<string, string> _toolsMap = new()
    {
        ["control-panel"] = "control.exe",
        ["task-manager"] = "taskmgr.exe",
        ["registry-editor"] = "regedit.exe",
        ["device-manager"] = "devmgmt.msc",
        ["services"] = "services.msc",
        ["event-viewer"] = "eventvwr.msc",
        ["cmd"] = "cmd.exe",
        ["powershell"] = "powershell.exe",
        ["disk-management"] = "diskmgmt.msc",
    };

    /// <summary>系统工具元数据（顺序即 UI 渲染顺序）</summary>
    private static readonly ToolDescriptor[] _tools =
    {
        new("control-panel", "控制面板", "⚙️"),
        new("task-manager", "任务管理器", "📊"),
        new("registry-editor", "注册表", "🧩"),
        new("device-manager", "设备管理器", "🖥️"),
        new("services", "服务", "🔧"),
        new("event-viewer", "事件查看器", "📋"),
        new("disk-management", "磁盘管理", "💽"),
        new("cmd", "命令提示符", "▣"),
        new("powershell", "PowerShell", ">_"),
    };

    // ===== 电源方案 GUID（Windows 内置）=====
    private const string SchemeBalanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string SchemePowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private const string SchemeHighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    // ===== 注册表路径常量 =====
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string BthServiceKey = @"SYSTEM\CurrentControlSet\Services\bthserv";
    private const string NotificationsSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    /// <summary>构造控制中心服务，内部创建性能采样器</summary>
    public ControlCenterService()
    {
        _sampler = new SystemPerformanceSampler();
    }

    /// <summary>专注模式是否启用（供 UI 初始化读取）</summary>
    public bool IsFocusModeEnabled => _focusModeEnabled;

    // ============================================================
    //  系统状态概览
    // ============================================================

    /// <summary>
    /// 采集一次系统状态（CPU + 内存）
    /// CPU 首次采样返回 0%（无基准），后续返回真实使用率
    /// </summary>
    public SystemStatusSample GetSystemStatus()
    {
        try
        {
            var s = _sampler.SampleOnce();
            return new SystemStatusSample(s.CpuUsage, s.MemoryUsage, s.MemoryTotalBytes, s.MemoryAvailableBytes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ControlCenter] 系统状态采样失败: {ex.Message}");
            return new SystemStatusSample(0, 0, 0, 0);
        }
    }

    /// <summary>
    /// 枚举所有磁盘分区状态
    /// </summary>
    public IReadOnlyList<DriveStatus> GetDiskStatus()
    {
        var list = new List<DriveStatus>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady)
                    {
                        list.Add(new DriveStatus(d.Name, 0, 0, 0, false));
                        continue;
                    }
                    var total = d.TotalSize;
                    var avail = d.AvailableFreeSpace;
                    var usage = total > 0 ? (double)(total - avail) / total * 100.0 : 0;
                    list.Add(new DriveStatus(d.Name, total, avail, usage, true));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ControlCenter] 磁盘 {d.Name} 读取失败: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ControlCenter] 磁盘枚举失败: {ex.Message}");
        }
        return list;
    }

    /// <summary>
    /// 查询网络连接状态
    /// </summary>
    public NetworkStatus GetNetworkStatus()
    {
        try
        {
            var isAvail = NetworkInterface.GetIsNetworkAvailable();
            if (!isAvail) return new NetworkStatus(false, "离线");

            var up = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                     && !n.Description.Contains("Loopback", StringComparison.OrdinalIgnoreCase));
            return new NetworkStatus(true, up != null ? $"已连接 · {up.Name}" : "已连接");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ControlCenter] 网络状态读取失败: {ex.Message}");
            return new NetworkStatus(false, "未知");
        }
    }

    // ============================================================
    //  快捷开关
    // ============================================================

    /// <summary>获取所有快捷开关的元数据（供 UI 渲染磁贴）</summary>
    public IReadOnlyList<ToggleDescriptor> GetToggles() => _toggles;

    /// <summary>
    /// 查询指定开关的当前状态
    /// </summary>
    /// <param name="key">开关标识</param>
    public ToggleStatus GetToggleStatus(string key) => key switch
    {
        "wifi" => GetWifiStatus(),
        "bluetooth" => GetBluetoothStatus(),
        "night-light" => GetNightLightStatus(),
        "do-not-disturb" => GetDoNotDisturbStatus(),
        "focus-mode" => new ToggleStatus(_focusModeEnabled, _focusModeEnabled ? "专注中" : "关闭"),
        "power-saver" => GetPowerModeStatus(SchemePowerSaver, "省电"),
        "performance" => GetPowerModeStatus(SchemeHighPerf, "高性能"),
        _ => new ToggleStatus(false, "未知"),
    };

    /// <summary>
    /// 切换指定开关，返回切换后的开启状态
    /// </summary>
    /// <param name="key">开关标识</param>
    public bool Toggle(string key) => key switch
    {
        "wifi" => ToggleWifi(),
        "bluetooth" => ToggleBluetooth(),
        "night-light" => ToggleNightLight(),
        "do-not-disturb" => ToggleDoNotDisturb(),
        "focus-mode" => ToggleFocusMode(),
        "power-saver" => TogglePowerMode(SchemePowerSaver),
        "performance" => TogglePowerMode(SchemeHighPerf),
        _ => false,
    };

    // ---------- WiFi ----------

    /// <summary>查找无线网卡接口（best-effort）</summary>
    private static NetworkInterface? FindWifiInterface()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        }
        catch
        {
            return null;
        }
    }

    private ToggleStatus GetWifiStatus()
    {
        var wifi = FindWifiInterface();
        if (wifi == null) return new ToggleStatus(false, "未检测到");
        var on = wifi.OperationalStatus == OperationalStatus.Up;
        return new ToggleStatus(on, on ? "已开启" : "已关闭");
    }

    private bool ToggleWifi()
    {
        var wifi = FindWifiInterface();
        if (wifi == null) return false;
        var on = wifi.OperationalStatus == OperationalStatus.Up;
        var target = on ? "disable" : "enable";
        try
        {
            var psi = new ProcessStartInfo("netsh", $"interface set interface name=\"{wifi.Name}\" admin={target}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            var refreshed = FindWifiInterface();
            return refreshed != null && refreshed.OperationalStatus == OperationalStatus.Up;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ControlCenter] WiFi 切换失败（可能需要管理员权限）: {ex.Message}");
            return GetWifiStatus().IsOn;
        }
    }

    // ---------- 蓝牙（best-effort，通过 bthserv 服务启动类型判断）----------

    private ToggleStatus GetBluetoothStatus()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(BthServiceKey);
            if (key?.GetValue("Start") is int start)
            {
                // Start: 2=自动 / 3=手动 视为可用；4=禁用 视为关闭
                var on = start <= 3;
                return new ToggleStatus(on, on ? "已开启" : "已关闭");
            }
            return new ToggleStatus(false, "未检测到");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ControlCenter] 蓝牙状态读取失败: {ex.Message}");
            return new ToggleStatus(false, "未知");
        }
    }

    private bool ToggleBluetooth()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(BthServiceKey, writable: true);
            if (key == null) return false;
            var cur = key.GetValue("Start") as int? ?? 3;
            var newVal = cur <= 3 ? 4 : 2;
            key.SetValue("Start", newVal, RegistryValueKind.DWord);
            return newVal <= 3;
        }
        catch (Exception ex)
        {
            // 修改 HKLM 通常需要管理员权限，失败时回退为当前读取值
            Console.WriteLine($"[ControlCenter] 蓝牙切换失败（可能需要管理员权限）: {ex.Message}");
            return GetBluetoothStatus().IsOn;
        }
    }

    // ---------- 夜间模式（深色主题，AppsUseLightTheme）----------

    private ToggleStatus GetNightLightStatus()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue("AppsUseLightTheme") is int v)
            {
                // 0 = 深色（夜间模式开启），1 = 浅色（关闭）
                var on = v == 0;
                return new ToggleStatus(on, on ? "深色" : "浅色");
            }
            return new ToggleStatus(false, "默认");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ControlCenter] 夜间模式状态读取失败: {ex.Message}");
            return new ToggleStatus(false, "未知");
        }
    }

    private bool ToggleNightLight()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey, writable: true);
            var cur = key.GetValue("AppsUseLightTheme") as int? ?? 1;
            var newVal = cur == 0 ? 1 : 0;
            key.SetValue("AppsUseLightTheme", newVal, RegistryValueKind.DWord);
            // 广播 WM_SETTINGCHANGE，通知资源管理器刷新主题（best-effort）
            BroadcastSettingChange();
            return newVal == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ControlCenter] 夜间模式切换失败: {ex.Message}");
            return GetNightLightStatus().IsOn;
        }
    }

    // ---------- 勿扰（best-effort，通知静音开关）----------

    private ToggleStatus GetDoNotDisturbStatus()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(NotificationsSettingsKey);
            // NOC_GLOBAL_SETTING_TOASTS_ENABLED: 0 = 通知静音（勿扰），1/缺失 = 正常
            if (key?.GetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED") is int v)
            {
                var on = v == 0;
                return new ToggleStatus(on, on ? "静音中" : "正常");
            }
            return new ToggleStatus(false, "正常");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ControlCenter] 勿扰状态读取失败: {ex.Message}");
            return new ToggleStatus(false, "未知");
        }
    }

    private bool ToggleDoNotDisturb()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(NotificationsSettingsKey, writable: true);
            var cur = key.GetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED") as int? ?? 1;
            var newVal = cur == 0 ? 1 : 0;
            key.SetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED", newVal, RegistryValueKind.DWord);
            BroadcastSettingChange();
            return newVal == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ControlCenter] 勿扰切换失败: {ex.Message}");
            return GetDoNotDisturbStatus().IsOn;
        }
    }

    // ---------- 专注模式（zDesktop 自定义）----------

    private bool ToggleFocusMode()
    {
        _focusModeEnabled = !_focusModeEnabled;
        return _focusModeEnabled;
    }

    // ---------- 电源模式（省电 / 性能，powercfg）----------

    /// <summary>解析当前激活的电源方案 GUID</summary>
    private string? GetActivePowerScheme()
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", "/getactivescheme")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd();
            p?.WaitForExit(3000);
            if (output != null)
            {
                var match = Regex.Match(
                    output,
                    "([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
                if (match.Success) return match.Groups[1].Value.ToLowerInvariant();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ControlCenter] 电源方案读取失败: {ex.Message}");
        }
        return null;
    }

    /// <summary>设置激活的电源方案，返回切换后的方案 GUID</summary>
    private string? SetActivePowerScheme(string guid)
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", $"/setactive {guid}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ControlCenter] 电源方案切换失败（可能需要管理员权限）: {ex.Message}");
        }
        return GetActivePowerScheme();
    }

    private ToggleStatus GetPowerModeStatus(string schemeGuid, string label)
    {
        var cur = GetActivePowerScheme();
        if (cur == null) return new ToggleStatus(false, "未知");
        var on = cur == schemeGuid;
        return new ToggleStatus(on, on ? label : "未启用");
    }

    private bool TogglePowerMode(string schemeGuid)
    {
        var cur = GetActivePowerScheme();
        // 已是该方案 → 切回平衡；否则切到目标方案
        var target = cur == schemeGuid ? SchemeBalanced : schemeGuid;
        var after = SetActivePowerScheme(target);
        return after == schemeGuid;
    }

    // ============================================================
    //  系统工具入口
    // ============================================================

    /// <summary>获取所有系统工具元数据（供 UI 渲染磁贴）</summary>
    public IReadOnlyList<ToolDescriptor> GetTools() => _tools;

    /// <summary>
    /// 启动指定的系统工具
    /// </summary>
    /// <param name="toolKey">工具标识</param>
    /// <returns>是否成功启动</returns>
    public bool LaunchTool(string toolKey)
    {
        if (!_toolsMap.TryGetValue(toolKey, out var exe))
        {
            Console.WriteLine($"[ControlCenter] 未知工具标识: {toolKey}");
            return false;
        }
        try
        {
            // UseShellExecute=true 以支持 .msc 文件（由 mmc 托管）及系统 PATH 解析
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ControlCenter] 启动工具 {exe} 失败: {ex.Message}");
            return false;
        }
    }

    // ============================================================
    //  辅助：广播系统设置变更（best-effort）
    // ============================================================

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string? lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>广播 WM_SETTINGCHANGE，通知系统/资源管理器刷新主题与通知设置</summary>
    private static void BroadcastSettingChange()
    {
        try
        {
            SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero,
                "ImmersiveColorSet", SMTO_ABORTIFHUNG, 1000, out _);
        }
        catch
        {
            // best-effort，失败忽略
        }
    }
}
