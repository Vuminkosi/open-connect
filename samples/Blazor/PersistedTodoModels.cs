using OpenConnect.Samples.Blazor.Models;

namespace OpenConnect.Samples.Blazor;

internal sealed class PersistedTodoScope
{
    public string Scope { get; set; } = string.Empty;
    public List<PersistedTodoItem> Items { get; set; } = [];
}

internal sealed class PersistedTodoItem
{
    public string Scope { get; set; } = string.Empty;
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsCompleted { get; set; }
    public TodoPriority Priority { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? DueDateUtc { get; set; }
    public PersistedTodoScope ScopeRecord { get; set; } = null!;
}
