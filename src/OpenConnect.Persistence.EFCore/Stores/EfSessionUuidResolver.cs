using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;

namespace OpenConnect.Persistence.EFCore.Stores;

/// <summary>
/// Public durable session UUID mapping store backed by the tenant-partitioned
/// <c>PublicSessionMappings</c> model. Thin adapter over the internal store; no mapping logic of its own.
/// </summary>
/// <remarks>
/// The public id is a genuine UUID supplied at session creation time; this adapter never derives a
/// UUID from a ULID. Mappings are immutable and insert-only. Tenant isolation is enforced server-side
/// on every operation; principal mismatches resolve as absent (fail-closed).
/// </remarks>
public sealed class EfSessionUuidMappingStore<TContext>(TContext context) : ISessionUuidMappingStore where TContext : DbContext
{
    private readonly EfPublicSessionMappingStore<TContext> _inner = new(context);

    /// <inheritdoc />
    public Task<SessionId?> ResolveAsync(Guid publicId, TenantId tenantId, PrincipalId principalId, CancellationToken ct = default) =>
        _inner.GetInternalSessionIdAsync(publicId, tenantId, principalId, ct);

    /// <inheritdoc />
    public async Task<SessionUuidMappingSaveOutcome> TryAddAsync(Guid publicId, SessionId sessionId, TenantId tenantId, PrincipalId principalId, CancellationToken ct = default) =>
        Map(await _inner.TryAddAsync(publicId, sessionId, tenantId, principalId, ct).ConfigureAwait(false));

    private static SessionUuidMappingSaveOutcome Map(PublicSessionMappingSaveOutcome outcome) => outcome switch
    {
        PublicSessionMappingSaveOutcome.Saved => SessionUuidMappingSaveOutcome.Saved,
        PublicSessionMappingSaveOutcome.AlreadyMapped => SessionUuidMappingSaveOutcome.AlreadyMapped,
        _ => SessionUuidMappingSaveOutcome.Conflict,
    };
}

/// <summary>
/// Public durable session UUID resolver (bridge read side) backed by the tenant-partitioned
/// <c>PublicSessionMappings</c> model. Thin adapter; no mapping logic of its own.
/// </summary>
public sealed class EfSessionUuidResolver<TContext>(TContext context) : IDurableSessionUuidResolver where TContext : DbContext
{
    private readonly EfSessionUuidMappingStore<TContext> _store = new(context);

    /// <inheritdoc />
    public Task<SessionId?> ResolveAsync(Guid publicId, TenantId tenantId, PrincipalId principalId, CancellationToken ct = default) =>
        _store.ResolveAsync(publicId, tenantId, principalId, ct);
}
