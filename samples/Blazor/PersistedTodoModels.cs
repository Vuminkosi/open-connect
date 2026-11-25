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
    public DateTime? CompletedAtUtc { get; set; }
    public long Revision { get; set; }
    public string? TenantId { get; set; }
    public string? AssignedPrincipalId { get; set; }
    public TodoStatus Status { get; set; } = TodoStatus.Open;
    public string? EscalationPolicy { get; set; }
    public string? CommentsJson { get; set; }
    public string? ApprovalsJson { get; set; }
    public string? AuditHistoryJson { get; set; }
    public PersistedTodoScope ScopeRecord { get; set; } = null!;
}
