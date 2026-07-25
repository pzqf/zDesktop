using System.IO;
using zDesktop.Core.Fences;

namespace zDesktop.Shell.Fences;

/// <summary>
/// 把 ListView 的**显示名**解析为文件**路径**。
///
/// <para><b>为什么必须有这一层</b>（M2 spike 结论第 3 条遗留项）：
/// <c>LVM_GETITEMTEXTW</c> 返回的是显示名，不是路径。在「隐藏已知文件类型扩展名」
/// 开启时，<c>报告.docx</c> 和 <c>报告.pdf</c> 的显示名都是「报告」；
/// 而「此电脑」「回收站」这类命名空间扩展根本没有文件路径。</para>
///
/// <para><b>安全原则</b>：解析不出唯一路径时**一律不处理该图标**。
/// 归属记录以路径为键，猜错路径会把用户的 A 文件按 B 文件的规则搬走 ——
/// 宁可少管一个图标，也不能动错文件。</para>
/// </summary>
public sealed class DesktopItemResolver
{
    /// <summary>显示名 → 候选路径。多于一个即为歧义。</summary>
    private readonly Dictionary<string, List<string>> _byDisplayName =
        new(StringComparer.CurrentCultureIgnoreCase);

    /// <summary>路径 → 元数据快照，供规则匹配使用</summary>
    private readonly Dictionary<string, FileSnapshot> _snapshots =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本次扫描到的全部桌面文件路径</summary>
    public IReadOnlyCollection<string> AllPaths => _snapshots.Keys;

    /// <summary>路径 → 元数据</summary>
    public IReadOnlyDictionary<string, FileSnapshot> Snapshots => _snapshots;

    /// <summary>解析不出唯一路径的显示名（含虚拟项与重名文件），仅供诊断</summary>
    public List<string> Unresolved { get; } = new();

    /// <summary>扫描用户桌面与公共桌面，重建映射</summary>
    public void Refresh()
    {
        _byDisplayName.Clear();
        _snapshots.Clear();
        Unresolved.Clear();

        ScanDirectory(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));

        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrEmpty(common))
            ScanDirectory(common);
    }

    private void ScanDirectory(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ItemResolver] 无法枚举 {dir}: {ex.Message}");
            return;
        }

        foreach (var path in entries)
        {
            var name = Path.GetFileName(path);
            if (IsSkipped(name)) continue;

            DateTime modified;
            var isDirectory = false;
            try
            {
                modified = File.GetLastWriteTime(path);
                isDirectory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
            }
            catch
            {
                modified = DateTime.MinValue; // 权限不足等，按最旧处理，不影响归属本身
            }

            _snapshots[path] = FileSnapshot.Of(path, modified, isDirectory);

            // 显示名有两种可能形态：显示扩展名时是全名，隐藏时是主名。
            // 两种都登记，命中任一即可反查 —— 但只有唯一候选时才会被采用。
            Register(name, path);
            var stem = Path.GetFileNameWithoutExtension(path);
            if (!string.Equals(stem, name, StringComparison.Ordinal))
                Register(stem, path);
        }
    }

    private void Register(string displayName, string path)
    {
        if (string.IsNullOrEmpty(displayName)) return;

        if (!_byDisplayName.TryGetValue(displayName, out var list))
            _byDisplayName[displayName] = list = new List<string>();

        if (!list.Contains(path, StringComparer.OrdinalIgnoreCase))
            list.Add(path);
    }

    private static bool IsSkipped(string name)
        => string.IsNullOrEmpty(name)
        || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
        || name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("~$", StringComparison.Ordinal);

    /// <summary>
    /// 解析显示名 → 路径。歧义或无对应文件时返回 null。
    /// </summary>
    public string? Resolve(string displayName)
    {
        if (string.IsNullOrEmpty(displayName)) return null;

        if (!_byDisplayName.TryGetValue(displayName, out var candidates) || candidates.Count == 0)
        {
            // 「此电脑」「回收站」等命名空间扩展走这条路 —— 属预期情况
            if (!Unresolved.Contains(displayName)) Unresolved.Add(displayName);
            return null;
        }

        if (candidates.Count > 1)
        {
            // 隐藏扩展名时的同名文件。宁可不管，也不能猜
            if (!Unresolved.Contains(displayName)) Unresolved.Add(displayName);
            return null;
        }

        return candidates[0];
    }

    /// <summary>
    /// 把一批图标快照解析为「路径 → 索引」。解析失败的图标被安静跳过。
    /// </summary>
    public Dictionary<string, int> ResolveAll(IEnumerable<DesktopIconSnapshot> icons)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var icon in icons)
        {
            var path = Resolve(icon.DisplayName);
            if (path != null) map[path] = icon.Index;
        }

        return map;
    }
}
