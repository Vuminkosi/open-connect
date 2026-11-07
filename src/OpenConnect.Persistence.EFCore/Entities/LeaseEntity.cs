namespace OpenConnect.Persistence.EFCore.Entities;
internal sealed class LeaseEntity
{
    public Guid Id { get; set; }
    public string TenantScope { get; set; } = null!;
    public string ResourceKey { get; set; } = null!;
    public string Owner { get; set; } = null!;
    public long FencingVersion { get; set; }
    public DateTime ExpiresUtc { get; set; }
}
