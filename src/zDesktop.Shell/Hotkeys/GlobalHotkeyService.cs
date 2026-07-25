using System.Runtime.InteropServices;
using System.Windows.Interop;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Hotkeys;

/// <summary>
/// 全局热键服务 — 基于 RegisterHotKey + HwndSource 消息钩子
///
/// 用法：
/// 1. App 启动后，传入 overlay 窗口的 HWND 创建实例
/// 2. 调用 Register 注册热键（如 Alt+Space、Ctrl+Space）
/// 3. WM_HOTKEY 消息通过 HwndSource 钩子拦截，分发给回调
/// 4. 退出时 Dispose 注销所有热键
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _callbacks = new();
    private int _nextId = 9000;

    /// <summary>热键注册成功事件（id, 描述）</summary>
    public event Action<int, string>? HotkeyRegistered;

    /// <summary>热键注册失败事件（描述, 错误码）</summary>
    public event Action<string, int>? HotkeyRegistrationFailed;

    public GlobalHotkeyService(IntPtr hwnd)
    {
        _source = HwndSource.FromHwnd(hwnd)
            ?? throw new ArgumentException("无法从 HWND 获取 HwndSource", nameof(hwnd));
        _source.AddHook(WndProc);
    }

    /// <summary>
    /// 注册全局热键
    /// </summary>
    /// <param name="modifiers">修饰键组合（MOD_ALT | MOD_CONTROL 等）</param>
    /// <param name="key">虚拟键码</param>
    /// <param name="callback">触发回调</param>
    /// <param name="description">热键描述（用于日志）</param>
    /// <returns>热键 ID（失败返回 -1）</returns>
    public int Register(uint modifiers, uint key, Action callback, string description = "")
    {
        var id = _nextId++;
        var mods = modifiers | Win32.MOD_NOREPEAT;

        if (Win32.RegisterHotKey(_source.Handle, id, mods, key))
        {
            _callbacks[id] = callback;
            HotkeyRegistered?.Invoke(id, description);
            Console.WriteLine($"[Hotkey] 已注册: {description} (id={id})");
            return id;
        }
        else
        {
            var err = Marshal.GetLastWin32Error();
            HotkeyRegistrationFailed?.Invoke(description, err);
            Console.WriteLine($"[Hotkey] 注册失败: {description} (err={err})");
            return -1;
        }
    }

    /// <summary>注销热键</summary>
    public void Unregister(int id)
    {
        if (_callbacks.Remove(id))
        {
            Win32.UnregisterHotKey(_source.Handle, id);
            Console.WriteLine($"[Hotkey] 已注销 (id={id})");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_callbacks.TryGetValue(id, out var callback))
            {
                callback();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _callbacks.Keys.ToList())
        {
            Win32.UnregisterHotKey(_source.Handle, id);
        }
        _callbacks.Clear();
        _source.RemoveHook(WndProc);
    }
}
