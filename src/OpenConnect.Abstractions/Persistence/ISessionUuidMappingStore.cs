namespace OpenConnect.Abstractions.Persistence;

/// <summary>Outcome of a durable session UUID mapping write.</summary>
public enum SessionUuidMappingSaveOutcome
{
    /// <summary>The mapping was newly persisted.</summary>
    Saved,

    /// <summary>The identical mapping already exists; no write was needed.</summary>
    AlreadyMapped,

    /// <summary>
    /// A different mapping already occupies the public UUID or the internal session id.
    /// Fail closed: callers must not use any stored value.
    /// </summary>
    Conflict,
}

/// <summary>
/// Durable public-UUID to runtime-ULID session mapping (contract SESSION-UUID-MAPPING, durable).
/// </summary>
/// <remarks>
/// <para>
/// The public id is a genuine UUID supplied at session creation time; implementations must never
/// derive a UUID from a ULID. Mappings are immutable: only an absent key may be created.
/// </para>
/// <para>
/// Tenant isolation is enforced server-side on every operation. Reads additionally verify the
/// principal and return <see langword="null"/> on any mismatch so unmapped or foreign UUIDs are
/// indistinguishable from absent (fail-closed, resolve as session-not-found). Callers keep their
/// post-resolve dual ownership checks.
/// </para>
/// </remarks>
public interface ISessionUuidMappingStore
{
    /// <summary>Resolves a public UUID to its internal session id only when tenant and principal match.</summary>
    Task<SessionId?> ResolveAsync(Guid publicId, TenantId tenantId, PrincipalId principalId, CancellationToken ct = default);

    /// <summary>Atomically creates a public-UUID to session mapping.</summary>
    /// <returns><see cref="SessionUuidMappingSaveOutcome.Saved"/> when persisted, otherwise the exact non-write outcome.</returns>
    Task<SessionUuidMappingSaveOutcome> TryAddAsync(Guid publicId, SessionId sessionId, TenantId tenantId, PrincipalId principalId, CancellationToken ct = default);
}
