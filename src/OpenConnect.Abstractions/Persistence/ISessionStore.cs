namespace OpenConnect.Abstractions.Persistence;

/// <summary>Provider-independent storage contract for session state.</summary>
public interface ISessionStore
{
    /// <summary>Gets a session only when its tenant and principal match.</summary>
    Task<SessionRecord?> GetAsync(SessionId sessionId, TenantId tenantId, PrincipalId principalId, CancellationToken ct = default);

    /// <summary>Creates or updates a session record.</summary>
    Task<SessionRecord> UpsertAsync(SessionRecord record, CancellationToken ct = default);
}
