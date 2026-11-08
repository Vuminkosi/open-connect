using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Stores;

/// <summary>Outcome of an approval nonce consume attempt (WF-04 durable).</summary>
internal enum ApprovalNonceConsumeOutcome
{
    /// <summary>The nonce was newly bound to the approval.</summary>
    Consumed,

    /// <summary>The identical tenant/nonce/approval/principal binding already exists.</summary>
    AlreadyConsumed,

    /// <summary>
    /// The tenant/nonce key is already bound to a different approval or principal.
    /// Fail closed: the caller must reject the replay.
    /// </summary>
    Conflict,
}

/// <summary>Audit view of a stored approval nonce binding.</summary>
/// <param name="ApprovalId">The approval the nonce is bound to.</param>
/// <param name="CreatedAtUtc">When the binding was first persisted.</param>
internal sealed record ApprovalNonceReceipt(Guid ApprovalId, DateTime CreatedAtUtc);

/// <summary>Audit view of a stored approval nonce string binding.</summary>
/// <param name="RequestId">The native request id the nonce is bound to, stored verbatim.</param>
/// <param name="CreatedAtUtc">When the binding was first persisted.</param>
internal sealed record ApprovalNonceRequestReceipt(string RequestId, DateTime CreatedAtUtc);

/// <summary>
/// Durable single-use approval nonce store (WF-04 durable).
/// </summary>
/// <remarks>
/// <para>
/// Nonces are opaque caller-supplied values stored verbatim; this store never derives
/// a nonce. Bindings are immutable and insert-only: only an absent
/// <c>(TenantId, Nonce)</c> key may be created, never updated or deleted.
/// </para>
/// <para>
/// Tenant isolation is enforced server-side on every operation. Reads additionally
/// verify the principal and return <see langword="null"/> on any mismatch or corrupt
/// row so foreign or malformed bindings are indistinguishable from absent
/// (fail-closed). Callers keep their ownership checks.
/// </para>
/// </remarks>
internal sealed class EfApprovalNonceStore<TContext>(TContext context) where TContext : DbContext
{
    public async Task<ApprovalNonceReceipt?> GetAsync(
        TenantId tenantId,
        PrincipalId principalId,
        string nonce,
        CancellationToken ct = default)
    {
        ValidateKey(tenantId, principalId, nonce);

        var tenant = tenantId.Value.ToString();
        var row = await context.Set<ApprovalNonceEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenant && x.Nonce == nonce, ct)
            .ConfigureAwait(false);
        if (row is null) return null;
        if (!string.Equals(row.PrincipalId, principalId.Value.ToString(), StringComparison.Ordinal)) return null;
        if (row.ApprovalId == Guid.Empty) return null;
        if (string.IsNullOrEmpty(row.Nonce)) return null;
        return new ApprovalNonceReceipt(row.ApprovalId, row.CreatedAtUtc);
    }

    public async Task<ApprovalNonceConsumeOutcome> TryConsumeAsync(
        TenantId tenantId,
        PrincipalId principalId,
        string nonce,
        Guid approvalId,
        CancellationToken ct = default)
    {
        ValidateKey(tenantId, principalId, nonce);
        if (approvalId == Guid.Empty) throw new ArgumentException("Approval id must be non-empty.", nameof(approvalId));

        var tenant = tenantId.Value.ToString();
        var principal = principalId.Value.ToString();
        var set = context.Set<ApprovalNonceEntity>();

        var current = await set.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenant && x.Nonce == nonce, ct)
            .ConfigureAwait(false);
        if (current is not null)
            return Matches(current, tenant, principal, approvalId)
                ? ApprovalNonceConsumeOutcome.AlreadyConsumed
                : ApprovalNonceConsumeOutcome.Conflict;

        // DateTime.UtcNow is the approved interim clock; no IClock dependency is introduced.
        var entity = new ApprovalNonceEntity
        {
            TenantId = tenant,
            Nonce = nonce,
            ApprovalId = approvalId,
            PrincipalId = principal,
            CreatedAtUtc = DateTime.UtcNow,
        };
        set.Add(entity);
        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return ApprovalNonceConsumeOutcome.Consumed;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            context.Entry(entity).State = EntityState.Detached;
        }

        // Lost an insert race: re-read under the tenant key and compare exactly.
        var winner = await set.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenant && x.Nonce == nonce, ct)
            .ConfigureAwait(false);
        if (winner is not null)
            return Matches(winner, tenant, principal, approvalId)
                ? ApprovalNonceConsumeOutcome.AlreadyConsumed
                : ApprovalNonceConsumeOutcome.Conflict;

        // No row under our key won; the key space is contested. Fail closed.
        return ApprovalNonceConsumeOutcome.Conflict;
    }

    /// <summary>
    /// Gets the string request-id binding for audit only when tenant and principal match.
    /// </summary>
    /// <remarks>
    /// The request id is stored and compared verbatim (<see cref="StringComparison.Ordinal"/>);
    /// it is never coerced to a <see cref="Guid"/>, trimmed, or case-folded. Rows whose stored
    /// <c>RequestId</c> is empty (pre-string-binding legacy rows) resolve as absent (fail-closed).
    /// </remarks>
    public async Task<ApprovalNonceRequestReceipt?> GetWithRequestIdAsync(
        TenantId tenantId,
        PrincipalId principalId,
        string nonce,
        CancellationToken ct = default)
    {
        ValidateKey(tenantId, principalId, nonce);

        var tenant = tenantId.Value.ToString();
        var row = await context.Set<ApprovalNonceEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenant && x.Nonce == nonce, ct)
            .ConfigureAwait(false);
        if (row is null) return null;
        if (!string.Equals(row.PrincipalId, principalId.Value.ToString(), StringComparison.Ordinal)) return null;
        if (string.IsNullOrEmpty(row.RequestId)) return null;
        if (string.IsNullOrEmpty(row.Nonce)) return null;
        return new ApprovalNonceRequestReceipt(row.RequestId, row.CreatedAtUtc);
    }

    /// <summary>
    /// Atomically binds a nonce to a native string request id for single-use replay protection.
    /// </summary>
    /// <remarks>
    /// The request id is stored verbatim; it is never coerced to a <see cref="Guid"/>, derived,
    /// hashed, or transformed. Bindings are immutable and insert-only. A legacy row bound to a
    /// <see cref="Guid"/> approval (empty <c>RequestId</c>) conflicts with any string consume
    /// under the same key (fail-closed).
    /// </remarks>
    public async Task<ApprovalNonceConsumeOutcome> TryConsumeWithRequestIdAsync(
        TenantId tenantId,
        PrincipalId principalId,
        string nonce,
        string requestId,
        CancellationToken ct = default)
    {
        ValidateKey(tenantId, principalId, nonce);
        ValidateRequestId(requestId);

        var tenant = tenantId.Value.ToString();
        var principal = principalId.Value.ToString();
        var set = context.Set<ApprovalNonceEntity>();

        var current = await set.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenant && x.Nonce == nonce, ct)
            .ConfigureAwait(false);
        if (current is not null)
            return MatchesRequest(current, tenant, principal, requestId)
                ? ApprovalNonceConsumeOutcome.AlreadyConsumed
                : ApprovalNonceConsumeOutcome.Conflict;

        // DateTime.UtcNow is the approved interim clock; no IClock dependency is introduced.
        var entity = new ApprovalNonceEntity
        {
            TenantId = tenant,
            Nonce = nonce,
            RequestId = requestId,
            PrincipalId = principal,
            CreatedAtUtc = DateTime.UtcNow,
        };
        set.Add(entity);
        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return ApprovalNonceConsumeOutcome.Consumed;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            context.Entry(entity).State = EntityState.Detached;
        }

        // Lost an insert race: re-read under the tenant key and compare exactly.
        var winner = await set.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenant && x.Nonce == nonce, ct)
            .ConfigureAwait(false);
        if (winner is not null)
            return MatchesRequest(winner, tenant, principal, requestId)
                ? ApprovalNonceConsumeOutcome.AlreadyConsumed
                : ApprovalNonceConsumeOutcome.Conflict;

        // No row under our key won; the key space is contested. Fail closed.
        return ApprovalNonceConsumeOutcome.Conflict;
    }

    private static void ValidateRequestId(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("Request id must be non-empty.", nameof(requestId));
        if (requestId.Length > 256) throw new ArgumentException("Request id must be at most 256 characters.", nameof(requestId));
    }

    private static bool MatchesRequest(ApprovalNonceEntity row, string tenant, string principal, string requestId) =>
        string.Equals(row.TenantId, tenant, StringComparison.Ordinal) &&
        string.Equals(row.PrincipalId, principal, StringComparison.Ordinal) &&
        string.Equals(row.RequestId, requestId, StringComparison.Ordinal) &&
        !string.IsNullOrEmpty(row.RequestId) &&
        !string.IsNullOrEmpty(row.Nonce);

    private static void ValidateKey(TenantId tenantId, PrincipalId principalId, string nonce)
    {
        if (tenantId.Value == default) throw new ArgumentException("Tenant id must be non-default.", nameof(tenantId));
        if (principalId.Value == default) throw new ArgumentException("Principal id must be non-default.", nameof(principalId));
        if (string.IsNullOrWhiteSpace(nonce)) throw new ArgumentException("Nonce must be non-empty.", nameof(nonce));
        if (nonce.Length > 64) throw new ArgumentException("Nonce must be at most 64 characters.", nameof(nonce));
    }

    private static bool Matches(ApprovalNonceEntity row, string tenant, string principal, Guid approvalId) =>
        string.Equals(row.TenantId, tenant, StringComparison.Ordinal) &&
        string.Equals(row.PrincipalId, principal, StringComparison.Ordinal) &&
        row.ApprovalId == approvalId &&
        !string.IsNullOrEmpty(row.Nonce);

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
