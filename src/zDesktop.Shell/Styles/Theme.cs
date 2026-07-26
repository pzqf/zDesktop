using System.Windows;
using System.Windows.Media;

namespace zDesktop.Shell.Styles;

/// <summary>
/// 主题预设
/// </summary>
public enum ThemePreset
{
    /// <summary>墨韵 — 深色水墨风：墨黑底 + 靛蓝/朱砂/赭石强调，宋体标题</summary>
    MoYun,

    /// <summary>浅草 — 浅色宣纸风：米白底 + 松绿/竹青/青瓷强调，楷体标题</summary>
    QianCao,
}

/// <summary>
/// 全局视觉样式系统 — 支持双主题预设运行时切换
///
/// 设计理念：
/// - 墨韵（默认）：深色水墨底色 + 靛蓝主色 + 朱砂/赭石/竹青语义色，营造沉静雅致的中式质感
/// - 浅草（可选）：宣纸米白底色 + 松绿主色 + 青瓷/赭黄语义色，清新通透
///
/// 技术实现：
/// 所有笔刷为非冻结的 SolidColorBrush 实例，ApplyPreset 修改其 Color 属性时，
/// WPF 的依赖属性通知机制会自动刷新所有引用该笔刷的控件，实现全局即时换肤。
/// </summary>
public static class Theme
{
    // ===== 当前预设 =====

    private static ThemePreset _currentPreset = ThemePreset.MoYun;

    /// <summary>当前主题预设</summary>
    public static ThemePreset CurrentPreset => _currentPreset;

    /// <summary>当前主题模式（手动/跟随系统）— 供设置页回显选中态</summary>
    public static UserSettingsStore.ThemeMode CurrentMode { get; private set; } = UserSettingsStore.ThemeMode.Manual;

    /// <summary>主题切换事件 — 切换后触发，供需要额外刷新的组件订阅</summary>
    public static event Action? ThemeChanged;

    // ===== 基础色板（属性，随预设切换）=====

    /// <summary>应用底色</summary>
    public static Color Background { get; private set; }

    /// <summary>卡片底色</summary>
    public static Color Card { get; private set; }

    /// <summary>前景文字</summary>
    public static Color Foreground { get; private set; }

    /// <summary>主强调色</summary>
    public static Color Primary { get; private set; }

    /// <summary>次要底色</summary>
    public static Color Muted { get; private set; }

    /// <summary>次要文字</summary>
    public static Color MutedForeground { get; private set; }

    /// <summary>边框</summary>
    public static Color Border { get; private set; }

    /// <summary>成功</summary>
    public static Color Success { get; private set; }

    /// <summary>警告</summary>
    public static Color Warning { get; private set; }

    /// <summary>错误</summary>
    public static Color Error { get; private set; }

    /// <summary>信息</summary>
    public static Color Info { get; private set; }

    /// <summary>强调色预设-粉</summary>
    public static Color AccentPink { get; private set; }

    /// <summary>强调色预设-青</summary>
    public static Color AccentTeal { get; private set; }

    /// <summary>强调色预设-橙</summary>
    public static Color AccentOrange { get; private set; }

    /// <summary>阴影色（DropShadowEffect 专用）</summary>
    public static Color ShadowColor { get; private set; }

    // ===== 可变笔刷（非冻结，切换预设时自动刷新所有引用控件）=====

    /// <summary>组件容器背景</summary>
    public static readonly SolidColorBrush ContainerBackground = new();

    /// <summary>组件容器边框</summary>
    public static readonly SolidColorBrush ContainerBorder = new();

    /// <summary>标题栏背景</summary>
    public static readonly SolidColorBrush HeaderBackground = new();

    /// <summary>内部分区/分隔线</summary>
    public static readonly SolidColorBrush Divider = new();

    /// <summary>主要文字</summary>
    public static readonly SolidColorBrush TextPrimary = new();

    /// <summary>常规文字</summary>
    public static readonly SolidColorBrush TextRegular = new();

    /// <summary>次要文字</summary>
    public static readonly SolidColorBrush TextSecondary = new();

    /// <summary>占位/极弱文字</summary>
    public static readonly SolidColorBrush TextFaint = new();

    /// <summary>主色笔刷（实色）</summary>
    public static readonly SolidColorBrush PrimaryBrush = new();

    /// <summary>主色笔刷（85%）</summary>
    public static readonly SolidColorBrush PrimaryAccent = new();

    /// <summary>主色笔刷（浅底）</summary>
    public static readonly SolidColorBrush PrimarySubtle = new();

    /// <summary>成功色笔刷</summary>
    public static readonly SolidColorBrush SuccessBrush = new();

    /// <summary>输入框/次级按钮底色</summary>
    public static readonly SolidColorBrush InputBackground = new();

    /// <summary>输入框边框</summary>
    public static readonly SolidColorBrush InputBorder = new();

    /// <summary>列表项底色</summary>
    public static readonly SolidColorBrush ListItemBackground = new();

    /// <summary>列表项底色（禁用）</summary>
    public static readonly SolidColorBrush ListItemMuted = new();

    /// <summary>图表底色</summary>
    public static readonly SolidColorBrush ChartBackground = new();

    // ===== 字体 =====

    /// <summary>正文字体（无衬线 — 雅黑/PingFang/Inter）</summary>
    public static readonly FontFamily UiFont = new("Inter, PingFang SC, Microsoft YaHei UI, Microsoft YaHei, system-ui");

    /// <summary>标题字体（衬线 — 宋体/思源宋体，营造中式书卷气）</summary>
    public static readonly FontFamily TitleFont = new("Source Han Serif SC, Noto Serif SC, Songti SC, SimSun, 宋体, Microsoft YaHei UI, serif");

    /// <summary>等宽数字字体</summary>
    public static readonly FontFamily MonoFont = new("Cascadia Code, Consolas, Courier New, monospace");

    // ===== 圆角 =====

    public const double RadiusSm = 6;
    public const double RadiusMd = 10;
    public const double RadiusLg = 16;

    /// <summary>组件容器圆角</summary>
    public static readonly CornerRadius ContainerRadius = new(RadiusLg);

    /// <summary>标题栏顶部圆角</summary>
    public static readonly CornerRadius HeaderRadius = new(RadiusLg, RadiusLg, 0, 0);

    /// <summary>按钮/输入框圆角</summary>
    public static readonly CornerRadius ControlRadius = new(RadiusMd);

    /// <summary>小元素圆角</summary>
    public static readonly CornerRadius SmallRadius = new(RadiusSm);

    // ===== 预设色板定义 =====

    /// <summary>预设色板数据</summary>
    private struct Palette
    {
        public Color Background, Card, Foreground, Primary, Muted, MutedForeground, Border;
        public Color Success, Warning, Error, Info;
        public Color AccentPink, AccentTeal, AccentOrange, ShadowColor;

        // 笔刷 ARGB（alpha + RGB，深色主题用白色叠加，浅色主题用墨色叠加）
        public Color ContainerBackground, ContainerBorder, HeaderBackground, Divider;
        public Color TextPrimary, TextRegular, TextSecondary, TextFaint;
        public Color PrimaryAccent, PrimarySubtle;
        public Color InputBackground, InputBorder, ListItemBackground, ListItemMuted, ChartBackground;
    }

    /// <summary>墨韵色板 — 深色水墨</summary>
    private static readonly Palette MoYun = new()
    {
        // 基础色 — 深色水墨底 + 靛蓝主色
        Background = Color.FromRgb(0x13, 0x11, 0x1A),     // 深墨
        Card = Color.FromRgb(0x1C, 0x1A, 0x26),           // 砚石
        Foreground = Color.FromRgb(0xE8, 0xE4, 0xD9),     // 宣纸白（暖）
        Primary = Color.FromRgb(0x4A, 0x6F, 0xA5),        // 靛蓝
        Muted = Color.FromRgb(0x2A, 0x27, 0x38),          // 次级底
        MutedForeground = Color.FromRgb(0x9C, 0x97, 0xA8),// 次级文字（冷灰）
        Border = Color.FromRgb(0x32, 0x2F, 0x42),         // 边框
        Success = Color.FromRgb(0x6B, 0x9B, 0x6E),        // 竹青
        Warning = Color.FromRgb(0xC8, 0x80, 0x4A),        // 赭石
        Error = Color.FromRgb(0xC0, 0x39, 0x2B),          // 朱砂
        Info = Color.FromRgb(0x5B, 0x8B, 0xB0),           // 天青
        AccentPink = Color.FromRgb(0xD1, 0x7B, 0x88),     // 桃红
        AccentTeal = Color.FromRgb(0x6B, 0x9B, 0x8E),     // 青瓷
        AccentOrange = Color.FromRgb(0xD8, 0x8A, 0x3A),   // 橘黄
        ShadowColor = Color.FromRgb(0x00, 0x00, 0x00),

        // 笔刷 — 白色叠加（深色底用白线分层）
        // 容器透明度对齐浅色主题的 0xE8：原来的 0xD2（82%）在壁纸上还行，
        // 一旦组件压住桌面图标，18% 的高对比图案透上来就把文字搅糊了。
        // 我们没有背后模糊可用，只能靠不透明度保可读性。
        ContainerBackground = Color.FromArgb(0xE8, 0x1C, 0x1A, 0x26),
        ContainerBorder = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF),
        HeaderBackground = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
        Divider = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF),
        TextPrimary = Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF),
        TextRegular = Color.FromArgb(0xC7, 0xFF, 0xFF, 0xFF),
        TextSecondary = Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF),
        TextFaint = Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF),
        PrimaryAccent = Color.FromArgb(0xD9, 0x4A, 0x6F, 0xA5),
        PrimarySubtle = Color.FromArgb(0x38, 0x4A, 0x6F, 0xA5),
        InputBackground = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
        InputBorder = Color.FromArgb(0xFF, 0x32, 0x2F, 0x42),
        ListItemBackground = Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF),
        ListItemMuted = Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF),
        ChartBackground = Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF),
    };

    /// <summary>浅草色板 — 浅色宣纸</summary>
    private static readonly Palette QianCao = new()
    {
        // 基础色 — 宣纸米白底 + 松绿主色
        Background = Color.FromRgb(0xF2, 0xEF, 0xE6),     // 宣纸
        Card = Color.FromRgb(0xFB, 0xF9, 0xF2),           // 暖白
        Foreground = Color.FromRgb(0x2A, 0x26, 0x20),     // 墨黑（暖）
        Primary = Color.FromRgb(0x4A, 0x7C, 0x59),        // 松绿
        Muted = Color.FromRgb(0xE8, 0xE3, 0xD6),          // 次级底
        MutedForeground = Color.FromRgb(0x7A, 0x74, 0x68),// 次级文字（暖灰）
        Border = Color.FromRgb(0xD5, 0xCF, 0xC0),         // 边框
        Success = Color.FromRgb(0x5C, 0x8A, 0x5E),        // 竹青
        Warning = Color.FromRgb(0xB8, 0x84, 0x3A),        // 赭黄
        Error = Color.FromRgb(0xC0, 0x39, 0x2B),          // 朱砂
        Info = Color.FromRgb(0x4A, 0x7B, 0x9A),           // 青蓝
        AccentPink = Color.FromRgb(0xC7, 0x6B, 0x7E),     // 桃红
        AccentTeal = Color.FromRgb(0x5A, 0x8B, 0x7E),     // 青瓷
        AccentOrange = Color.FromRgb(0xC7, 0x7A, 0x2E),   // 橘黄
        ShadowColor = Color.FromRgb(0x2A, 0x26, 0x20),

        // 笔刷 — 墨色叠加（浅色底用墨线分层）
        ContainerBackground = Color.FromArgb(0xE8, 0xFB, 0xF9, 0xF2),
        ContainerBorder = Color.FromArgb(0x20, 0x2A, 0x26, 0x20),
        HeaderBackground = Color.FromArgb(0x0A, 0x2A, 0x26, 0x20),
        Divider = Color.FromArgb(0x14, 0x2A, 0x26, 0x20),
        TextPrimary = Color.FromArgb(0xE0, 0x2A, 0x26, 0x20),
        TextRegular = Color.FromArgb(0xB8, 0x2A, 0x26, 0x20),
        TextSecondary = Color.FromArgb(0x80, 0x2A, 0x26, 0x20),
        TextFaint = Color.FromArgb(0x4D, 0x2A, 0x26, 0x20),
        PrimaryAccent = Color.FromArgb(0xCC, 0x4A, 0x7C, 0x59),
        PrimarySubtle = Color.FromArgb(0x24, 0x4A, 0x7C, 0x59),
        InputBackground = Color.FromArgb(0x0A, 0x2A, 0x26, 0x20),
        InputBorder = Color.FromArgb(0xFF, 0xD5, 0xCF, 0xC0),
        ListItemBackground = Color.FromArgb(0x08, 0x2A, 0x26, 0x20),
        ListItemMuted = Color.FromArgb(0x05, 0x2A, 0x26, 0x20),
        ChartBackground = Color.FromArgb(0x07, 0x2A, 0x26, 0x20),
    };

    // ===== 静态初始化 — 从持久化设置恢复上次主题 =====

    static Theme()
    {
        var s = UserSettingsStore.Load();
        CurrentMode = s.Mode;

        // 跟随系统模式：根据 Windows 暗色偏好决定预设
        var preset = s.Mode == UserSettingsStore.ThemeMode.FollowSystem
            ? (IsSystemDarkMode() ? ThemePreset.MoYun : ThemePreset.QianCao)
            : s.Preset;

        ApplyPreset(preset);

        // 恢复自定义强调色（若有）
        if (s.CustomAccentArgb.HasValue)
        {
            var argb = s.CustomAccentArgb.Value;
            ApplyAccent(Color.FromArgb(
                (byte)((argb >> 24) & 0xFF),
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF)));
        }

        Console.WriteLine($"[Theme] 已恢复主题：模式={s.Mode}，预设={preset}" +
                          (s.CustomAccentArgb.HasValue ? "，自定义强调色" : ""));
    }

    /// <summary>
    /// 切换主题预设 — 即时更新所有笔刷颜色，WPF 自动刷新引用控件，并持久化到 settings.json
    /// </summary>
    /// <param name="preset">目标预设</param>
    /// <param name="persist">是否写入持久化（默认 true；静态构造恢复时传 false 避免重复写盘）</param>
    public static void ApplyPreset(ThemePreset preset, bool persist = true)
    {
        _currentPreset = preset;
        // ApplyPreset 视为手动选择，清除自定义强调色（回到预设主色）
        var p = preset == ThemePreset.QianCao ? QianCao : MoYun;

        // 更新基础色板
        Background = p.Background;
        Card = p.Card;
        Foreground = p.Foreground;
        Primary = p.Primary;
        Muted = p.Muted;
        MutedForeground = p.MutedForeground;
        Border = p.Border;
        Success = p.Success;
        Warning = p.Warning;
        Error = p.Error;
        Info = p.Info;
        AccentPink = p.AccentPink;
        AccentTeal = p.AccentTeal;
        AccentOrange = p.AccentOrange;
        ShadowColor = p.ShadowColor;

        // 更新笔刷颜色（非冻结笔刷的 Color 变更会自动通知所有引用控件）
        ContainerBackground.Color = p.ContainerBackground;
        ContainerBorder.Color = p.ContainerBorder;
        HeaderBackground.Color = p.HeaderBackground;
        Divider.Color = p.Divider;
        TextPrimary.Color = p.TextPrimary;
        TextRegular.Color = p.TextRegular;
        TextSecondary.Color = p.TextSecondary;
        TextFaint.Color = p.TextFaint;
        PrimaryBrush.Color = p.Primary;
        PrimaryAccent.Color = p.PrimaryAccent;
        PrimarySubtle.Color = p.PrimarySubtle;
        SuccessBrush.Color = p.Success;
        InputBackground.Color = p.InputBackground;
        InputBorder.Color = p.InputBorder;
        ListItemBackground.Color = p.ListItemBackground;
        ListItemMuted.Color = p.ListItemMuted;
        ChartBackground.Color = p.ChartBackground;

        // 持久化（手动选择预设时记录；跟随系统模式不覆盖手动预设记录）
        if (persist)
        {
            CurrentMode = UserSettingsStore.ThemeMode.Manual;
            UserSettingsStore.Update(s =>
            {
                s.Mode = UserSettingsStore.ThemeMode.Manual;
                s.Preset = preset;
                // 切换预设清除自定义强调色
                s.CustomAccentArgb = null;
            });
        }

        ThemeChanged?.Invoke();
    }

    /// <summary>
    /// 动态切换强调色（不切换整体预设）— 用户在外观设置点击色板时调用
    /// 直接覆盖 Primary 及其衍生笔刷，即时刷新所有引用控件，并持久化。
    /// 切换预设（ApplyPreset）时会重置回预设默认强调色。
    /// </summary>
    public static void ApplyAccent(Color accent)
    {
        Primary = accent;
        PrimaryBrush.Color = accent;
        // PrimaryAccent：85% 不透明度
        PrimaryAccent.Color = Color.FromArgb(0xD9, accent.R, accent.G, accent.B);
        // PrimarySubtle：低不透明度浅底（深色主题 0x38，浅色主题 0x24）
        var isDark = _currentPreset == ThemePreset.MoYun;
        PrimarySubtle.Color = Color.FromArgb(isDark ? (byte)0x38 : (byte)0x24, accent.R, accent.G, accent.B);

        // 持久化自定义强调色
        var argb = ((uint)accent.A << 24) | ((uint)accent.R << 16) | ((uint)accent.G << 8) | accent.B;
        UserSettingsStore.Update(s => s.CustomAccentArgb = argb);

        ThemeChanged?.Invoke();
    }

    /// <summary>
    /// 切换为「跟随系统」模式 — 根据 Windows 暗色偏好决定预设，并持久化模式标记
    /// </summary>
    public static void ApplyFollowSystem()
    {
        var dark = IsSystemDarkMode();
        var preset = dark ? ThemePreset.MoYun : ThemePreset.QianCao;
        CurrentMode = UserSettingsStore.ThemeMode.FollowSystem;
        _currentPreset = preset;
        var p = preset == ThemePreset.QianCao ? QianCao : MoYun;

        // 应用预设颜色（不通过 ApplyPreset 持久化，单独记录模式）
        Background = p.Background;
        Card = p.Card;
        Foreground = p.Foreground;
        Primary = p.Primary;
        Muted = p.Muted;
        MutedForeground = p.MutedForeground;
        Border = p.Border;
        Success = p.Success;
        Warning = p.Warning;
        Error = p.Error;
        Info = p.Info;
        AccentPink = p.AccentPink;
        AccentTeal = p.AccentTeal;
        AccentOrange = p.AccentOrange;
        ShadowColor = p.ShadowColor;

        ContainerBackground.Color = p.ContainerBackground;
        ContainerBorder.Color = p.ContainerBorder;
        HeaderBackground.Color = p.HeaderBackground;
        Divider.Color = p.Divider;
        TextPrimary.Color = p.TextPrimary;
        TextRegular.Color = p.TextRegular;
        TextSecondary.Color = p.TextSecondary;
        TextFaint.Color = p.TextFaint;
        PrimaryBrush.Color = p.Primary;
        PrimaryAccent.Color = p.PrimaryAccent;
        PrimarySubtle.Color = p.PrimarySubtle;
        SuccessBrush.Color = p.Success;
        InputBackground.Color = p.InputBackground;
        InputBorder.Color = p.InputBorder;
        ListItemBackground.Color = p.ListItemBackground;
        ListItemMuted.Color = p.ListItemMuted;
        ChartBackground.Color = p.ChartBackground;

        // 持久化模式（保留用户上次手动预设记录，但标记为跟随系统）
        UserSettingsStore.Update(s => s.Mode = UserSettingsStore.ThemeMode.FollowSystem);

        ThemeChanged?.Invoke();
    }

    /// <summary>读取 Windows 系统暗色模式偏好（AppsUseLightTheme=0 为深色）</summary>
    public static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v == 0; // 0=深色，1=浅色
        }
        catch { /* 读取失败默认深色 */ }
        return true;
    }
}
