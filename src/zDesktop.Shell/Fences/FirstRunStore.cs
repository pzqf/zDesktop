using System.IO;
using System.Text.Json;

namespace zDesktop.Shell.Fences;

/// <summary>
/// 首次运行状态（设计案 v3.1 §六）。
///
/// <para>「以后再说」之后不再弹第二次 —— 反复弹同一张引导卡片是最快让用户
/// 关掉开机自启的方式之一。</para>
/// </summary>
public sealed class FirstRunStore
{
    private sealed class State
    {
        /// <summary>引导卡片是否已展示过（无论用户选了什么）</summary>
        public bool OnboardingShown { get; set; }

        /// <summary>用户是否已完成过一次整理</summary>
        public bool OrganizedOnce { get; set; }

        public DateTime? FirstRunAt { get; set; }
    }

    private static readonly string DefaultDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private State _state = new();

    public FirstRunStore(string? directory = null)
    {
        _path = Path.Combine(directory ?? DefaultDir, "first-run.json");
        Load();
    }

    /// <summary>是否应当展示引导卡片</summary>
    public bool ShouldShowOnboarding => !_state.OnboardingShown;

    /// <summary>是否已完成过整理</summary>
    public bool HasOrganized => _state.OrganizedOnce;

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path), JsonOptions) ?? new State();
        }
        catch (Exception ex)
        {
            // 状态文件损坏时按「首次运行」处理：大不了多弹一次引导，不该阻断启动
            Console.WriteLine($"[FirstRun] 读取失败，按首次运行处理: {ex.Message}");
            _state = new State();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_state, JsonOptions));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FirstRun] 保存失败: {ex.Message}");
        }
    }

    /// <summary>标记引导已展示（用户选「预览效果」或「以后再说」都算）</summary>
    public void MarkOnboardingShown()
    {
        if (_state.OnboardingShown) return;

        _state.OnboardingShown = true;
        _state.FirstRunAt ??= DateTime.Now;
        Save();
    }

    /// <summary>标记已完成过一次整理</summary>
    public void MarkOrganized()
    {
        if (_state.OrganizedOnce) return;

        _state.OrganizedOnce = true;
        Save();
    }
}
