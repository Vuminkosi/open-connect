using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public interface ICloudPrivacyCaseReader
{
    Task<CloudPrivacyObservation?> GetAsync(CloudSubject subject, Guid tenantId, Guid caseId, CancellationToken cancellationToken);
}

public sealed class EfCloudPrivacyCaseReader(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider clock) : ICloudPrivacyCaseReader
{
    public Task<CloudPrivacyObservation?> GetAsync(CloudSubject subject, Guid tenantId, Guid caseId, CancellationToken cancellationToken) =>
        CloudPrivacyCaseReader.GetAsync(factory, clock, subject, tenantId, caseId, cancellationToken);
}

/// <summary>Reads a recorded Cloud decision as evidence for an authorized tenant security case.</summary>
public static class CloudPrivacyCaseReader
{
    public static async Task<CloudPrivacyObservation?> GetAsync(IDbContextFactory<CloudControlPlaneDbContext> factory,
        TimeProvider clock, CloudSubject subject, Guid tenantId, Guid caseId, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || caseId == Guid.Empty) throw new ArgumentException("Invalid privacy case query.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var actor = db.Memberships.AsNoTracking().Where(item => item.TenantId == tenantId &&
            item.SubjectIssuer == subject.Issuer && item.SubjectId == subject.SubjectId);
        var role = await actor.Select(item => item.Role).SingleOrDefaultAsync(cancellationToken);
        var authorized = role is "Owner" or "Administrator" or "SecurityAnalyst";
        CloudPrivacyObservation? result = null;
        if (authorized)
        {
            var membership = actor.Where(item => item.Role == "Owner" || item.Role == "Administrator" || item.Role == "SecurityAnalyst");
            var row = await db.PrivacyEvidence.AsNoTracking().Where(item => item.TenantId == tenantId &&
                item.Id == caseId && membership.Any()).Select(item => new { item.Id, item.ApplicationId, item.RequestId,
                    item.Outcome, item.TraceId, item.OccurredAt, item.CategoryCounts }).SingleOrDefaultAsync(cancellationToken);
            if (row is not null)
                result = new CloudPrivacyObservation(row.Id, row.ApplicationId, row.RequestId, row.Outcome, row.TraceId, row.OccurredAt)
                { Categories = CloudPrivacyCategories.Decode(row.CategoryCounts, row.Outcome) };
            role = await actor.Select(item => item.Role).SingleOrDefaultAsync(cancellationToken);
            authorized = role is "Owner" or "Administrator" or "SecurityAnalyst";
            if (!authorized) result = null;
        }
        db.AuditEvents.Add(new CloudAuditEvent { Id = Guid.NewGuid(), TenantId = tenantId, SubjectIssuer = subject.Issuer,
            SubjectId = subject.SubjectId, ActorRole = role ?? "None", Action = "privacy.case.read",
            ResourceId = caseId.ToString("D"),
            Outcome = !authorized ? "forbidden" : result is null ? "not_found" : "succeeded",
            TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"), OccurredAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }
}
