namespace OpenConnect.Docs.Cloud;

public sealed record CloudCredentialApplication(Guid Id, string Environment);

public interface ICloudCredentialStore
{
    Task<ICloudCredentialSession> OpenAsync(bool transactional, CancellationToken cancellationToken);
}

public interface ICloudCredentialSession : IAsyncDisposable
{
    Task<string?> FindRoleAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken);
    Task<CloudCredentialApplication?> FindApplicationAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken);
    Task<CloudCredential?> FindAsync(Guid credentialId, Guid? tenantId, Guid? applicationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CloudCredential>> ListAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken);
    Task<CloudCapabilitySnapshot?> GetCapabilitiesAsync(CloudSubject subject, Guid tenantId, Guid applicationId, CancellationToken cancellationToken);
    Task<bool> TouchIfActiveAsync(Guid credentialId, DateTimeOffset now, CancellationToken cancellationToken);
    void Add(CloudCredential credential);
    void AddAudit(CloudSubject subject, Guid tenantId, string role, string action, Guid credentialId, DateTimeOffset now);
    Task CommitAsync(CancellationToken cancellationToken);
}
