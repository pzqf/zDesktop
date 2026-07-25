using System.IO;
using zDesktop.Core.Fences;

namespace zDesktop.Shell.Fences;

/// <summary>卸载时对分区内文件的处置方式（设计案 v3.1 §6.2）</summary>
public enum DispositionMode
{
    /// <summary>
    /// 保持现状（默认）—— 图标留在分区形成的位置上，只是分区框消失。
    /// 桌面看起来仍然整齐，这是伤害最小的默认值。
    /// </summary>
    KeepAsIs = 0,

    /// <summary>
    /// 收进文件夹 —— 每个分区在桌面上创建同名真实文件夹，把该分区的文件移进去。
    /// 桌面只剩几个文件夹。
    /// </summary>
    MoveIntoFolders = 1,

    /// <summary>
    /// 恢复安装前布局 —— 用最早的快照还原图标位置。
    /// </summary>
    RestoreOriginalLayout = 2,
}

/// <summary>一次处置的结果</summary>
/// <param name="Mode">采用的方式</param>
/// <param name="FoldersCreated">创建的文件夹数</param>
/// <param name="FilesMoved">移动的文件数</param>
/// <param name="IconsRestored">还原位置的图标数</param>
/// <param name="Failures">失败项说明</param>
public sealed record DispositionResult(
    DispositionMode Mode,
    int FoldersCreated,
    int FilesMoved,
    int IconsRestored,
    IReadOnlyList<string> Failures);

/// <summary>
/// 卸载时的用户资产处置（设计案 v3.1 §6.2）。
///
/// <para><b>为什么这是必须做的一环</b>：分区一旦消失，桌面上几十个图标会散落一地，
/// 用户会觉得「这软件把我桌面搞乱了」—— 即使技术上我们一个文件都没删。
/// 卸载体验决定用户对整个产品的最后印象。</para>
///
/// <para>供未来的卸载程序通过 <c>zDesktop.App.exe --uninstall-cleanup &lt;mode&gt;</c> 调用。</para>
/// </summary>
public sealed class UninstallDisposition
{
    private readonly FenceStore _store;
    private readonly FenceSnapshotStore _snapshots;
    private readonly NativeIconController _icons;
    private readonly DesktopItemResolver _resolver;

    public UninstallDisposition(FenceStore store, FenceSnapshotStore snapshots,
        NativeIconController icons, DesktopItemResolver resolver)
    {
        _store = store;
        _snapshots = snapshots;
        _icons = icons;
        _resolver = resolver;
    }

    /// <summary>执行处置</summary>
    public DispositionResult Execute(DispositionMode mode)
    {
        var failures = new List<string>();

        return mode switch
        {
            DispositionMode.MoveIntoFolders => MoveIntoFolders(failures),
            DispositionMode.RestoreOriginalLayout => RestoreOriginalLayout(failures),
            // 保持现状：什么都不做即可 —— 图标已经在分区排布好的位置上
            _ => new DispositionResult(DispositionMode.KeepAsIs, 0, 0, 0, failures),
        };
    }

    /// <summary>
    /// 把每个分区的文件移进桌面上的同名文件夹。
    ///
    /// 逐个文件独立 try/catch：一个文件被占用不应中断其余文件的迁移。
    /// 目标同名时自动加序号，绝不覆盖用户已有文件。
    /// </summary>
    private DispositionResult MoveIntoFolders(List<string> failures)
    {
        var config = _store.Load();
        var assignments = new FenceAssignmentModel(config.Assignments);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        var foldersCreated = 0;
        var filesMoved = 0;

        foreach (var fence in config.Fences)
        {
            var files = assignments.InFence(fence.Id);
            if (files.Count == 0) continue;

            var folder = Path.Combine(desktop, SanitizeFolderName(fence.Name));
            try
            {
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                    foldersCreated++;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"创建文件夹「{fence.Name}」失败: {ex.Message}");
                continue;
            }

            foreach (var a in files)
            {
                try
                {
                    if (!File.Exists(a.Path) && !Directory.Exists(a.Path)) continue;

                    var target = UniqueTarget(folder, Path.GetFileName(a.Path));
                    if (Directory.Exists(a.Path)) Directory.Move(a.Path, target);
                    else File.Move(a.Path, target);

                    filesMoved++;
                }
                catch (Exception ex)
                {
                    failures.Add($"移动「{Path.GetFileName(a.Path)}」失败: {ex.Message}");
                }
            }
        }

        return new DispositionResult(DispositionMode.MoveIntoFolders, foldersCreated, filesMoved, 0, failures);
    }

    /// <summary>用最早的快照还原图标位置</summary>
    private DispositionResult RestoreOriginalLayout(List<string> failures)
    {
        // 最早的快照才是「安装前」的样子，最近的那份是最后一次整理前的
        var earliest = _snapshots.List().LastOrDefault();
        if (earliest == null)
        {
            failures.Add("没有可用的快照，无法还原安装前布局");
            return new DispositionResult(DispositionMode.RestoreOriginalLayout, 0, 0, 0, failures);
        }

        var snapshot = _snapshots.Load(earliest.Id);
        if (snapshot == null)
        {
            failures.Add($"快照 {earliest.Id} 读取失败");
            return new DispositionResult(DispositionMode.RestoreOriginalLayout, 0, 0, 0, failures);
        }

        if (!_icons.EnsureConnected())
        {
            failures.Add("无法连接桌面图标层");
            return new DispositionResult(DispositionMode.RestoreOriginalLayout, 0, 0, 0, failures);
        }

        _resolver.Refresh();
        var pathToIndex = _resolver.ResolveAll(_icons.ReadAll());

        var writes = new List<(int, IconPoint)>();
        foreach (var (path, point) in snapshot.IconPositions)
        {
            if (pathToIndex.TryGetValue(path, out var index))
                writes.Add((index, point.ToIconPoint()));
        }

        var restored = _icons.SetPositions(writes);
        if (restored < writes.Count)
            failures.Add($"{writes.Count - restored} 个图标位置还原失败");

        return new DispositionResult(DispositionMode.RestoreOriginalLayout, 0, 0, restored, failures);
    }

    /// <summary>目标已存在时加序号，绝不覆盖用户文件</summary>
    private static string UniqueTarget(string folder, string fileName)
    {
        var target = Path.Combine(folder, fileName);
        if (!File.Exists(target) && !Directory.Exists(target)) return target;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    /// <summary>去掉文件名非法字符，空名回退为「分区」</summary>
    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "分区" : cleaned;
    }
}
