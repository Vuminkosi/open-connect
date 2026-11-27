using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudKnowledgeEmbeddingPrivacyCase(Guid Id, Guid ApplicationId, Guid RequestId, string Direction, string Outcome,
    string TraceId, DateTimeOffset OccurredAt, IReadOnlyList<CloudPrivacyCategoryCount> Categories);
public sealed record CloudKnowledgeEmbeddingPrivacyTotal(string Direction, string Outcome, long Count);
public sealed record CloudKnowledgeEmbeddingCategoryTotal(string Direction, string Category, long Count);
public sealed record CloudKnowledgeEmbeddingPrivacyDashboard(IReadOnlyList<CloudKnowledgeEmbeddingPrivacyCase> RecentCases,
    IReadOnlyList<CloudKnowledgeEmbeddingPrivacyTotal> Totals, IReadOnlyList<CloudKnowledgeEmbeddingCategoryTotal> Categories);

public interface ICloudKnowledgeEmbeddingPrivacyReader
{
    Task<CloudKnowledgeEmbeddingPrivacyDashboard?> GetTenantAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken);
}

public sealed class EfCloudKnowledgeEmbeddingPrivacyReader(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider clock)
    : ICloudKnowledgeEmbeddingPrivacyReader
{
    private sealed class CaseRow
    {
        public Guid Id { get; set; }
        public Guid ApplicationId { get; set; }
        public Guid RequestId { get; set; }
        public string Direction { get; set; } = "";
        public string Outcome { get; set; } = "";
        public string TraceId { get; set; } = "";
        public DateTimeOffset OccurredAt { get; set; }
        public string CategoryCounts { get; set; } = "";
    }
    private sealed class CountRow
    {
        public string Direction { get; set; } = "";
        public string Label { get; set; } = "";
        public long Count { get; set; }
    }

    public async Task<CloudKnowledgeEmbeddingPrivacyDashboard?> GetTenantAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Invalid tenant.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var actor = db.Memberships.AsNoTracking().Where(x => x.TenantId == tenantId && x.SubjectIssuer == subject.Issuer && x.SubjectId == subject.SubjectId);
        var role = await actor.Select(x => x.Role).SingleOrDefaultAsync(cancellationToken);
        var authorized = role is "Owner" or "Administrator" or "SecurityAnalyst";
        CloudKnowledgeEmbeddingPrivacyDashboard? result = null;
        if (authorized)
        {
            var cases = await db.Database.SqlQuery<CaseRow>($"""
                SELECT "Id", "ApplicationId", "RequestId", "Direction", "Outcome", "TraceId", "OccurredAt", "CategoryCounts"
                FROM cloud.knowledge_embedding_privacy_evidence WHERE "TenantId" = {tenantId}
                  AND EXISTS (SELECT 1 FROM cloud.memberships m WHERE m."TenantId" = {tenantId}
                    AND m."SubjectIssuer" = {subject.Issuer} AND m."SubjectId" = {subject.SubjectId}
                    AND m."Role" IN ('Owner', 'Administrator', 'SecurityAnalyst'))
                ORDER BY "OccurredAt" DESC, "Id" DESC LIMIT 50
                """).ToListAsync(cancellationToken);
            var totals = await db.Database.SqlQuery<CountRow>($"""
                SELECT "Direction", "Outcome" AS "Label", COUNT(*)::bigint AS "Count"
                FROM cloud.knowledge_embedding_privacy_evidence WHERE "TenantId" = {tenantId}
                  AND EXISTS (SELECT 1 FROM cloud.memberships m WHERE m."TenantId" = {tenantId}
                    AND m."SubjectIssuer" = {subject.Issuer} AND m."SubjectId" = {subject.SubjectId}
                    AND m."Role" IN ('Owner', 'Administrator', 'SecurityAnalyst'))
                GROUP BY "Direction", "Outcome"
                """).ToListAsync(cancellationToken);
            var categories = await db.Database.SqlQuery<CountRow>($"""
                SELECT e."Direction", item.key AS "Label", SUM(item.value::bigint)::bigint AS "Count"
                FROM cloud.knowledge_embedding_privacy_evidence e CROSS JOIN LATERAL jsonb_each_text(e."CategoryCounts"::jsonb) item
                WHERE e."TenantId" = {tenantId}
                  AND EXISTS (SELECT 1 FROM cloud.memberships m WHERE m."TenantId" = {tenantId}
                    AND m."SubjectIssuer" = {subject.Issuer} AND m."SubjectId" = {subject.SubjectId}
                    AND m."Role" IN ('Owner', 'Administrator', 'SecurityAnalyst'))
                GROUP BY e."Direction", item.key
                """).ToListAsync(cancellationToken);
            result = new(cases.Select(row => new CloudKnowledgeEmbeddingPrivacyCase(row.Id, row.ApplicationId, row.RequestId, row.Direction,
                row.Outcome, row.TraceId, row.OccurredAt, CloudPrivacyCategories.Decode(row.CategoryCounts, row.Outcome) ?? [])).ToArray(),
                totals.Select(row => new CloudKnowledgeEmbeddingPrivacyTotal(row.Direction, row.Label, row.Count)).ToArray(),
                categories.Select(row => new CloudKnowledgeEmbeddingCategoryTotal(row.Direction, row.Label, row.Count)).ToArray());
            role = await actor.Select(x => x.Role).SingleOrDefaultAsync(cancellationToken);
            authorized = role is "Owner" or "Administrator" or "SecurityAnalyst";
            if (!authorized)
                result = null;
        }
        db.AuditEvents.Add(new CloudAuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SubjectIssuer = subject.Issuer,
            SubjectId = subject.SubjectId,
            ActorRole = role ?? "None",
            Action = "knowledge_embedding.privacy.read",
            Outcome = authorized ? "succeeded" : "forbidden",
            TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
            OccurredAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }
}
