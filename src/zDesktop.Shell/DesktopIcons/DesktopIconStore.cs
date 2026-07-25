using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using zDesktop.Core.DesktopIcons;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.DesktopIcons;

/// <summary>
/// 桌面图标存储服务 — 扫描桌面文件夹 + 提取图标 + 布局持久化
///
/// 数据来源：用户桌面 + 公共桌面（Common Desktop）
/// 图标提取：System.Drawing.Icon.ExtractAssociatedIcon（对 .lnk/exe/文件夹均可用）
/// 持久化：%APPDATA%\zDesktop\icons-layout.json
/// </summary>
public sealed class DesktopIconStore
{
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");

    private static readonly string LayoutFilePath = Path.Combine(AppDataDir, "icons-layout.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _lock = new();

    /// <summary>扫描结果项 — 图标信息 + 已提取的 WPF 图标图像</summary>
    public sealed record ScannedIcon(DesktopIconInfo Info, ImageSource? Icon);

    /// <summary>
    /// 扫描桌面文件夹，返回所有可见桌面项
    /// </summary>
    public List<ScannedIcon> Scan()
    {
        var result = new List<ScannedIcon>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 用户桌面
        var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        ScanDirectory(userDesktop, isCommon: false, result, seen);

        // 公共桌面
        var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrEmpty(commonDesktop) &&
            !string.Equals(commonDesktop, userDesktop, StringComparison.OrdinalIgnoreCase))
        {
            ScanDirectory(commonDesktop, isCommon: true, result, seen);
        }

        Console.WriteLine($"[DesktopIconStore] 扫描完成：{result.Count} 个桌面项");
        return result;
    }

    private void ScanDirectory(string dir, bool isCommon, List<ScannedIcon> result, HashSet<string> seen)
    {
        if (!Directory.Exists(dir)) return;

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DesktopIconStore] 无法读取目录 {dir}: {ex.Message}");
            return;
        }

        foreach (var path in entries)
        {
            var name = Path.GetFileName(path);
            if (ShouldSkip(name)) continue;
            if (!seen.Add(path)) continue;

            var info = new DirectoryInfo(path);
            var isDir = info.Attributes.HasFlag(FileAttributes.Directory);

            var iconInfo = new DesktopIconInfo
            {
                SourcePath = path,
                DisplayName = BuildDisplayName(name, isDir),
                IsShortcut = name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                             name.EndsWith(".url", StringComparison.OrdinalIgnoreCase),
                IsDirectory = isDir,
                IsCommon = isCommon,
            };

            var icon = ExtractIcon(path, isDir);
            result.Add(new ScannedIcon(iconInfo, icon));
        }
    }

    /// <summary>判断是否跳过该文件（系统/临时文件）</summary>
    private static bool ShouldSkip(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("~$", StringComparison.OrdinalIgnoreCase)) return true; // Office 临时文件
        return false;
    }

    /// <summary>构造显示名 — 快捷方式/URL 去后缀，文件夹与普通文件保留原名</summary>
    private static string BuildDisplayName(string fileName, bool isDir)
    {
        if (isDir) return fileName;
        var lower = fileName.ToLowerInvariant();
        if (lower.EndsWith(".lnk") || lower.EndsWith(".url"))
            return Path.GetFileNameWithoutExtension(fileName);
        return fileName;
    }

    /// <summary>提取文件/文件夹高清图标 → 冻结的 BitmapSource（跨线程安全）
    /// 优先使用 IShellItemImageFactory（与资源管理器同等清晰度），失败回退到 ExtractAssociatedIcon</summary>
    private static ImageSource? ExtractIcon(string path, bool isDir)
    {
        // 优先：IShellItemImageFactory — 48x48 高清，自动解析 .lnk 目标
        var icon = ShellIconInterop.GetIcon(path, 48);
        if (icon != null) return icon;

        // 回退：ExtractAssociatedIcon — 32x32（拉伸略糊但保证有图）
        try
        {
            using var sysIcon = Icon.ExtractAssociatedIcon(path);
            if (sysIcon == null) return null;

            var bmp = Imaging.CreateBitmapSourceFromHIcon(
                sysIcon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DesktopIconStore] 图标提取失败 {path}: {ex.Message}");
            return null;
        }
    }

    // ===== 布局持久化 =====

    /// <summary>加载图标布局配置，文件不存在或解析失败返回 null</summary>
    public IconLayoutConfig? LoadLayout()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(LayoutFilePath)) return null;
                var json = File.ReadAllText(LayoutFilePath);
                var config = JsonSerializer.Deserialize<IconLayoutConfig>(json, JsonOptions);
                if (config != null)
                {
                    // v2：图标改为列优先竖排，废弃旧的水平排列布局
                    if (config.Version < 2)
                    {
                        config.Version = 2;
                        Console.WriteLine("[DesktopIconStore] 已迁移 v1 → v2：废弃旧图标布局（竖排重排）");
                        return null;
                    }
                    Console.WriteLine($"[DesktopIconStore] 已加载图标布局：{config.Icons.Count} 项");
                }
                return config;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DesktopIconStore] 布局加载失败: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>保存图标布局配置</summary>
    public void SaveLayout(IconLayoutConfig config)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                config.SavedAt = DateTime.Now;
                var json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(LayoutFilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DesktopIconStore] 布局保存失败: {ex.Message}");
            }
        }
    }
}
