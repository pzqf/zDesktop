namespace zDesktop.Core.Todos;

/// <summary>
/// 待办项数据模型 — 可 JSON 序列化
/// </summary>
public class TodoItem
{
    /// <summary>唯一标识</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>待办内容</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>是否已完成</summary>
    public bool IsCompleted { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>完成时间（null 表示未完成）</summary>
    public DateTime? CompletedAt { get; set; }
}
