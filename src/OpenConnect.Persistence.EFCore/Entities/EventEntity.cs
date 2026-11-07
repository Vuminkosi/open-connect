namespace OpenConnect.Persistence.EFCore.Entities;
internal sealed class EventEntity
{
    public Guid EventId { get; set; }
    public string TenantScope { get; set; } = null!;
    public Guid SessionId { get; set; }
    public long Sequence { get; set; }
    public string EventType { get; set; } = null!;
    public string SanitizedPayload { get; set; } = null!;
    public DateTime OccurredUtc { get; set; }
    public string? CorrelationId { get; set; }
}
