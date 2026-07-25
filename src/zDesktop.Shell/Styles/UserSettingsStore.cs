using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using zDesktop.Shell.Styles;

namespace zDesktop.Shell.Styles;

/// <summary>
/// 用户偏好持久化 — 主题预设、强调色、主题模式（手动/跟随系统）
///
/// 存储路径：%APPDATA%\zDesktop\settings.json
/// 线程安全：所有读写加锁
/// 由 <see cref="Theme"/> 启动时读取、切换时写回，实现重启后保留主题选择
/// </summary>
public static class UserSettingsStore
{
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");

    private static readonly string FilePath = Path.Combine(AppDataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly object _lock = new();

    /// <summary>主题模式：手动选择 / 跟随系统</summary>
    public enum ThemeMode
    {
        /// <summary>手动选择预设（深色或浅色）</summary>
        Manual = 0,

        /// <summary>跟随 Windows 系统暗色模式</summary>
        FollowSystem = 1,
    }

    /// <summary>持久化的用户偏好数据</summary>
    public sealed class UserSettings
    {
        /// <summary>主题模式</summary>
        public ThemeMode Mode { get; set; } = ThemeMode.Manual;

        /// <summary>手动模式下的主题预设</summary>
        public ThemePreset Preset { get; set; } = ThemePreset.MoYun;

        /// <summary>自定义强调色 ARGB（null 表示用预设默认主色）</summary>
        public uint? CustomAccentArgb { get; set; }

        /// <summary>界面语言代码（zh-CN / en-US / ja-JP）</summary>
        public string Language { get; set; } = "zh-CN";

        /// <summary>组件拖拽时吸附网格</summary>
        public bool WidgetSnapToGrid { get; set; } = true;

        /// <summary>组件拖拽时显示对齐辅助线</summary>
        public bool WidgetGuideLines { get; set; } = false;
    }

    /// <summary>加载用户偏好，文件不存在或解析失败返回默认值</summary>
    public static UserSettings Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(FilePath)) return new UserSettings();
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions);
                return s ?? new UserSettings();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UserSettings] 加载失败: {ex.Message}");
                return new UserSettings();
            }
        }
    }

    /// <summary>保存用户偏好</summary>
    public static void Save(UserSettings settings)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UserSettings] 保存失败: {ex.Message}");
            }
        }
    }

    /// <summary>便捷更新：读取当前设置 → 应用修改 → 保存</summary>
    public static void Update(Action<UserSettings> mutate)
    {
        var s = Load();
        mutate(s);
        Save(s);
    }
}
