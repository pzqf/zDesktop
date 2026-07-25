using System.IO;
using System.Text.Json;
using zDesktop.Core.Todos;

namespace zDesktop.Shell.Todos;

/// <summary>
/// 待办持久化服务 — JSON 文件存储
///
/// 存储路径：%APPDATA%\zDesktop\todos.json
/// 线程安全：所有读写加锁
/// </summary>
public sealed class TodoStore
{
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop");

    private static readonly string FilePath = Path.Combine(AppDataDir, "todos.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _lock = new();
    private List<TodoItem> _items = new();

    /// <summary>数据变更通知 — UI 订阅后刷新</summary>
    public event Action? Changed;

    public TodoStore()
    {
        Load();
    }

    /// <summary>获取所有待办（按创建时间倒序）</summary>
    public IReadOnlyList<TodoItem> GetAll()
    {
        lock (_lock)
        {
            return _items.OrderByDescending(x => x.CreatedAt).ToList();
        }
    }

    /// <summary>添加待办</summary>
    public TodoItem Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("待办内容不能为空", nameof(text));

        var item = new TodoItem
        {
            Text = text.Trim(),
            CreatedAt = DateTime.Now,
        };

        lock (_lock)
        {
            _items.Add(item);
        }

        Save();
        Changed?.Invoke();
        return item;
    }

    /// <summary>切换完成状态</summary>
    public void Toggle(Guid id)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(x => x.Id == id);
            if (item == null) return;

            item.IsCompleted = !item.IsCompleted;
            item.CompletedAt = item.IsCompleted ? DateTime.Now : null;
        }

        Save();
        Changed?.Invoke();
    }

    /// <summary>删除待办</summary>
    public void Remove(Guid id)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(x => x.Id == id);
            if (item == null) return;

            _items.Remove(item);
        }

        Save();
        Changed?.Invoke();
    }

    /// <summary>清除所有已完成</summary>
    public void ClearCompleted()
    {
        lock (_lock)
        {
            _items.RemoveAll(x => x.IsCompleted);
        }

        Save();
        Changed?.Invoke();
    }

    // ===== 持久化 =====

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;

            var json = File.ReadAllText(FilePath);
            var items = JsonSerializer.Deserialize<List<TodoItem>>(json, JsonOptions);
            if (items != null)
            {
                _items = items;
                Console.WriteLine($"[TodoStore] 已加载 {_items.Count} 条待办");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TodoStore] 加载失败: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            lock (_lock)
            {
                var json = JsonSerializer.Serialize(_items, JsonOptions);
                File.WriteAllText(FilePath, json);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TodoStore] 保存失败: {ex.Message}");
        }
    }
}
