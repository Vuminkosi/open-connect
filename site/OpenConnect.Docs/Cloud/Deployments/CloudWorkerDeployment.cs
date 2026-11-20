namespace OpenConnect.Docs.Cloud;

// Operator-controlled inventory. Presence in this table does not authorize execution.
public sealed class CloudWorkerDeployment
{
    public Guid Id { get; set; }
    public required string CapabilityId { get; set; }
    public required string EndpointReference { get; set; }
    public required string Transport { get; set; }
    public required string Region { get; set; }
    public required string ContractVersion { get; set; }
    public string? ModelId { get; set; }
    public string? ModelRevision { get; set; }
    public string? ModelDigest { get; set; }
    public required string ImageDigest { get; set; }
    public int ConcurrencyLimit { get; set; }
    public bool Enabled { get; set; }
    public long Revision { get; set; } = 1;
}
