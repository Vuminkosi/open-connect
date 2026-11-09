namespace OpenConnect.Persistence.EFCore.Entities;
internal sealed class ApprovalEntity
{
    public Guid ApprovalId { get; set; }
    public string TenantScope { get; set; } = null!;
    public string PrincipalScope { get; set; } = null!;
    public Guid TaskId { get; set; }
    public string? CommandId { get; set; }
    public string? CommandDigest { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    public string State { get; set; } = "pending";
    public string? Decision { get; set; }
    public string? BoundArgumentsHash { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? DecidedUtc { get; set; }
}
