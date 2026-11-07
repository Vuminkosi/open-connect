namespace OpenConnect.Persistence.EFCore.Entities;
internal sealed class OutboxEntity
{
    public Guid Id { get; set; }
    public string TenantScope { get; set; } = null!;
    public string EventId { get; set; } = null!;
    public string Subject { get; set; } = null!;
    public string SanitizedPayload { get; set; } = null!;
    public string Status { get; set; } = "pending";
    public long FencingVersion { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? PublishedUtc { get; set; }
    public DateTime NextAttemptUtc { get; set; }
    public int Attempts { get; set; }
}
