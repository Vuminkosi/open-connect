using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudCapabilityAvailabilityStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider)
    : ICloudCapabilityAvailabilityStore
{
    private sealed class AvailabilityRow
    {
        public required string CapabilityId { get; init; }
        public required string Availability { get; init; }
        public long Revision { get; init; }
    }

    public async Task<CloudCapabilityAvailabilityView?> GetAsync(string capabilityId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var value = await db.Database.SqlQuery<AvailabilityRow>($"""
            SELECT c."Id" AS "CapabilityId", c."PlatformAvailability" AS "Availability", r."Revision"
            FROM cloud.capabilities c JOIN cloud.capability_availability_revisions r ON r."CapabilityId" = c."Id"
            WHERE c."Id" = {capabilityId}
            """).SingleOrDefaultAsync(cancellationToken);
        return value is null ? null : new(value.CapabilityId, value.Availability, value.Revision);
    }

    public async Task<CloudCapabilityAvailabilityResult> PutAsync(CloudSubject actor, string traceId, string capabilityId,
        string availability, long revision, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var capability = await db.Capabilities.FromSqlInterpolated($"SELECT * FROM cloud.capabilities WHERE \"Id\" = {capabilityId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (capability is null) { await RecordAsync(actor, traceId, capabilityId, "not_found", false, cancellationToken); return new("not_found"); }
        var revisions = await db.Database.SqlQuery<long>($"SELECT \"Revision\" AS \"Value\" FROM cloud.capability_availability_revisions WHERE \"CapabilityId\" = {capabilityId}")
            .ToListAsync(cancellationToken);
        if (revisions.Count != 1) return new("unavailable");
        if (revisions[0] != revision) { await RecordAsync(actor, traceId, capabilityId, "conflict", false, cancellationToken); return new("conflict"); }
        // Hold matching inventory rows until the declaration commits. Inventory
        // can still change afterward; this declaration is not a live health check.
        if (availability == "available" && (await db.WorkerDeployments.FromSqlInterpolated($"""
            SELECT * FROM cloud.worker_deployments WHERE "CapabilityId" = {capabilityId} AND "Enabled" = TRUE FOR SHARE
            """).ToListAsync(cancellationToken)).Count == 0)
        {
            await RecordAsync(actor, traceId, capabilityId, "deployment_required", false, cancellationToken);
            return new("deployment_required");
        }
        capability.PlatformAvailability = availability;
        db.AuditEvents.Add(Audit(actor, traceId, capabilityId, "succeeded", false));
        await db.SaveChangesAsync(cancellationToken);
        var next = await db.Database.SqlQuery<long>($"SELECT \"Revision\" AS \"Value\" FROM cloud.capability_availability_revisions WHERE \"CapabilityId\" = {capabilityId}")
            .SingleAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new("succeeded", new(capabilityId, availability, next));
    }

    public async Task RecordAsync(CloudSubject actor, string traceId, string capabilityId, string outcome, bool denied, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.AuditEvents.Add(Audit(actor, traceId, capabilityId, outcome, denied));
        await db.SaveChangesAsync(cancellationToken);
    }

    private CloudAuditEvent Audit(CloudSubject actor, string traceId, string capabilityId, string outcome, bool denied) => new()
    {
        Id = Guid.NewGuid(), SubjectIssuer = actor.Issuer, SubjectId = actor.SubjectId, ActorRole = denied ? "AuthenticatedUser" : "PlatformOperator",
        Action = "catalog.availability.changed", ResourceId = capabilityId.Length <= 80 ? capabilityId : null,
        Outcome = outcome, TraceId = traceId, OccurredAt = timeProvider.GetUtcNow(),
    };
}
