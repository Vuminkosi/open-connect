using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudKnowledgeEmbeddingUsageDailyTotal(DateTime DayUtc, Guid ApplicationId, long InputCharacters, long Records);
public sealed record CloudKnowledgeEmbeddingUsageSummary(IReadOnlyList<CloudKnowledgeEmbeddingUsageDailyTotal> Totals, bool LimitExceeded);

public interface ICloudKnowledgeEmbeddingUsageReader
{
    Task<CloudKnowledgeEmbeddingUsageSummary?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset from,
        DateTimeOffset to, CancellationToken cancellationToken);
}

public sealed class EfCloudKnowledgeEmbeddingUsageReader(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider clock)
    : ICloudKnowledgeEmbeddingUsageReader
{
    public async Task<CloudKnowledgeEmbeddingUsageSummary?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset from,
        DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || from >= to || to - from > TimeSpan.FromDays(93)) throw new ArgumentException("Invalid usage range.");
        from = from.ToUniversalTime();
        to = to.ToUniversalTime();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var actor = db.Memberships.AsNoTracking().Where(x => x.TenantId == tenantId && x.SubjectIssuer == subject.Issuer && x.SubjectId == subject.SubjectId);
        var role = await actor.Select(x => x.Role).SingleOrDefaultAsync(cancellationToken);
        var authorized = role is "Owner" or "Administrator" or "BillingReader";
        CloudKnowledgeEmbeddingUsageSummary? result = null;
        if (authorized)
        {
            var rows = await db.Database.SqlQuery<CloudKnowledgeEmbeddingUsageDailyTotal>($"""
                SELECT date_trunc('day', u."RecordedAt", 'UTC') AS "DayUtc", u."ApplicationId",
                    SUM(u."InputCharacters")::bigint AS "InputCharacters", COUNT(*)::bigint AS "Records"
                FROM cloud.knowledge_embedding_usage u
                WHERE u."TenantId" = {tenantId} AND u."RecordedAt" >= {from} AND u."RecordedAt" < {to}
                  AND EXISTS (SELECT 1 FROM cloud.memberships m WHERE m."TenantId" = {tenantId}
                    AND m."SubjectIssuer" = {subject.Issuer} AND m."SubjectId" = {subject.SubjectId}
                    AND m."Role" IN ('Owner', 'Administrator', 'BillingReader'))
                GROUP BY 1, u."ApplicationId" ORDER BY 1, u."ApplicationId" LIMIT 1001
                """).ToListAsync(cancellationToken);
            result = rows.Count > 1000 ? new([], true) : new(rows, false);
            role = await actor.Select(x => x.Role).SingleOrDefaultAsync(cancellationToken);
            authorized = role is "Owner" or "Administrator" or "BillingReader";
            if (!authorized) result = null;
        }
        db.AuditEvents.Add(new CloudAuditEvent { Id = Guid.NewGuid(), TenantId = role is null ? null : tenantId,
            SubjectIssuer = subject.Issuer, SubjectId = subject.SubjectId, ActorRole = role ?? "None", Action = "knowledge_embedding.usage.read",
            Outcome = !authorized ? "forbidden" : result!.LimitExceeded ? "limit_exceeded" : "succeeded",
            TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"), OccurredAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }
}
