using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Stores;

internal sealed class EfSessionStore<TContext>(TContext context) : ISessionStore where TContext : DbContext
{
    public async Task<SessionRecord?> GetAsync(SessionId sessionId, TenantId tenantId, PrincipalId principalId, CancellationToken ct = default)
    {
        var entity = await context.Set<SessionEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == UlidGuidMapper.ToGuid(sessionId.Value) && x.TenantScope == UlidGuidMapper.Scope(tenantId.Value) && x.PrincipalScope == UlidGuidMapper.Scope(principalId.Value), ct).ConfigureAwait(false);
        return entity is null ? null : new(new(UlidGuidMapper.FromGuid(entity.SessionId)), tenantId, principalId, entity.Version);
    }

    public async Task<SessionRecord> UpsertAsync(SessionRecord record, CancellationToken ct = default)
    {
        var id = UlidGuidMapper.ToGuid(record.Id.Value);
        var tenant = UlidGuidMapper.Scope(record.TenantId.Value);
        var principal = UlidGuidMapper.Scope(record.PrincipalId.Value);
        var entity = await context.Set<SessionEntity>().SingleOrDefaultAsync(x => x.SessionId == id && x.TenantScope == tenant && x.PrincipalScope == principal, ct).ConfigureAwait(false);
        // DateTime.UtcNow is the approved interim clock; no IClock dependency is introduced.
        var now = DateTime.UtcNow;
        if (entity is null) context.Add(new SessionEntity { SessionId = id, TenantScope = tenant, PrincipalScope = principal, Version = record.Version, CreatedUtc = now, UpdatedUtc = now });
        else { entity.Version = record.Version; entity.UpdatedUtc = now; }
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return record;
    }
}
