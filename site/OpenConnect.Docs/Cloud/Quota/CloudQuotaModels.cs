namespace OpenConnect.Docs.Cloud;

public sealed class CloudQuotaBucket
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public required string CapabilityId { get; set; }
    public required string Unit { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public long Limit { get; set; }
    public long Consumed { get; set; }
    public long Reserved { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class CloudQuotaReservation
{
    public Guid Id { get; set; }
    public Guid BucketId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid RequestId { get; set; }
    public byte[]? RequestFingerprint { get; set; }
    public string? FingerprintKeyId { get; set; }
    public long Amount { get; set; }
    public long? Actual { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
}
