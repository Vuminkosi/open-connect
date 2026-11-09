// EF Core translation of the Postgres Navigator lease upsert/fencing logic
// (TASK PERSIST-04-NAV-SEAM). No raw SQL: every statement below is LINQ + SaveChanges.
//
// Postgres source translated (read-only):
//   - TryAcquireLeaseAsync: single-statement INSERT ... ON CONFLICT (tenant_id, name)
//     DO UPDATE ... WHERE oc_leases.expires_at <= now; winner's version = prior + 1
//     (1 for a new row); losers affect 0 rows and get null.
//   - RenewLeaseAsync: WHERE tenant/name/version-match AND unexpired; version checked +1.
//   - IsLeaseCurrentAsync / IsFenceCurrentAsync: tenant/name/version-match AND expires > now.
//   - ReleaseLeaseAsync: version-matched row retained with ExpiresAt = MinValue.
//   - timestamptz microsecond truncation (floor) so expiry is never extended by rounding.
//
// EF Core mapping onto the EFCore lease model (LeaseEntity, no schema change):
//   - (TenantScope, ResourceKey) unique index  <->  (tenant_id, name) conflict target.
//   - FencingVersion (already an EF concurrency token)  <->  version / fencing token.
//   - LeaseEntity.Id (row PK, stable across renew/takeover/release)  <->  LeaseId.
//   - ExecutionId "<uuidD>:<local>" -> TenantScope = uuid "D" string, ResourceKey = executionId.
//   - DbUpdateException on insert (unique-index race loser) -> null (conflict), fail closed.
//   - DbUpdateConcurrencyException on takeover/renew/release -> null/no-op, fail closed.
//
// Deliberate divergences from Postgres, documented (not silent):
//   - Read-modify-write instead of a single atomic upsert statement. The unique index plus
//     the FencingVersion concurrency token make concurrent losers fail closed, but
//     single-statement atomicity is NOT proven here -> marked NOT VERIFIED in handoff.
//   - Owner/token over-length inputs return null (fail closed) instead of relying on a
//     database length violation, because LeaseEntity caps Owner at 256 and ResourceKey
//     at 256 while the Postgres name column is unbounded.

using Microsoft.EntityFrameworkCore;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Navigator;

/// <summary>
/// <see cref="INavigatorLeaseSeam"/> over any <see cref="DbContext"/> whose model applies
/// the shared <c>LeaseEntityConfiguration</c>. Single-SaveChanges operations only.
/// </summary>
public sealed class EfNavigatorLeaseSeam<TContext>(TContext context) : INavigatorLeaseSeam
    where TContext : DbContext
{
    private const int MaxExecutionIdLength = 256;
    private const int MaxOwnerLength = 256;

    public async ValueTask<NavigatorLease?> TryAcquireAsync(
        string executionId, string owner, TimeSpan duration, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (duration <= TimeSpan.Zero
            || string.IsNullOrWhiteSpace(owner)
            || owner.Length > MaxOwnerLength
            || executionId is null
            || executionId.Length > MaxExecutionIdLength
            || !TrySplitExecution(executionId, out var tenantScope, out _))
            return null;

        var set = context.Set<LeaseEntity>();
        var row = await set.SingleOrDefaultAsync(
            x => x.TenantScope == tenantScope && x.ResourceKey == executionId, cancellationToken).ConfigureAwait(false);

        var expiry = TruncateToMicroseconds(now + duration);
        if (row is null)
        {
            var id = Guid.NewGuid();
            set.Add(new LeaseEntity
            {
                Id = id,
                TenantScope = tenantScope,
                ResourceKey = executionId,
                Owner = owner,
                FencingVersion = 1,
                ExpiresUtc = expiry.UtcDateTime,
            });
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new NavigatorLease(id.ToString("D"), executionId, 1, expiry);
            }
            catch (DbUpdateException)
            {
                // Unique-index race loser: another process inserted first. Fail closed.
                context.ChangeTracker.Clear();
                return null;
            }
        }

        if (row.ExpiresUtc > now.UtcDateTime)
            return null; // Live lease held: conflict (Postgres affected-row-count == 0 path).

        row.Owner = owner;
        row.ExpiresUtc = expiry.UtcDateTime;
        row.FencingVersion = checked(row.FencingVersion + 1);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new NavigatorLease(row.Id.ToString("D"), executionId, row.FencingVersion, expiry);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Concurrent takeover/renew winner exists. Fail closed.
            context.Entry(row).State = EntityState.Detached;
            return null;
        }
    }

    public async ValueTask<NavigatorLease?> RenewAsync(
        NavigatorLease lease, TimeSpan duration, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();
        if (duration <= TimeSpan.Zero
            || !TrySplitExecution(lease.ExecutionId, out var tenantScope, out _))
            return null;

        var row = await context.Set<LeaseEntity>().SingleOrDefaultAsync(
            x => x.TenantScope == tenantScope
                && x.ResourceKey == lease.ExecutionId
                && x.FencingVersion == lease.FencingToken
                && x.ExpiresUtc > now.UtcDateTime,
            cancellationToken).ConfigureAwait(false);
        if (row is null)
            return null; // Unknown, stale-token, or expired: conflict (Postgres null path).

        var expiry = TruncateToMicroseconds(now + duration);
        row.ExpiresUtc = expiry.UtcDateTime;
        row.FencingVersion = checked(row.FencingVersion + 1);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return lease with { FencingToken = row.FencingVersion, ExpiresAt = expiry };
        }
        catch (DbUpdateConcurrencyException)
        {
            context.Entry(row).State = EntityState.Detached;
            return null;
        }
    }

    public async ValueTask<bool> IsCurrentAsync(
        NavigatorLease lease, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TrySplitExecution(lease.ExecutionId, out var tenantScope, out _))
            return false;
        return await context.Set<LeaseEntity>().AsNoTracking().AnyAsync(
            x => x.TenantScope == tenantScope
                && x.ResourceKey == lease.ExecutionId
                && x.FencingVersion == lease.FencingToken
                && x.ExpiresUtc > now.UtcDateTime,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ReleaseAsync(NavigatorLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TrySplitExecution(lease.ExecutionId, out var tenantScope, out _))
            return;

        var row = await context.Set<LeaseEntity>().SingleOrDefaultAsync(
            x => x.TenantScope == tenantScope
                && x.ResourceKey == lease.ExecutionId
                && x.FencingVersion == lease.FencingToken,
            cancellationToken).ConfigureAwait(false);
        if (row is null)
            return; // Unknown/stale token: idempotent no-op (Postgres early-return path).

        // Retain the row so the next acquire advances the fencing token. Minimum UTC instant
        // also expires the row for callers holding a captured/injected clock value.
        row.ExpiresUtc = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            context.Entry(row).State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Mirrors Postgres TryExecutionTenant: "&lt;uuid&gt;:&lt;local&gt;" with a non-empty local part.
    /// TenantScope is the canonical "D" UUID string (36 chars; fits the 128-char column).
    /// </summary>
    private static bool TrySplitExecution(string executionId, out string tenantScope, out string local)
    {
        tenantScope = string.Empty;
        local = string.Empty;
        if (string.IsNullOrEmpty(executionId))
            return false;
        var separator = executionId.IndexOf(':');
        if (separator <= 0 || separator >= executionId.Length - 1)
            return false;
        if (!Guid.TryParse(executionId.AsSpan(0, separator), out var tenant))
            return false;
        tenantScope = tenant.ToString("D");
        local = executionId[(separator + 1)..];
        return true;
    }

    /// <summary>
    /// Floor to whole microseconds, mirroring Postgres timestamptz storage so persisted
    /// expiry never exceeds the requested instant.
    /// </summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
}
