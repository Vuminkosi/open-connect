using System.Data;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudRegionPolicyStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudRegionPolicyStore
{
    public async Task<CloudRegionPolicyResult> GetAsync(CloudSubject actor, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.Memberships.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.SubjectIssuer == actor.Issuer && x.SubjectId == actor.SubjectId, cancellationToken)) return new("not_found");
        var policy = await db.RegionPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        return new("succeeded", policy is null ? new([], 0) : new(JsonSerializer.Deserialize<string[]>(policy.AllowedRegions) ?? [], policy.Revision));
    }

    public async Task<CloudRegionPolicyResult> PutAsync(CloudSubject actor, Guid tenantId, string[] regions, int revision, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var membership = await db.Memberships.FromSqlInterpolated($"SELECT * FROM cloud.memberships WHERE \"TenantId\" = {tenantId} AND \"SubjectIssuer\" = {actor.Issuer} AND \"SubjectId\" = {actor.SubjectId} FOR SHARE")
                .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            if (membership is null) { await RecordRejectedAsync(actor, tenantId, "not_found", cancellationToken); return new("not_found"); }
            if (membership.Role is not ("Owner" or "Administrator")) { await RecordRejectedAsync(actor, tenantId, "forbidden", cancellationToken); return new("forbidden"); }
            var current = await db.RegionPolicies.SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
            if ((current?.Revision ?? 0) != revision) { await RecordRejectedAsync(actor, tenantId, "conflict", cancellationToken); return new("conflict"); }
            if (current is null)
                db.RegionPolicies.Add(new CloudRegionPolicy { TenantId = tenantId, AllowedRegions = JsonSerializer.Serialize(regions), Revision = 1 });
            else { current.AllowedRegions = JsonSerializer.Serialize(regions); current.Revision = revision + 1; }
            db.AuditEvents.Add(new CloudAuditEvent { Id = Guid.NewGuid(), TenantId = tenantId, SubjectIssuer = actor.Issuer,
                SubjectId = actor.SubjectId, ActorRole = membership.Role, Action = "tenant.region_policy.changed", ResourceId = tenantId.ToString("D"),
                Outcome = "succeeded", TraceId = Activity.Current?.TraceId.ToString(), OccurredAt = timeProvider.GetUtcNow() });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new("succeeded", new(regions, revision + 1));
        }
        catch (DbUpdateConcurrencyException) { await RecordRejectedAsync(actor, tenantId, "conflict", cancellationToken); return new("conflict"); }
        catch (Exception exception) when (IsConflict(exception)) { await RecordRejectedAsync(actor, tenantId, "conflict", cancellationToken); return new("conflict"); }
    }

    public async Task RecordRejectedAsync(CloudSubject actor, Guid tenantId, string outcome, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.AuditEvents.Add(new CloudAuditEvent { Id = Guid.NewGuid(), TenantId = null, SubjectIssuer = actor.Issuer, SubjectId = actor.SubjectId,
            ActorRole = "AuthenticatedUser", Action = "tenant.region_policy.changed", ResourceId = tenantId.ToString("D"), Outcome = outcome,
            TraceId = Activity.Current?.TraceId.ToString(), OccurredAt = timeProvider.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation }) return true;
        return false;
    }
}
