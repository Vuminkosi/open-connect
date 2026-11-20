using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudQuotaStore(IDbContextFactory<CloudControlPlaneDbContext> factory) : ICloudQuotaStore
{
    public async Task<CloudQuotaResult> ReserveAsync(CloudQuotaRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var existing = await FindExistingAsync(db, request, cancellationToken);
        if (existing is not null) return existing;
        var buckets = await db.QuotaBuckets.AsNoTracking().Where(x => x.TenantId == request.TenantId && x.ApplicationId == request.ApplicationId &&
            x.CapabilityId == request.CapabilityId && x.Unit == request.Unit && x.PeriodStart <= now && x.PeriodEnd > now)
            .Select(x => new { x.Id, x.PeriodEnd }).Take(2).ToArrayAsync(cancellationToken);
        if (buckets.Length != 1) return new("quota_unavailable");
        if (request.ExpiresAt > buckets[0].PeriodEnd) return new("invalid_request");
        var updated = await db.QuotaBuckets.Where(x => x.Id == buckets[0].Id && x.TenantId == request.TenantId && x.ApplicationId == request.ApplicationId &&
                x.PeriodStart <= now && x.PeriodEnd > now && x.PeriodEnd >= request.ExpiresAt && x.Limit - x.Consumed - x.Reserved >= request.Amount)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Reserved, y => y.Reserved + request.Amount), cancellationToken);
        if (updated != 1) return await FindExistingAsync(db, request, cancellationToken) ?? new("quota_exceeded");
        var reservation = new CloudQuotaReservation { Id = Guid.NewGuid(), BucketId = buckets[0].Id, TenantId = request.TenantId,
            ApplicationId = request.ApplicationId, RequestId = request.RequestId, RequestFingerprint = request.RequestFingerprint, FingerprintKeyId = request.FingerprintKeyId, Amount = request.Amount, ExpiresAt = request.ExpiresAt };
        db.QuotaReservations.Add(reservation);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new("reserved", reservation.Id);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            return await FindExistingAsync(db, request, cancellationToken) ?? new("quota_unavailable");
        }
    }

    public async Task<CloudFingerprintKeyLookup> FindFingerprintKeyAsync(Guid tenantId, Guid applicationId, Guid requestId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var item = await db.QuotaReservations.AsNoTracking().Where(x => x.TenantId == tenantId && x.ApplicationId == applicationId && x.RequestId == requestId)
            .Select(x => new { x.FingerprintKeyId, x.RequestFingerprint }).SingleOrDefaultAsync(cancellationToken);
        if (item is null) return new("not_found");
        return CloudRequestFingerprint.IsValidKeyId(item.FingerprintKeyId) && item.RequestFingerprint is { Length: 32 }
            ? new("found", item.FingerprintKeyId) : new("legacy_unverifiable");
    }

    public async Task<CloudQuotaResult> FinalizeAsync(Guid tenantId, Guid applicationId, Guid reservationId, long actual, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var reservation = await db.QuotaReservations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == reservationId && x.TenantId == tenantId && x.ApplicationId == applicationId, cancellationToken);
        if (reservation is null) return new("not_found");
        if (reservation.FinalizedAt is not null) return new(reservation.Actual == actual ? "already_finalized" : "idempotency_conflict", reservation.Id);
        if (actual > reservation.Amount) return new("overage_requires_reconciliation", reservation.Id);
        var finalized = await db.QuotaReservations.Where(x => x.Id == reservation.Id && x.TenantId == tenantId && x.ApplicationId == applicationId && x.FinalizedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Actual, actual).SetProperty(y => y.FinalizedAt, now), cancellationToken);
        if (finalized == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            var current = await db.QuotaReservations.AsNoTracking().SingleAsync(x => x.Id == reservationId && x.TenantId == tenantId && x.ApplicationId == applicationId, cancellationToken);
            return new(current.Actual == actual ? "already_finalized" : "idempotency_conflict", reservationId);
        }
        var updated = await db.QuotaBuckets.Where(x => x.Id == reservation.BucketId && x.TenantId == tenantId && x.ApplicationId == applicationId && x.Reserved >= reservation.Amount)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Reserved, y => y.Reserved - reservation.Amount).SetProperty(y => y.Consumed, y => y.Consumed + actual), cancellationToken);
        if (updated != 1) return new("quota_unavailable");
        await transaction.CommitAsync(cancellationToken);
        return new("finalized", reservationId);
    }

    private static async Task<CloudQuotaResult?> FindExistingAsync(CloudControlPlaneDbContext db, CloudQuotaRequest request, CancellationToken cancellationToken)
    {
        var existing = await (from reservation in db.QuotaReservations.AsNoTracking()
            join bucket in db.QuotaBuckets.AsNoTracking() on reservation.BucketId equals bucket.Id
            where reservation.TenantId == request.TenantId && reservation.ApplicationId == request.ApplicationId && reservation.RequestId == request.RequestId
            select new { reservation.Id, reservation.Amount, reservation.ExpiresAt, reservation.RequestFingerprint, reservation.FingerprintKeyId, bucket.CapabilityId, bucket.Unit }).SingleOrDefaultAsync(cancellationToken);
        if (existing is null) return null;
        return new(string.Equals(existing.FingerprintKeyId, request.FingerprintKeyId, StringComparison.Ordinal) && existing.RequestFingerprint is { Length: 32 } && CryptographicOperations.FixedTimeEquals(existing.RequestFingerprint, request.RequestFingerprint) &&
            existing.Amount == request.Amount && existing.ExpiresAt == request.ExpiresAt && existing.CapabilityId == request.CapabilityId && existing.Unit == request.Unit
            ? "duplicate" : "idempotency_conflict", existing.Id);
    }
}
