namespace OpenConnect.Docs.Cloud;

public sealed class CloudCredential
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public required string Environment { get; set; }
    public required byte[] KeyHash { get; set; }
    public required string Fingerprint { get; set; }
    public required string Scopes { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
