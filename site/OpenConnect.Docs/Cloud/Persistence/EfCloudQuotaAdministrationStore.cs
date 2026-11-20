using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudQuotaAdministrationStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudQuotaAdministrationStore
{
    public async Task<CloudQuotaBucket?> GetAsync(Guid tenantId, Guid applicationId, Guid bucketId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.QuotaBuckets.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ApplicationId == applicationId && x.Id == bucketId, cancellationToken);
    }

    public async Task<CloudQuotaPolicyResult> PutAsync(CloudSubject actor, string traceId, Guid tenantId, Guid applicationId, Guid bucketId, CloudQuotaPolicy policy, long revision, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var current = await db.QuotaBuckets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == bucketId, cancellationToken);
            string? failure = null;
            if (revision == 0)
            {
                if (current is not null) failure = "conflict";
                else if (!await db.Applications.AnyAsync(x => x.TenantId == tenantId && x.Id == applicationId, cancellationToken)) failure = "invalid_request";
                else if (await db.Capabilities.AsNoTracking().Where(x => x.Id == policy.CapabilityId).Select(x => x.BillingUnit).SingleOrDefaultAsync(cancellationToken) is not { } billingDescription ||
                    !CloudQuotaUnits.Supports(policy.CapabilityId, billingDescription, policy.Unit)) failure = "invalid_request";
                else if (await db.QuotaBuckets.AnyAsync(x => x.TenantId == tenantId && x.ApplicationId == applicationId && x.CapabilityId == policy.CapabilityId && x.Unit == policy.Unit && x.PeriodStart < policy.PeriodEnd && x.PeriodEnd > policy.PeriodStart, cancellationToken)) failure = "conflict";
                else db.QuotaBuckets.Add(new CloudQuotaBucket { Id = bucketId, TenantId = tenantId, ApplicationId = applicationId, CapabilityId = policy.CapabilityId, Unit = policy.Unit, PeriodStart = policy.PeriodStart, PeriodEnd = policy.PeriodEnd, Limit = policy.Limit });
            }
            else if (current is null || current.TenantId != tenantId || current.ApplicationId != applicationId || current.Revision != revision) failure = "conflict";
            else if (current.CapabilityId != policy.CapabilityId || current.Unit != policy.Unit || current.PeriodStart != policy.PeriodStart || current.PeriodEnd != policy.PeriodEnd) failure = "invalid_request";
            else if (current.Reserved > policy.Limit || current.Consumed > policy.Limit - current.Reserved) failure = "invalid_request";
            else if (current.Limit != policy.Limit)
            {
                var changed = await db.QuotaBuckets.Where(x => x.Id == bucketId && x.TenantId == tenantId && x.ApplicationId == applicationId && x.Revision == revision && x.Reserved <= policy.Limit && x.Consumed <= policy.Limit - x.Reserved)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.Limit, policy.Limit), cancellationToken);
                if (changed != 1) failure = "conflict";
            }
            db.AuditEvents.Add(Audit(actor, traceId, tenantId, bucketId, failure ?? "succeeded", false));
            await db.SaveChangesAsync(cancellationToken);
            var result = failure is null ? await db.QuotaBuckets.AsNoTracking().SingleAsync(x => x.Id == bucketId && x.TenantId == tenantId && x.ApplicationId == applicationId, cancellationToken) : null;
            await transaction.CommitAsync(cancellationToken);
            return failure is null ? new("succeeded", result) : new(failure);
        }
        catch (Exception exception) when (IsConflict(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            await RecordAsync(actor, traceId, tenantId, bucketId, "conflict", false, cancellationToken);
            return new("conflict");
        }
    }

    private static bool IsConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation }) return true;
        return false;
    }

    public async Task RecordAsync(CloudSubject actor, string traceId, Guid tenantId, Guid bucketId, string outcome, bool denied, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.AuditEvents.Add(Audit(actor, traceId, tenantId, bucketId, outcome, denied));
        await db.SaveChangesAsync(cancellationToken);
    }

    private CloudAuditEvent Audit(CloudSubject actor, string traceId, Guid tenantId, Guid bucketId, string outcome, bool denied) => new()
    {
        Id = Guid.NewGuid(), TenantId = null, SubjectIssuer = actor.Issuer, SubjectId = actor.SubjectId,
        ActorRole = denied ? "AuthenticatedUser" : "PlatformOperator", Action = "quota.write",
        ResourceId = $"{tenantId:D}/{bucketId:D}", Outcome = outcome, TraceId = traceId, OccurredAt = timeProvider.GetUtcNow()
    };
}
