namespace zDesktop.Core.DesktopIcons;

/// <summary>
/// 桌面图标静态信息 — 描述一个桌面项的元数据
/// 不含 WPF 类型，可在 Core 层使用
/// </summary>
public sealed class DesktopIconInfo
{
    /// <summary>桌面上的原始文件/文件夹路径（唯一键，用于持久化与删除/重命名）</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>显示名称（.lnk/.url 去后缀，文件夹用原名）</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>是否为快捷方式（.lnk）</summary>
    public bool IsShortcut { get; set; }

    /// <summary>是否为文件夹</summary>
    public bool IsDirectory { get; set; }

    /// <summary>是否为公共桌面项（来自 Common Desktop，通常不可由用户删除）</summary>
    public bool IsCommon { get; set; }
}
