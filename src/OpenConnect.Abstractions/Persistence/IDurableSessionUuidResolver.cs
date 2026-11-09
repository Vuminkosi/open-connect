namespace OpenConnect.Abstractions.Persistence;

/// <summary>Durable resolver for public session UUIDs (bridge read side).</summary>
/// <remarks>
/// Read-only projection over the durable session UUID mapping. Tenant isolation is enforced
/// server-side; principal mismatches and absent mappings resolve as <see langword="null"/>
/// (fail-closed, session-not-found). Callers keep their post-resolve dual ownership checks.
/// </remarks>
public interface IDurableSessionUuidResolver
{
    /// <summary>Resolves a public UUID to its internal session id only when tenant and principal match.</summary>
    Task<SessionId?> ResolveAsync(Guid publicId, TenantId tenantId, PrincipalId principalId, CancellationToken ct = default);
}
