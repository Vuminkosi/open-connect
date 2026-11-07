using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Stores;

internal sealed class EfLeaseStore<TContext>(TContext context) : ILeaseStore where TContext : DbContext
{
    public async Task<LeaseRecord?> GetAsync(string resourceKey, TenantId tenantId, CancellationToken ct = default)
    {
        var scope = UlidGuidMapper.Scope(tenantId.Value);
        var row = await context.Set<LeaseEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.ResourceKey == resourceKey && x.TenantScope == scope, ct).ConfigureAwait(false);
        return row is null ? null : new(row.ResourceKey, tenantId, row.Owner, new DateTimeOffset(DateTime.SpecifyKind(row.ExpiresUtc, DateTimeKind.Utc)), row.FencingVersion);
    }

    public async Task<bool> TryAcquireAsync(LeaseRecord lease, CancellationToken ct = default)
    {
        var scope = UlidGuidMapper.Scope(lease.TenantId.Value);
        var set = context.Set<LeaseEntity>();
        var row = await set.SingleOrDefaultAsync(x => x.ResourceKey == lease.ResourceKey && x.TenantScope == scope, ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        if (row is null) set.Add(new LeaseEntity { Id = Guid.NewGuid(), TenantScope = scope, ResourceKey = lease.ResourceKey, Owner = lease.Owner, FencingVersion = lease.FencingVersion, ExpiresUtc = lease.ExpiresAt.UtcDateTime });
        else if (row.ExpiresUtc > now) return false;
        else { row.Owner = lease.Owner; row.FencingVersion = Math.Max(row.FencingVersion + 1, lease.FencingVersion); row.ExpiresUtc = lease.ExpiresAt.UtcDateTime; }
        try { await context.SaveChangesAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateException) { context.Entry(row!).State = EntityState.Detached; return false; }
    }

    public async Task<bool> RenewAsync(string resourceKey, TenantId tenantId, string owner, long fencingVersion, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        var scope = UlidGuidMapper.Scope(tenantId.Value); var now = DateTime.UtcNow;
        var row = await context.Set<LeaseEntity>().SingleOrDefaultAsync(x => x.ResourceKey == resourceKey && x.TenantScope == scope && x.Owner == owner && x.FencingVersion == fencingVersion && x.ExpiresUtc > now, ct).ConfigureAwait(false);
        if (row is null) return false;
        row.ExpiresUtc = expiresAt.UtcDateTime;
        try { await context.SaveChangesAsync(ct).ConfigureAwait(false); return true; } catch (DbUpdateConcurrencyException) { return false; }
    }

    public async Task ReleaseAsync(string resourceKey, TenantId tenantId, string owner, CancellationToken ct = default)
    {
        var scope = UlidGuidMapper.Scope(tenantId.Value);
        var row = await context.Set<LeaseEntity>().SingleOrDefaultAsync(x => x.ResourceKey == resourceKey && x.TenantScope == scope && x.Owner == owner, ct).ConfigureAwait(false);
        if (row is null) return;
        context.Remove(row); await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
