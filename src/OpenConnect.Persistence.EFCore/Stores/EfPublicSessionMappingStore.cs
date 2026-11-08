using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Stores;

/// <summary>Outcome of a session UUID mapping write.</summary>
internal enum PublicSessionMappingSaveOutcome
{
    /// <summary>The mapping was newly persisted.</summary>
    Saved,

    /// <summary>The identical mapping already exists; no write was needed.</summary>
    AlreadyMapped,

    /// <summary>
    /// A different mapping already occupies the key or the internal session id.
    /// Fail closed: callers must not use any stored value.
    /// </summary>
    Conflict,
}

/// <summary>
/// Durable public-UUID to runtime-ULID session mapping store (contract SESSION-UUID-MAPPING).
/// </summary>
/// <remarks>
/// <para>
/// The public id is a genuine UUID supplied at session creation time; this store never
/// derives a UUID from a ULID. Mappings are immutable: only an absent key may be created.
/// </para>
/// <para>
/// Tenant isolation is enforced server-side on every operation. Reads additionally verify
/// the principal and return <see langword="null"/> on any mismatch so unmapped or foreign
/// UUIDs are indistinguishable from absent (resolve as session-not-found). Callers keep
/// their post-resolve dual ownership check.
/// </para>
/// </remarks>
internal sealed class EfPublicSessionMappingStore<TContext>(TContext context) where TContext : DbContext
{
    public async Task<SessionId?> GetInternalSessionIdAsync(
        Guid publicId,
        TenantId tenantId,
        PrincipalId principalId,
        CancellationToken ct = default)
    {
        if (publicId == Guid.Empty) throw new ArgumentException("Public session id must be non-empty.", nameof(publicId));
        if (tenantId.Value == default) throw new ArgumentException("Tenant id must be non-default.", nameof(tenantId));
        if (principalId.Value == default) throw new ArgumentException("Principal id must be non-default.", nameof(principalId));

        var tenant = tenantId.Value.ToString();
        var row = await context.Set<PublicSessionMappingEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenant && x.PublicId == publicId, ct)
            .ConfigureAwait(false);
        if (row is null) return null;
        if (!string.Equals(row.PrincipalId, principalId.Value.ToString(), StringComparison.Ordinal)) return null;
        Ulid ulid;
        try
        {
            ulid = Ulid.Parse(row.InternalSessionId);
        }
        catch (FormatException)
        {
            return null;
        }
        return new SessionId(ulid);
    }

    public async Task<PublicSessionMappingSaveOutcome> TryAddAsync(
        Guid publicId,
        SessionId sessionId,
        TenantId tenantId,
        PrincipalId principalId,
        CancellationToken ct = default)
    {
        if (publicId == Guid.Empty) throw new ArgumentException("Public session id must be non-empty.", nameof(publicId));
        if (sessionId.Value == default) throw new ArgumentException("Session id must be non-default.", nameof(sessionId));
        if (tenantId.Value == default) throw new ArgumentException("Tenant id must be non-default.", nameof(tenantId));
        if (principalId.Value == default) throw new ArgumentException("Principal id must be non-default.", nameof(principalId));

        var tenant = tenantId.Value.ToString();
        var internalId = sessionId.Value.ToString();
        var principal = principalId.Value.ToString();
        var set = context.Set<PublicSessionMappingEntity>();

        var current = await set.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenant && x.PublicId == publicId, ct)
            .ConfigureAwait(false);
        if (current is not null)
            return Matches(current, internalId, tenant, principal)
                ? PublicSessionMappingSaveOutcome.AlreadyMapped
                : PublicSessionMappingSaveOutcome.Conflict;

        // DateTime.UtcNow is the approved interim clock; no IClock dependency is introduced.
        var entity = new PublicSessionMappingEntity
        {
            PublicId = publicId,
            InternalSessionId = internalId,
            TenantId = tenant,
            PrincipalId = principal,
            CreatedAtUtc = DateTime.UtcNow,
            Version = 1,
        };
        set.Add(entity);
        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return PublicSessionMappingSaveOutcome.Saved;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            context.Entry(entity).State = EntityState.Detached;
        }

        // Lost an insert race: re-read under the tenant key and compare exactly.
        var winner = await set.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenant && x.PublicId == publicId, ct)
            .ConfigureAwait(false);
        if (winner is not null)
            return Matches(winner, internalId, tenant, principal)
                ? PublicSessionMappingSaveOutcome.AlreadyMapped
                : PublicSessionMappingSaveOutcome.Conflict;

        // The conflicting row owns the internal session id (unique index) but not our key.
        return PublicSessionMappingSaveOutcome.Conflict;
    }

    private static bool Matches(PublicSessionMappingEntity row, string internalId, string tenant, string principal) =>
        string.Equals(row.InternalSessionId, internalId, StringComparison.Ordinal) &&
        string.Equals(row.TenantId, tenant, StringComparison.Ordinal) &&
        string.Equals(row.PrincipalId, principal, StringComparison.Ordinal);

    // Keep this narrow: only a provider-reported unique/primary-key violation is a create race.
    private static bool IsUniqueConstraintViolation(Exception exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            var type = current.GetType();
            var code = type.GetProperty("SqliteExtendedErrorCode")?.GetValue(current)?.ToString();
            if (type.FullName == "Microsoft.Data.Sqlite.SqliteException" && (code == "1555" || code == "2067")) return true;
            code = type.GetProperty("SqlState")?.GetValue(current)?.ToString();
            if (type.FullName == "Npgsql.PostgresException" && code == "23505") return true;
            code = type.GetProperty("Number")?.GetValue(current)?.ToString();
            if (type.FullName == "Microsoft.Data.SqlClient.SqlException" && (code == "2601" || code == "2627")) return true;
        }
        return false;
    }
}
