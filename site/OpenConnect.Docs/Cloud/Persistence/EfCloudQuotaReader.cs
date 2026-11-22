using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudQuotaReader(IDbContextFactory<CloudControlPlaneDbContext> contextFactory) : ICloudQuotaReader
{
    public async Task<CloudQuotaPage?> GetTenantAsync(CloudSubject subject, Guid tenantId, int offset, int limit, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var membership = db.Memberships.AsNoTracking().Where(x => x.TenantId == tenantId &&
            x.SubjectIssuer == subject.Issuer && x.SubjectId == subject.SubjectId &&
            (x.Role == "Owner" || x.Role == "Administrator" || x.Role == "BillingReader"));
        if (!await membership.AnyAsync(cancellationToken))
            return null;

        // Repeat the authorization predicate in the data query so a revoked membership cannot expose rows.
        var rows = await db.QuotaBuckets.AsNoTracking().Where(x => x.TenantId == tenantId && membership.Any())
            .OrderBy(x => x.Id).Skip(offset).Take(limit + 1)
            .Select(x => new CloudQuotaView(x.Id, x.ApplicationId, x.CapabilityId, x.Unit,
                x.PeriodStart, x.PeriodEnd, x.Limit, x.Consumed, x.Reserved))
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);
        return new CloudQuotaPage(rows, offset, hasMore ? offset + limit : null);
    }
}
