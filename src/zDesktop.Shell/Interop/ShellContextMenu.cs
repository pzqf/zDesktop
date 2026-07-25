using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace zDesktop.Shell.Interop;

/// <summary>
/// Windows 原生右键菜单 — 通过 Shell IContextMenu 接口显示文件/文件夹的完整上下文菜单
///
/// 用途：zDesktop 自渲染图标上右键时，弹出与资源管理器完全相同的菜单（打开/删除/重命名/属性等）
/// 不做功能上的修改，完全委托给系统
/// </summary>
internal static class ShellContextMenu
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct STRRET
    {
        public uint uType;
        public IntPtr pOleStr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpTitle;
    }

    private const uint CMF_NORMAL = 0x0;
    private const uint CMF_DEFAULTONLY = 0x1;
    private const uint CMF_EXPLORE = 0x4; // 资源管理器上下文 — 包含重命名等完整菜单项

    private const int SW_SHOWNORMAL = 1;

    private const uint TPM_LEFTALIGN = 0x0;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const int MF_SEPARATOR = 0x0800;
    private const int TPM_LEFTBUTTON = 0x0;

    // GUID for IContextMenu
    private static readonly Guid IID_IContextMenu =
        new("000214E4-0000-0000-C000-000000000046");

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl,
            [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid,
            ref uint rgfReserved, [MarshalAs(UnmanagedType.Interface)] out IContextMenu ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, out STRRET pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl,
            [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst,
            uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        void GetCommandString(uint idCmd, uint uFlags, IntPtr pwReserved,
            StringBuilder pszName, uint cchMax);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        ref uint psfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHBindToParent(
        IntPtr pidl,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellFolder ppv,
        out IntPtr ppidlLast);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILFree(IntPtr pidl);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags,
        int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_NULL = 0x0000;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern int GetMessagePos();

    [StructLayout(LayoutKind.Sequential)]
    private struct TPMPARAMS
    {
        public int cbSize;
        public RECT rcExclude;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>
    /// 显示文件/文件夹的 Windows 原生右键菜单
    ///
    /// 在调用方的 UI 线程上执行（通过 Dispatcher.BeginInvoke 排队）。
    /// TrackPopupMenuEx 有自己的模态消息循环，会暂时接管消息泵，
    /// 菜单关闭后返回，期间 WPF 仍能处理绘制（菜单自身的 UI 由 Win32 处理）。
    /// </summary>
    /// <param name="path">文件/文件夹完整路径</param>
    /// <param name="screenX">菜单显示位置（屏幕坐标 X，物理像素）</param>
    /// <param name="screenY">菜单显示位置（屏幕坐标 Y，物理像素）</param>
    /// <param name="hwndOwner">所有者窗口句柄</param>
    public static void Show(string path, int screenX, int screenY, IntPtr hwndOwner)
    {
        // 在 WPF UI 线程上执行（Dispatcher 已由调用方保证）
        ShowInternal(path, screenX, screenY, hwndOwner);
    }

    /// <summary>菜单显示的实际执行体</summary>
    private static void ShowInternal(string path, int screenX, int screenY, IntPtr hwndOwner)
    {
        try
        {
            // 1. 解析路径 → PIDL
            uint attrs = 0;
            int hr = SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidlFull, 0, ref attrs);
            if (hr != 0 || pidlFull == IntPtr.Zero)
            {
                Console.WriteLine($"[ShellContextMenu] SHParseDisplayName 失败: {path} (hr=0x{hr:X})");
                return;
            }

            try
            {
                // 2. 绑定到父 IShellFolder，获取子项 PIDL
                var iidFolder = typeof(IShellFolder).GUID;
                hr = SHBindToParent(pidlFull, ref iidFolder, out IShellFolder shellFolder, out IntPtr pidlChild);
                if (hr != 0 || shellFolder == null)
                {
                    Console.WriteLine($"[ShellContextMenu] SHBindToParent 失败 (hr=0x{hr:X})");
                    return;
                }

                try
                {
                    // 3. 获取 IContextMenu
                    var iidCm = IID_IContextMenu;
                    uint reserved = 0;
                    var pidls = new[] { pidlChild };
                    hr = shellFolder.GetUIObjectOf(hwndOwner, 1, pidls, ref iidCm, ref reserved, out IContextMenu? cmenu);
                    if (hr != 0 || cmenu == null)
                    {
                        Console.WriteLine($"[ShellContextMenu] GetUIObjectOf 失败 (hr=0x{hr:X})");
                        return;
                    }

                    try
                    {
                        // 4. 构建菜单
                        IntPtr hMenu = CreatePopupMenu();
                        if (hMenu == IntPtr.Zero) return;

                        try
                        {
                            hr = cmenu.QueryContextMenu(hMenu, 0, 0, 0x7FFF, CMF_NORMAL | CMF_EXPLORE);
                            if (hr < 0)
                            {
                                Console.WriteLine($"[ShellContextMenu] QueryContextMenu 失败 (hr=0x{hr:X})");
                                return;
                            }

                            // 5. 激活所有者窗口 — Shell 的模态命令需要前台窗口
                            if (hwndOwner != IntPtr.Zero)
                                SetForegroundWindow(hwndOwner);

                            // 6. 显示菜单（TPM_RETURNCMD 返回选中的命令 ID）
                            //    TrackPopupMenuEx 有自己的模态消息循环
                            int cmd = TrackPopupMenuEx(
                                hMenu,
                                TPM_LEFTALIGN | TPM_RETURNCMD | TPM_RIGHTBUTTON,
                                screenX, screenY,
                                hwndOwner,
                                IntPtr.Zero);

                            if (cmd > 0)
                            {
                                // 7. 执行命令
                                var info = new CMINVOKECOMMANDINFOEX
                                {
                                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                                    fMask = 0,
                                    hwnd = hwndOwner,
                                    lpVerb = (IntPtr)cmd, // 偏移量作为动词
                                    lpParameters = IntPtr.Zero,
                                    lpDirectory = IntPtr.Zero,
                                    nShow = SW_SHOWNORMAL,
                                    dwHotKey = 0,
                                    hIcon = IntPtr.Zero,
                                    lpTitle = null!,
                                };
                                cmenu.InvokeCommand(ref info);
                            }
                        }
                        finally
                        {
                            DestroyMenu(hMenu);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(cmenu);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(shellFolder);
                }
            }
            finally
            {
                ILFree(pidlFull);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ShellContextMenu] 显示菜单异常 {path}: {ex.Message}");
        }
    }
}
