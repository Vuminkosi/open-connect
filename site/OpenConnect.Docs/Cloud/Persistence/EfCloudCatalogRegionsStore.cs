using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudCatalogRegionsStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudCatalogRegionsStore
{
    public async Task<CloudCatalogRegionsView?> GetAsync(string capabilityId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var result = await (from capability in db.Capabilities.AsNoTracking()
                            join revision in db.CatalogRegionRevisions.AsNoTracking() on capability.Id equals revision.CapabilityId
                            where capability.Id == capabilityId
                            select new { capability.AllowedRegions, capability.AllowedRegionsVerified, revision.Revision }).SingleOrDefaultAsync(cancellationToken);
        return result is null ? null : new(capabilityId, JsonSerializer.Deserialize<string[]>(result.AllowedRegions) ?? [], result.AllowedRegionsVerified, result.Revision);
    }

    public async Task<CloudCatalogRegionsResult> PutAsync(CloudSubject actor, string traceId, string capabilityId, string[] regions, bool verified, long revision, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var capability = await db.Capabilities.FromSqlInterpolated($"SELECT * FROM cloud.capabilities WHERE \"Id\" = {capabilityId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (capability is null)
        { await RecordAsync(actor, traceId, capabilityId, "not_found", false, cancellationToken); return new("not_found"); }
        var current = await db.CatalogRegionRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.CapabilityId == capabilityId, cancellationToken);
        if (current is null)
            return new("unavailable");
        if (current.Revision != revision)
        { await RecordAsync(actor, traceId, capabilityId, "conflict", false, cancellationToken); return new("conflict"); }
        capability.AllowedRegions = JsonSerializer.Serialize(regions);
        capability.AllowedRegionsVerified = verified;
        db.AuditEvents.Add(Audit(actor, traceId, capabilityId, "succeeded", false));
        await db.SaveChangesAsync(cancellationToken);
        var next = await db.CatalogRegionRevisions.AsNoTracking().Where(x => x.CapabilityId == capabilityId).Select(x => x.Revision).SingleAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new("succeeded", new(capabilityId, regions, verified, next));
    }

    public async Task RecordAsync(CloudSubject actor, string traceId, string capabilityId, string outcome, bool denied, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.AuditEvents.Add(Audit(actor, traceId, capabilityId, outcome, denied));
        await db.SaveChangesAsync(cancellationToken);
    }

    private CloudAuditEvent Audit(CloudSubject actor, string traceId, string capabilityId, string outcome, bool denied) => new()
    {
        Id = Guid.NewGuid(),
        SubjectIssuer = actor.Issuer,
        SubjectId = actor.SubjectId,
        ActorRole = denied ? "AuthenticatedUser" : "PlatformOperator",
        Action = "catalog.regions.changed",
        ResourceId = capabilityId.Length <= 80 ? capabilityId : null,
        Outcome = outcome,
        TraceId = traceId,
        OccurredAt = timeProvider.GetUtcNow()
    };
}
