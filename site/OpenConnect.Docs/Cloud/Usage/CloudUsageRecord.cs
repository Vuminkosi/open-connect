namespace OpenConnect.Docs.Cloud;

public sealed class CloudUsageRecord
{
    public Guid ReservationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid RequestId { get; set; }
    public long Quantity { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public required string Provenance { get; set; }
    public string? CapabilityId { get; set; }
    public string? Unit { get; set; }
}
