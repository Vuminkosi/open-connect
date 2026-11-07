namespace OpenConnect.Persistence.EFCore.Entities;
internal sealed class SessionEntity
{
    public Guid SessionId { get; set; }
    public string TenantScope { get; set; } = null!;
    public string PrincipalScope { get; set; } = null!;
    public long Version { get; set; }
    public string? ContextCheckpoint { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
}
