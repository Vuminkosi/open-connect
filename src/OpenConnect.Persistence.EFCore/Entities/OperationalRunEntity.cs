namespace OpenConnect.Persistence.EFCore.Entities;
internal sealed class OperationalRunEntity
{
    public Guid Id { get; set; }
    public string TenantScope { get; set; } = null!;
    public string WorkloadScope { get; set; } = null!;
    public string RunId { get; set; } = null!;
    public string Status { get; set; } = null!;
    public long FencingVersion { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
}
