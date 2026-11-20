using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudUsageReader(IDbContextFactory<CloudControlPlaneDbContext> contextFactory, TimeProvider timeProvider) : ICloudUsageReader
{
    public Task<CloudUsagePage?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset? from, DateTimeOffset? to,
        CloudUsageCursor? before, int limit, CancellationToken cancellationToken) =>
        ReadAsync(subject, tenantId, from, to, before, limit, false, cancellationToken);

    public Task<CloudUsagePage?> ExportTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        ReadAsync(subject, tenantId, from, to, null, 10000, true, cancellationToken);

    private async Task<CloudUsagePage?> ReadAsync(CloudSubject subject, Guid tenantId, DateTimeOffset? from, DateTimeOffset? to,
        CloudUsageCursor? before, int limit, bool export, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var actor = db.Memberships.AsNoTracking().Where(x => x.TenantId == tenantId && x.SubjectIssuer == subject.Issuer && x.SubjectId == subject.SubjectId);
        var role = await actor.Select(x => x.Role).SingleOrDefaultAsync(cancellationToken);
        if (role is not ("Owner" or "Administrator" or "BillingReader"))
        {
            await AuditAsync(db, subject, role is null ? null : tenantId, role ?? "None", export ? "usage.records.export" : "usage.records.read", "forbidden", cancellationToken);
            return null;
        }
        var membership = actor.Where(x => x.Role == "Owner" || x.Role == "Administrator" || x.Role == "BillingReader");
        var query = db.UsageRecords.AsNoTracking().Where(x => x.TenantId == tenantId && membership.Any());
        if (from is not null) query = query.Where(x => x.RecordedAt >= from.Value);
        if (to is not null) query = query.Where(x => x.RecordedAt < to.Value);
        if (before is not null) query = query.Where(x => x.RecordedAt < before.RecordedAt ||
            x.RecordedAt == before.RecordedAt && x.ReservationId.CompareTo(before.ReservationId) < 0);
        var rows = await query.OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.ReservationId).Take(limit + 1)
            .Select(x => new CloudUsageEntry(x.ReservationId, x.ApplicationId, x.RequestId, x.CapabilityId,
                x.Unit, x.Quantity, x.Provenance, x.RecordedAt)).ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var last = rows.LastOrDefault();
        await AuditAsync(db, subject, tenantId, role, export ? "usage.records.export" : "usage.records.read", export && hasMore ? "limit_exceeded" : "succeeded", cancellationToken);
        return new(rows, hasMore && last is not null ? new(last.RecordedAt, last.ReservationId) : null);
    }

    private Task AuditAsync(CloudControlPlaneDbContext db, CloudSubject subject, Guid? tenantId, string role, string action, string outcome, CancellationToken cancellationToken)
    {
        db.AuditEvents.Add(new CloudAuditEvent { Id = Guid.NewGuid(), TenantId = tenantId, SubjectIssuer = subject.Issuer,
            SubjectId = subject.SubjectId, ActorRole = role, Action = action, Outcome = outcome,
            TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"), OccurredAt = timeProvider.GetUtcNow() });
        return db.SaveChangesAsync(cancellationToken);
    }
}
