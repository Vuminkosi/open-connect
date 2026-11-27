using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudPrivacyObservationReader(IDbContextFactory<CloudControlPlaneDbContext> contextFactory, TimeProvider timeProvider) : ICloudPrivacyObservationReader
{
    public async Task<CloudPrivacyObservationPage?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset? from, DateTimeOffset? to,
        CloudPrivacyObservationCursor? before, int limit, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var actor = db.Memberships.AsNoTracking().Where(x => x.TenantId == tenantId && x.SubjectIssuer == subject.Issuer && x.SubjectId == subject.SubjectId);
        var role = await actor.Select(x => x.Role).SingleOrDefaultAsync(cancellationToken);
        if (role is not ("Owner" or "Administrator" or "SecurityAnalyst"))
        {
            await AuditAsync(db, subject, tenantId, role ?? "None", "forbidden", cancellationToken);
            return null;
        }
        var membership = actor.Where(x => x.Role == "Owner" || x.Role == "Administrator" || x.Role == "SecurityAnalyst");
        var query = db.PrivacyEvidence.AsNoTracking().Where(x => x.TenantId == tenantId && membership.Any());
        if (from is not null)
            query = query.Where(x => x.OccurredAt >= from.Value);
        if (to is not null)
            query = query.Where(x => x.OccurredAt < to.Value);
        if (before is not null)
            query = query.Where(x => x.OccurredAt < before.OccurredAt ||
            x.OccurredAt == before.OccurredAt && x.Id.CompareTo(before.Id) < 0);
        var rows = await query.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id).Take(limit + 1)
            .Select(x => new { x.Id, x.ApplicationId, x.RequestId, x.Outcome, x.TraceId, x.OccurredAt, x.CategoryCounts })
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);
        var last = rows.LastOrDefault();
        await AuditAsync(db, subject, tenantId, role, "succeeded", cancellationToken);
        var observations = rows.Select(row => new CloudPrivacyObservation(row.Id, row.ApplicationId, row.RequestId,
            row.Outcome, row.TraceId, row.OccurredAt)
        { Categories = CloudPrivacyCategories.Decode(row.CategoryCounts, row.Outcome) }).ToList();
        return new(observations, hasMore && last is not null ? new(last.OccurredAt, last.Id) : null);
    }

    private Task AuditAsync(CloudControlPlaneDbContext db, CloudSubject subject, Guid tenantId, string role, string outcome, CancellationToken cancellationToken)
    {
        db.AuditEvents.Add(new CloudAuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SubjectIssuer = subject.Issuer,
            SubjectId = subject.SubjectId,
            ActorRole = role,
            Action = "privacy.observations.read",
            Outcome = outcome,
            TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
            OccurredAt = timeProvider.GetUtcNow()
        });
        return db.SaveChangesAsync(cancellationToken);
    }
}
