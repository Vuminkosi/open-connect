namespace OpenConnect.Persistence.EFCore.Entities;
internal sealed class TaskEntity
{
    public Guid TaskId { get; set; }
    public string TenantScope { get; set; } = null!;
    public string PrincipalScope { get; set; } = null!;
    public Guid SessionId { get; set; }
    public string ClientTaskId { get; set; } = null!;
    public string WorkloadScope { get; set; } = null!;
    public string AgentId { get; set; } = null!;
    public string State { get; set; } = null!;
    public long Version { get; set; }
    public string CanonicalHash { get; set; } = null!;
    public string? InputRef { get; set; }
    public string? ResultRef { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}
