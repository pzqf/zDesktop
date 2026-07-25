using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using zDesktop.Core.Fences;

namespace zDesktop.Shell.Fences;

/// <summary>
/// 分区配置持久化 —— <c>%APPDATA%\zDesktop\fences.json</c>（设计案 v3.1 §五）。
///
/// 损坏时的处理与 LayoutStore 一致：备份为 <c>.bak</c> 后按空配置启动，
/// 绝不让一个坏掉的 JSON 阻断整个程序（§七 失败降级矩阵）。
/// </summary>
public sealed class FenceStore
{
    private static readonly string DefaultDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();
    private readonly string _dir;
    private readonly string _filePath;

    /// <param name="directory">存储目录；null 表示 <c>%APPDATA%\zDesktop</c></param>
    public FenceStore(string? directory = null)
    {
        _dir = directory ?? DefaultDir;
        _filePath = Path.Combine(_dir, "fences.json");
    }

    /// <summary>配置文件路径（诊断用）</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// 加载分区配置。文件不存在返回空配置（不是 null）——
    /// 「还没有分区」是正常状态，调用方不该为此写空判断。
    /// </summary>
    public FenceConfig Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_filePath)) return new FenceConfig();

                var json = File.ReadAllText(_filePath);
                var config = JsonSerializer.Deserialize<FenceConfig>(json, JsonOptions);
                if (config == null) return new FenceConfig();

                var repaired = Sanitize(config);
                Console.WriteLine($"[FenceStore] 已加载 {repaired.Fences.Count} 个分区 / " +
                                  $"{repaired.Assignments.Count} 条归属（v{repaired.Version}）");
                return repaired;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FenceStore] 加载失败，按空配置启动: {ex.Message}");
                BackupCorrupted();
                return new FenceConfig();
            }
        }
    }

    /// <summary>
    /// 修复加载后的配置：剔除 Id 为空的分区、指向不存在分区的自动归属、重复归属。
    ///
    /// 手动归属即使分区已不存在也保留（降级为无分区），理由见
    /// <see cref="FenceAssignmentModel.PruneMissingFences"/>。
    /// </summary>
    private static FenceConfig Sanitize(FenceConfig config)
    {
        config.Fences.RemoveAll(f => string.IsNullOrWhiteSpace(f.Id));

        foreach (var f in config.Fences)
        {
            f.Rect ??= new FenceRect();
            f.Rules ??= new List<FenceRule>();
            // 尺寸为 0 的分区点不中也拖不动，给一个可操作的最小尺寸
            if (f.Rect.Width < 40) f.Rect.Width = 320;
            if (f.Rect.Height < 40) f.Rect.Height = 240;
        }

        var model = new FenceAssignmentModel(config.Assignments);
        model.PruneMissingFences(config.Fences.Select(f => f.Id));
        config.Assignments = model.ToList();

        return config;
    }

    /// <summary>保存分区配置</summary>
    public void Save(FenceConfig config)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                config.SavedAt = DateTime.Now;
                config.Version = FenceConfig.CurrentVersion;

                // 先写临时文件再替换：写一半断电不会留下半个配置
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FenceStore] 保存失败: {ex.Message}");
            }
        }
    }

    private void BackupCorrupted()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var bak = _filePath + ".bak";
            File.Copy(_filePath, bak, overwrite: true);
            Console.WriteLine($"[FenceStore] 损坏的配置已备份到 {bak}");
        }
        catch
        {
            // 备份失败不影响主流程
        }
    }
}
