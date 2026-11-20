using System.Data;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudEntitlementAdministrationStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudEntitlementAdministrationStore
{
    public async Task<CloudTenantEntitlement?> GetAsync(Guid tenantId, string capabilityId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.TenantEntitlements.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.CapabilityId == capabilityId, cancellationToken);
    }

    public async Task<CloudEntitlementResult> PutAsync(CloudSubject actor, string traceId, Guid tenantId, string capabilityId, CloudEntitlementChange change, int revision, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var current = await db.TenantEntitlements.FromSqlInterpolated($"SELECT * FROM cloud.tenant_entitlements WHERE \"TenantId\" = {tenantId} AND \"CapabilityId\" = {capabilityId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            string? failure = null;
            if (current is null)
            {
                if (revision != 0) failure = "conflict";
                else if (!await db.Tenants.AnyAsync(x => x.Id == tenantId, cancellationToken) || !await db.Capabilities.AnyAsync(x => x.Id == capabilityId, cancellationToken)) failure = "invalid_request";
                else db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenantId, CapabilityId = capabilityId, PlanGranted = change.PlanGranted, ExpiresAt = change.ExpiresAt, Revision = 1 });
            }
            else if (current.Revision < 1 || current.Revision != revision) failure = "conflict";
            else
            {
                var updated = await db.TenantEntitlements.Where(x => x.TenantId == tenantId && x.CapabilityId == capabilityId && x.Revision == revision)
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.PlanGranted, change.PlanGranted).SetProperty(y => y.ExpiresAt, change.ExpiresAt), cancellationToken);
                if (updated != 1) failure = "conflict";
            }
            db.AuditEvents.Add(Audit(actor, traceId, tenantId, capabilityId, failure ?? "succeeded", false));
            await db.SaveChangesAsync(cancellationToken);
            var result = failure is null ? await db.TenantEntitlements.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.CapabilityId == capabilityId, cancellationToken) : null;
            await transaction.CommitAsync(cancellationToken);
            return failure is null ? new("succeeded", result) : new(failure);
        }
        catch (Exception exception) when (IsConflict(exception))
        {
            await RecordAsync(actor, traceId, tenantId, capabilityId, "conflict", false, cancellationToken);
            return new("conflict");
        }
    }

    public async Task RecordAsync(CloudSubject actor, string traceId, Guid tenantId, string capabilityId, string outcome, bool denied, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.AuditEvents.Add(Audit(actor, traceId, tenantId, capabilityId, outcome, denied));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation }) return true;
        return false;
    }

    private CloudAuditEvent Audit(CloudSubject actor, string traceId, Guid tenantId, string capabilityId, string outcome, bool denied) => new()
    {
        Id = Guid.NewGuid(), TenantId = null, SubjectIssuer = actor.Issuer, SubjectId = actor.SubjectId,
        ActorRole = denied ? "AuthenticatedUser" : "PlatformOperator", Action = "entitlement.write",
        ResourceId = $"{tenantId:D}/{(CloudDeploymentRegistry.ValidToken(capabilityId, 80) ? capabilityId : "invalid")}",
        Outcome = outcome, TraceId = traceId, OccurredAt = timeProvider.GetUtcNow()
    };
}
