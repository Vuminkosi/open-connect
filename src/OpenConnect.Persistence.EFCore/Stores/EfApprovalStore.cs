using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Stores;

internal sealed class EfApprovalStore<TContext>(TContext context) : IApprovalStore where TContext : DbContext
{
    public async Task<ApprovalRecord> CreateAsync(ApprovalRecord record, CancellationToken ct = default)
    {
        // EF-02F master-approved interim mapping.
        var now = DateTime.UtcNow;
        context.Set<ApprovalEntity>().Add(new ApprovalEntity
        {
            ApprovalId = record.Id,
            TenantScope = UlidGuidMapper.Scope(record.TenantId.Value),
            PrincipalScope = UlidGuidMapper.Scope(record.PrincipalId.Value),
            TaskId = UlidGuidMapper.ToGuid(record.TaskId.Value),
            State = record.State,
            Decision = record.Decision,
            BoundArgumentsHash = null,
            CreatedUtc = now,
            ExpiresUtc = now.AddDays(30),
            DecidedUtc = null
        });
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return record;
    }

    public async Task<ApprovalRecord?> GetAsync(Guid id, TenantId tenantId, PrincipalId principalId, CancellationToken ct = default)
    {
        var tenantScope = UlidGuidMapper.Scope(tenantId.Value);
        var principalScope = UlidGuidMapper.Scope(principalId.Value);
        var row = await context.Set<ApprovalEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.ApprovalId == id && x.TenantScope == tenantScope && x.PrincipalScope == principalScope, ct).ConfigureAwait(false);
        return row is null ? null : new ApprovalRecord(row.ApprovalId, tenantId, principalId, new TaskId(UlidGuidMapper.FromGuid(row.TaskId)), row.State, row.Decision);
    }

    public async Task DecideAsync(Guid id, TenantId tenantId, PrincipalId principalId, string decision, CancellationToken ct = default)
    {
        var tenantScope = UlidGuidMapper.Scope(tenantId.Value);
        var principalScope = UlidGuidMapper.Scope(principalId.Value);
        var row = await context.Set<ApprovalEntity>().SingleOrDefaultAsync(x => x.ApprovalId == id && x.TenantScope == tenantScope && x.PrincipalScope == principalScope, ct).ConfigureAwait(false);
        if (row is null) throw new KeyNotFoundException("approval_not_found");
        row.Decision = decision;
        row.State = "decided";
        row.DecidedUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
