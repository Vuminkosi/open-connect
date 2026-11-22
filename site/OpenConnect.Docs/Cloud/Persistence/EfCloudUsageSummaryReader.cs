using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudUsageSummaryReader(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudUsageSummaryReader
{
    public async Task<CloudUsageSummary?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var actor = db.Memberships.AsNoTracking().Where(x => x.TenantId == tenantId && x.SubjectIssuer == subject.Issuer && x.SubjectId == subject.SubjectId);
        var role = await actor.Select(x => x.Role).SingleOrDefaultAsync(cancellationToken);
        var authorized = role is "Owner" or "Administrator" or "BillingReader";
        CloudUsageSummary? result = null;
        if (authorized)
        {
            // PostgreSQL evaluates the day in UTC regardless of the database session timezone.
            // Membership is repeated in the aggregate query so revoked access cannot expose rows.
            var rows = await db.Database.SqlQuery<CloudUsageDailyTotal>($"""
                SELECT date_trunc('day', u."RecordedAt", 'UTC') AS "DayUtc", u."ApplicationId", u."CapabilityId", u."Unit",
                       sum(u."Quantity")::bigint AS "Quantity", count(*)::bigint AS "Records"
                FROM cloud.usage_records u
                WHERE u."TenantId" = {tenantId} AND u."RecordedAt" >= {from} AND u."RecordedAt" < {to}
                  AND EXISTS (SELECT 1 FROM cloud.memberships m WHERE m."TenantId" = {tenantId}
                    AND m."SubjectIssuer" = {subject.Issuer} AND m."SubjectId" = {subject.SubjectId}
                    AND m."Role" IN ('Owner', 'Administrator', 'BillingReader'))
                GROUP BY 1, u."ApplicationId", u."CapabilityId", u."Unit"
                ORDER BY 1, u."ApplicationId", u."CapabilityId", u."Unit" LIMIT 1001
                """).ToListAsync(cancellationToken);
            result = rows.Count > 1000 ? new([], true) : new(rows, false);
            role = await actor.Select(x => x.Role).SingleOrDefaultAsync(cancellationToken);
            authorized = role is "Owner" or "Administrator" or "BillingReader";
            if (!authorized)
                result = null;
        }
        db.AuditEvents.Add(new CloudAuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = role is null ? null : tenantId,
            SubjectIssuer = subject.Issuer,
            SubjectId = subject.SubjectId,
            ActorRole = role ?? "None",
            Action = "usage.summary.read",
            Outcome = !authorized ? "forbidden" : result!.LimitExceeded ? "limit_exceeded" : "succeeded",
            TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
            OccurredAt = timeProvider.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }
}
