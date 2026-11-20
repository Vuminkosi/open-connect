namespace OpenConnect.Docs.Cloud;

// A cloud-side decision observation, not an application-host receipt or a delivery record.
public sealed class CloudPrivacyEvidence
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid RequestId { get; set; }
    public required string Outcome { get; set; }
    public required string TraceId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public interface ICloudPrivacyEvidenceStore
{
    Task AppendAsync(CloudCredentialIdentity identity, Guid requestId, string outcome, string traceId, CancellationToken cancellationToken);
}
