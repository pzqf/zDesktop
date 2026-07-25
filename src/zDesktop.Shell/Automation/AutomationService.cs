using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualBasic.FileIO;

namespace zDesktop.Shell.Automation;

// ============================================================
//  枚举定义
// ============================================================

/// <summary>规则条件所针对的文件字段</summary>
public enum ConditionField
{
    /// <summary>文件名（含扩展名）</summary>
    FileName,

    /// <summary>扩展名（含点，如 .psd）</summary>
    Extension,

    /// <summary>文件大小（字节）</summary>
    Size,

    /// <summary>最后修改时间</summary>
    ModifiedDate,

    /// <summary>创建时间</summary>
    CreationDate,
}

/// <summary>条件运算符</summary>
public enum ConditionOperator
{
    /// <summary>包含</summary>
    Contains,

    /// <summary>等于</summary>
    Equals,

    /// <summary>开头是</summary>
    StartsWith,

    /// <summary>结尾是</summary>
    EndsWith,

    /// <summary>大于（数值/日期/字符串序）</summary>
    GreaterThan,

    /// <summary>小于（数值/日期/字符串序）</summary>
    LessThan,

    /// <summary>早于（天数，用于日期字段）</summary>
    OlderThan,
}

/// <summary>规则动作类型</summary>
public enum ActionType
{
    /// <summary>移动到目标目录</summary>
    Move,

    /// <summary>复制到目标目录</summary>
    Copy,

    /// <summary>永久删除（需规则 <see cref="AutomationRule.IsDestructive"/> 显式标记）</summary>
    Delete,

    /// <summary>按 NameTemplate 重命名</summary>
    Rename,

    /// <summary>移入回收站</summary>
    Recycle,

    /// <summary>启动外部程序并传入文件路径</summary>
    OpenApp,
}

/// <summary>规则执行结果状态</summary>
public enum ExecutionStatus
{
    /// <summary>成功</summary>
    Success,

    /// <summary>跳过（条件不匹配或安全拦截）</summary>
    Skipped,

    /// <summary>失败</summary>
    Failed,
}

// ============================================================
//  数据模型
// ============================================================

/// <summary>
/// 单条规则条件 — 字段 + 运算符 + 值
/// </summary>
public sealed class RuleCondition
{
    /// <summary>针对的文件字段</summary>
    public ConditionField Field { get; set; } = ConditionField.FileName;

    /// <summary>运算符</summary>
    public ConditionOperator Operator { get; set; } = ConditionOperator.Contains;

    /// <summary>比较值（字符串；Size 为数字；OlderThan 为天数）</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// 单条规则动作
/// </summary>
public sealed class RuleAction
{
    /// <summary>动作类型</summary>
    public ActionType Type { get; set; } = ActionType.Move;

    /// <summary>目标路径（Move/Copy 为目录，OpenApp 为可执行文件路径）</summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>名称模板（Rename 使用，支持 {日期} {时间} {原名} {扩展名} 变量）</summary>
    public string NameTemplate { get; set; } = string.Empty;
}

/// <summary>
/// 自动化规则 — 监控某文件夹，满足条件时执行动作
/// </summary>
public sealed class AutomationRule
{
    /// <summary>唯一标识</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>规则名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; }

    /// <summary>监控文件夹路径</summary>
    public string WatchFolder { get; set; } = string.Empty;

    /// <summary>条件列表（AND 关系；为空表示匹配所有）</summary>
    public List<RuleCondition> Conditions { get; set; } = new();

    /// <summary>动作列表（按顺序执行）</summary>
    public List<RuleAction> Actions { get; set; } = new();

    /// <summary>是否允许执行永久删除（Delete 动作的显式安全标记）</summary>
    public bool IsDestructive { get; set; }

    /// <summary>上次执行时间</summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>累计执行次数</summary>
    public long RunCount { get; set; }
}

/// <summary>
/// 规则执行日志条目
/// </summary>
public sealed class RuleExecutionLog
{
    /// <summary>规则 Id</summary>
    public Guid RuleId { get; set; }

    /// <summary>规则名称（执行时快照）</summary>
    public string RuleName { get; set; } = string.Empty;

    /// <summary>触发时间</summary>
    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>目标文件路径</summary>
    public string TargetFile { get; set; } = string.Empty;

    /// <summary>执行状态</summary>
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Success;

    /// <summary>附加消息</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 规则模板 — 内置推荐配置，供用户一键创建
/// </summary>
public sealed class RuleTemplate
{
    /// <summary>模板名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>模板说明</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>预填充规则（Enabled 默认 false，用户确认后再启用）</summary>
    public AutomationRule Rule { get; set; } = new();
}

// ============================================================
//  服务
// ============================================================

/// <summary>
/// 自动化任务规则引擎服务 — 类似 macOS Hazel 的桌面文件自动化
///
/// 职责：
/// - 规则的增删改查与 JSON 持久化（%APPDATA%\zDesktop\automation-rules.json）
/// - 内置推荐模板（<see cref="GetTemplates"/>）
/// - 为每条启用规则挂载 <see cref="FileSystemWatcher"/>，文件创建/修改/重命名时触发评估
/// - 条件评估（<see cref="EvaluateConditions"/>，所有条件 AND 关系）
/// - 动作执行（<see cref="ExecuteAction"/>，含变量替换、同名冲突自动重命名、回收站）
/// - 执行日志（内存最近 500 条，<see cref="GetLogs"/>）
///
/// 线程安全：规则列表与日志均加锁；watcher 事件在后台线程触发，评估与执行容错。
/// </summary>
public sealed class AutomationService
{
    // ===== 持久化 =====

    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");

    private static readonly string FilePath = Path.Combine(AppDataDir, "automation-rules.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    // ===== 状态 =====

    private readonly object _lock = new();
    private List<AutomationRule> _rules = new();

    private readonly object _watcherLock = new();
    private readonly Dictionary<Guid, FileSystemWatcher> _watchers = new();

    private readonly object _logLock = new();
    private readonly List<RuleExecutionLog> _logs = new();

    /// <summary>最近处理过的文件路径去重表（避免 watcher 连续事件重复执行）</summary>
    private readonly ConcurrentDictionary<string, DateTime> _recent = new();

    /// <summary>规则集合变更通知 — UI 订阅后刷新列表</summary>
    public event Action? Changed;

    /// <summary>新增执行日志通知 — UI 订阅后刷新日志区</summary>
    public event Action? LogsChanged;

    public AutomationService()
    {
        Load();
    }

    // ============================================================
    //  CRUD
    // ============================================================

    /// <summary>获取所有规则（按名称排序的快照）</summary>
    public IReadOnlyList<AutomationRule> GetAll()
    {
        lock (_lock)
        {
            return _rules.OrderBy(r => r.Name).ToList();
        }
    }

    /// <summary>按 Id 获取规则（返回内存中的活引用）</summary>
    public AutomationRule? Get(Guid id)
    {
        lock (_lock)
        {
            return _rules.FirstOrDefault(r => r.Id == id);
        }
    }

    /// <summary>新增规则；若启用则立即挂载监控</summary>
    public AutomationRule Add(AutomationRule rule)
    {
        if (rule.Id == Guid.Empty) rule.Id = Guid.NewGuid();

        lock (_lock)
        {
            _rules.Add(rule);
        }

        Save();
        if (rule.Enabled) StartRule(rule);
        Changed?.Invoke();
        return rule;
    }

    /// <summary>更新规则；重启其监控以应用新配置</summary>
    public void Update(AutomationRule rule)
    {
        lock (_lock)
        {
            var idx = _rules.FindIndex(r => r.Id == rule.Id);
            if (idx < 0) return;
            _rules[idx] = rule;
        }

        StopRule(rule.Id);
        if (rule.Enabled) StartRule(rule);
        Save();
        Changed?.Invoke();
    }

    /// <summary>删除规则并卸载其监控</summary>
    public void Remove(Guid id)
    {
        lock (_lock)
        {
            _rules.RemoveAll(r => r.Id == id);
        }

        StopRule(id);
        Save();
        Changed?.Invoke();
    }

    /// <summary>切换单条规则启用状态并同步监控</summary>
    public void ToggleEnabled(Guid id)
    {
        AutomationRule? rule;
        lock (_lock)
        {
            rule = _rules.FirstOrDefault(r => r.Id == id);
            if (rule == null) return;
            rule.Enabled = !rule.Enabled;
        }

        StopRule(id);
        if (rule.Enabled) StartRule(rule);
        Save();
        Changed?.Invoke();
    }

    /// <summary>批量启用/禁用所有规则</summary>
    public void SetAllEnabled(bool enabled)
    {
        lock (_lock)
        {
            foreach (var r in _rules) r.Enabled = enabled;
        }

        Stop();
        if (enabled) Start();
        Save();
        Changed?.Invoke();
    }

    // ============================================================
    //  模板
    // ============================================================

    /// <summary>
    /// 内置推荐模板列表 — 每个模板预填一条规则（Enabled 默认 false）
    /// </summary>
    public static IReadOnlyList<RuleTemplate> GetTemplates()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return new List<RuleTemplate>
        {
            new()
            {
                Name = "同步设计稿到云端",
                Description = "监控设计稿目录，新增 .psd 文件自动复制到云端同步文件夹",
                Rule = new AutomationRule
                {
                    Name = "同步设计稿到云端",
                    Enabled = false,
                    WatchFolder = Path.Combine(userProfile, "Documents", "Design"),
                    Conditions = new List<RuleCondition>
                    {
                        new()
                        {
                            Field = ConditionField.Extension,
                            Operator = ConditionOperator.EndsWith,
                            Value = ".psd",
                        },
                    },
                    Actions = new List<RuleAction>
                    {
                        new()
                        {
                            Type = ActionType.Copy,
                            TargetPath = Path.Combine(userProfile, "OneDrive", "DesignSync"),
                        },
                    },
                },
            },
            new()
            {
                Name = "清理空文件夹",
                Description = "监控指定文件夹，将零字节空文件移入回收站，保持目录整洁",
                Rule = new AutomationRule
                {
                    Name = "清理空文件夹",
                    Enabled = false,
                    WatchFolder = desktop,
                    Conditions = new List<RuleCondition>
                    {
                        new()
                        {
                            Field = ConditionField.Size,
                            Operator = ConditionOperator.Equals,
                            Value = "0",
                        },
                    },
                    Actions = new List<RuleAction>
                    {
                        new() { Type = ActionType.Recycle },
                    },
                },
            },
            new()
            {
                Name = "批量重命名",
                Description = "按模板重命名新增文件，支持 {日期} {时间} {原名} {扩展名} 变量",
                Rule = new AutomationRule
                {
                    Name = "批量重命名",
                    Enabled = false,
                    WatchFolder = desktop,
                    Conditions = new List<RuleCondition>
                    {
                        new()
                        {
                            Field = ConditionField.Extension,
                            Operator = ConditionOperator.EndsWith,
                            Value = ".png",
                        },
                    },
                    Actions = new List<RuleAction>
                    {
                        new()
                        {
                            Type = ActionType.Rename,
                            NameTemplate = "{原名}_{日期}_{时间}",
                        },
                    },
                },
            },
            new()
            {
                Name = "按类型整理桌面",
                Description = "将桌面新增图片自动移动到「整理/图片」分类文件夹",
                Rule = new AutomationRule
                {
                    Name = "按类型整理桌面",
                    Enabled = false,
                    WatchFolder = desktop,
                    Conditions = new List<RuleCondition>
                    {
                        new()
                        {
                            Field = ConditionField.Extension,
                            Operator = ConditionOperator.EndsWith,
                            Value = ".png",
                        },
                    },
                    Actions = new List<RuleAction>
                    {
                        new()
                        {
                            Type = ActionType.Move,
                            TargetPath = Path.Combine(desktop, "整理", "图片"),
                        },
                    },
                },
            },
            new()
            {
                Name = "归档旧文件",
                Description = "将超过 30 天未修改的文件移动到归档目录",
                Rule = new AutomationRule
                {
                    Name = "归档旧文件",
                    Enabled = false,
                    WatchFolder = desktop,
                    Conditions = new List<RuleCondition>
                    {
                        new()
                        {
                            Field = ConditionField.ModifiedDate,
                            Operator = ConditionOperator.OlderThan,
                            Value = "30",
                        },
                    },
                    Actions = new List<RuleAction>
                    {
                        new()
                        {
                            Type = ActionType.Move,
                            TargetPath = Path.Combine(desktop, "归档"),
                        },
                    },
                },
            },
        };
    }

    // ============================================================
    //  监控生命周期
    // ============================================================

    /// <summary>启动所有启用规则的文件监控</summary>
    public void Start()
    {
        IReadOnlyList<AutomationRule> rules;
        lock (_lock) rules = _rules.ToList();

        var started = 0;
        foreach (var r in rules)
        {
            if (r.Enabled)
            {
                StartRule(r);
                started++;
            }
        }

        Console.WriteLine($"[Automation] 已启动 {started} 条规则监控");
    }

    /// <summary>停止所有文件监控</summary>
    public void Stop()
    {
        lock (_watcherLock)
        {
            foreach (var w in _watchers.Values)
            {
                try
                {
                    w.EnableRaisingEvents = false;
                    w.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Automation] 卸载监控失败: {ex.Message}");
                }
            }

            _watchers.Clear();
        }
    }

    /// <summary>为单条规则挂载监控（已存在则跳过；目录不存在记录日志）</summary>
    private void StartRule(AutomationRule rule)
    {
        lock (_watcherLock)
        {
            if (_watchers.ContainsKey(rule.Id)) return;

            if (string.IsNullOrWhiteSpace(rule.WatchFolder) || !Directory.Exists(rule.WatchFolder))
            {
                Console.WriteLine($"[Automation] 规则「{rule.Name}」监控目录不存在: {rule.WatchFolder}");
                return;
            }

            FileSystemWatcher watcher;
            try
            {
                watcher = new FileSystemWatcher(rule.WatchFolder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                   | NotifyFilters.Size | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true,
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Automation] 规则「{rule.Name}」监控初始化失败: {ex.Message}");
                return;
            }

            var ruleId = rule.Id;
            watcher.Created += (_, e) => OnFileEvent(ruleId, e.FullPath);
            watcher.Changed += (_, e) => OnFileEvent(ruleId, e.FullPath);
            watcher.Renamed += (_, e) => OnFileEvent(ruleId, e.FullPath);
            watcher.Error += (_, e) =>
                Console.WriteLine($"[Automation] watcher 错误: {e.GetException().Message}");

            _watchers[rule.Id] = watcher;
        }
    }

    /// <summary>卸载单条规则的监控</summary>
    private void StopRule(Guid id)
    {
        lock (_watcherLock)
        {
            if (_watchers.TryGetValue(id, out var w))
            {
                try
                {
                    w.EnableRaisingEvents = false;
                    w.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Automation] 卸载监控失败: {ex.Message}");
                }

                _watchers.Remove(id);
            }
        }
    }

    // ============================================================
    //  文件事件 → 规则评估
    // ============================================================

    /// <summary>watcher 事件入口（后台线程）</summary>
    private void OnFileEvent(Guid ruleId, string path)
    {
        // 路径去重：1 秒内同一规则+路径只处理一次
        var key = ruleId + "|" + path;
        var now = DateTime.UtcNow;
        if (_recent.TryGetValue(key, out var last) && (now - last).TotalSeconds < 1) return;
        _recent[key] = now;
        if (_recent.Count > 2000) _recent.Clear(); // 防止无限增长

        Task.Run(async () =>
        {
            try
            {
                // 等待文件写入完成
                await Task.Delay(300);

                var rule = Get(ruleId);
                if (rule == null || !rule.Enabled) return;
                ExecuteRule(rule, path);
            }
            catch (Exception ex)
            {
                AddLog(ruleId, string.Empty, path, ExecutionStatus.Failed, ex.Message);
            }
        });
    }

    /// <summary>对单个文件执行单条规则（条件评估 + 动作执行 + 日志 + 统计）</summary>
    private void ExecuteRule(AutomationRule rule, string filePath)
    {
        try
        {
            // 仅处理文件（目录变化交给安全动作）
            if (!File.Exists(filePath))
            {
                // 目录路径：仅当动作是 Recycle/Delete 时有意义，这里跳过常规触发
                return;
            }

            if (!EvaluateConditions(filePath, rule.Conditions))
            {
                AddLog(rule.Id, rule.Name, filePath, ExecutionStatus.Skipped, "条件不匹配");
                return;
            }

            var success = 0;
            var failed = 0;
            foreach (var action in rule.Actions)
            {
                try
                {
                    if (ExecuteAction(filePath, action, rule))
                        success++;
                    else
                        failed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    AddLog(rule.Id, rule.Name, filePath, ExecutionStatus.Failed, $"{action.Type}: {ex.Message}");
                }
            }

            rule.LastRunAt = DateTime.Now;
            rule.RunCount++;

            var status = failed == 0 ? ExecutionStatus.Success
                                       : (success == 0 ? ExecutionStatus.Failed : ExecutionStatus.Skipped);
            AddLog(rule.Id, rule.Name, filePath, status,
                $"执行 {rule.Actions.Count} 个动作（成功 {success} / 失败 {failed}）");
        }
        catch (Exception ex)
        {
            AddLog(rule.Id, rule.Name, filePath, ExecutionStatus.Failed, ex.Message);
        }
    }

    // ============================================================
    //  条件评估（AND 关系）
    // ============================================================

    /// <summary>
    /// 评估文件是否满足全部条件（AND 关系；条件列表为空视为匹配所有）
    /// </summary>
    public bool EvaluateConditions(string filePath, IReadOnlyList<RuleCondition> conditions)
    {
        if (conditions == null || conditions.Count == 0) return true;

        foreach (var c in conditions)
        {
            if (!EvaluateCondition(filePath, c)) return false;
        }

        return true;
    }

    private static bool EvaluateCondition(string filePath, RuleCondition c)
    {
        try
        {
            switch (c.Field)
            {
                case ConditionField.FileName:
                    return CompareString(Path.GetFileName(filePath), c.Operator, c.Value);

                case ConditionField.Extension:
                    var ext = Path.GetExtension(filePath) ?? string.Empty;
                    // 同时尝试带点与不带点两种写法
                    return CompareString(ext, c.Operator, c.Value)
                           || CompareString(ext.TrimStart('.'), c.Operator, (c.Value ?? string.Empty).TrimStart('.'));

                case ConditionField.Size:
                    var fi = new FileInfo(filePath);
                    return fi.Exists && long.TryParse(c.Value, out var size) && CompareNumber(fi.Length, c.Operator, size);

                case ConditionField.ModifiedDate:
                    var fm = new FileInfo(filePath);
                    return fm.Exists && CompareDate(fm.LastWriteTime, c.Operator, c.Value);

                case ConditionField.CreationDate:
                    var fc = new FileInfo(filePath);
                    return fc.Exists && CompareDate(fc.CreationTime, c.Operator, c.Value);

                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool CompareString(string target, ConditionOperator op, string? value)
    {
        var v = value ?? string.Empty;
        return op switch
        {
            ConditionOperator.Contains => target.Contains(v, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.Equals => target.Equals(v, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.StartsWith => target.StartsWith(v, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.EndsWith => target.EndsWith(v, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.GreaterThan => string.Compare(target, v, StringComparison.OrdinalIgnoreCase) > 0,
            ConditionOperator.LessThan => string.Compare(target, v, StringComparison.OrdinalIgnoreCase) < 0,
            _ => false, // OlderThan 不适用于字符串
        };
    }

    private static bool CompareNumber(long actual, ConditionOperator op, string? value)
    {
        if (!long.TryParse(value, out var expected)) return false;
        return op switch
        {
            ConditionOperator.Equals => actual == expected,
            ConditionOperator.GreaterThan => actual > expected,
            ConditionOperator.LessThan => actual < expected,
            _ => false,
        };
    }

    private static bool CompareNumber(long actual, ConditionOperator op, long expected)
    {
        return op switch
        {
            ConditionOperator.Equals => actual == expected,
            ConditionOperator.GreaterThan => actual > expected,
            ConditionOperator.LessThan => actual < expected,
            _ => false,
        };
    }

    private static bool CompareDate(DateTime fileDate, ConditionOperator op, string? value)
    {
        var v = value ?? string.Empty;
        return op switch
        {
            ConditionOperator.OlderThan => double.TryParse(v, out var days)
                                              && (DateTime.Now - fileDate).TotalDays > days,
            ConditionOperator.Equals => fileDate.ToString("yyyy-MM-dd") == v,
            ConditionOperator.GreaterThan => DateTime.TryParse(v, out var after) && fileDate > after,
            ConditionOperator.LessThan => DateTime.TryParse(v, out var before) && fileDate < before,
            _ => false,
        };
    }

    // ============================================================
    //  动作执行
    // ============================================================

    /// <summary>
    /// 对单个文件执行单个动作；成功返回 true，被安全拦截或失败返回 false（失败已记录日志）
    /// </summary>
    public bool ExecuteAction(string filePath, RuleAction action, AutomationRule rule)
    {
        try
        {
            switch (action.Type)
            {
                case ActionType.Move:
                    EnsureDirectory(action.TargetPath);
                    File.Move(filePath, ResolveUniquePath(action.TargetPath, Path.GetFileName(filePath)));
                    return true;

                case ActionType.Copy:
                    EnsureDirectory(action.TargetPath);
                    File.Copy(filePath, ResolveUniquePath(action.TargetPath, Path.GetFileName(filePath)));
                    return true;

                case ActionType.Delete:
                    if (!rule.IsDestructive)
                    {
                        AddLog(rule.Id, rule.Name, filePath, ExecutionStatus.Skipped,
                            "Delete 动作需规则启用「允许永久删除」标记");
                        return false;
                    }

                    if (File.Exists(filePath)) File.Delete(filePath);
                    else if (Directory.Exists(filePath)) Directory.Delete(filePath, recursive: false);
                    return true;

                case ActionType.Recycle:
                    if (File.Exists(filePath))
                    {
                        FileSystem.DeleteFile(filePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    }
                    else if (Directory.Exists(filePath))
                    {
                        FileSystem.DeleteDirectory(filePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    }
                    else
                    {
                        return false;
                    }

                    return true;

                case ActionType.Rename:
                    var dir = Path.GetDirectoryName(filePath) ?? string.Empty;
                    var newName = ReplaceTemplate(action.NameTemplate, filePath);
                    if (string.IsNullOrEmpty(Path.GetExtension(newName))
                        && !string.IsNullOrEmpty(Path.GetExtension(filePath)))
                    {
                        newName += Path.GetExtension(filePath); // 模板未带扩展名则保留原扩展名
                    }

                    File.Move(filePath, ResolveUniquePath(dir, newName));
                    return true;

                case ActionType.OpenApp:
                    var psi = new ProcessStartInfo
                    {
                        FileName = action.TargetPath,
                        Arguments = $"\"{filePath}\"",
                        UseShellExecute = true,
                    };
                    Process.Start(psi);
                    return true;

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            AddLog(rule.Id, rule.Name, filePath, ExecutionStatus.Failed, $"{action.Type}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 名称模板变量替换：{日期} {时间} {原名} {扩展名}
    /// </summary>
    public static string ReplaceTemplate(string template, string filePath)
    {
        if (string.IsNullOrEmpty(template)) return Path.GetFileName(filePath);

        var now = DateTime.Now;
        var nameNoExt = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath).TrimStart('.');

        return template
            .Replace("{日期}", now.ToString("yyyy-MM-dd"))
            .Replace("{时间}", now.ToString("HHmmss"))
            .Replace("{原名}", nameNoExt)
            .Replace("{扩展名}", ext);
    }

    /// <summary>确保目标目录存在</summary>
    private static void EnsureDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(path);
    }

    /// <summary>在目标目录下生成不冲突的文件路径（同名自动追加 (2) (3)…）</summary>
    private static string ResolveUniquePath(string folder, string fileName)
    {
        var target = Path.Combine(folder, fileName);
        if (!File.Exists(target) && !Directory.Exists(target)) return target;

        var nameNoExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(folder, $"{nameNoExt} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        return target;
    }

    // ============================================================
    //  日志
    // ============================================================

    /// <summary>获取最近执行日志（最新在前，最多 500 条）</summary>
    public IReadOnlyList<RuleExecutionLog> GetLogs()
    {
        lock (_logLock)
        {
            return _logs.ToList();
        }
    }

    /// <summary>清空日志</summary>
    public void ClearLogs()
    {
        lock (_logLock) _logs.Clear();
        LogsChanged?.Invoke();
    }

    private void AddLog(Guid ruleId, string ruleName, string file, ExecutionStatus status, string message)
    {
        var log = new RuleExecutionLog
        {
            RuleId = ruleId,
            RuleName = ruleName,
            TargetFile = file,
            Status = status,
            Message = message,
            Time = DateTime.Now,
        };

        lock (_logLock)
        {
            _logs.Insert(0, log);
            if (_logs.Count > 500) _logs.RemoveRange(500, _logs.Count - 500);
        }

        LogsChanged?.Invoke();
    }

    // ============================================================
    //  持久化
    // ============================================================

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;

            var json = File.ReadAllText(FilePath);
            var rules = JsonSerializer.Deserialize<List<AutomationRule>>(json, JsonOptions);
            if (rules != null)
            {
                lock (_lock) _rules = rules;
                Console.WriteLine($"[Automation] 已加载 {_rules.Count} 条规则");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Automation] 加载失败: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            List<AutomationRule> snapshot;
            lock (_lock) snapshot = _rules.ToList();

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Automation] 保存失败: {ex.Message}");
        }
    }
}
