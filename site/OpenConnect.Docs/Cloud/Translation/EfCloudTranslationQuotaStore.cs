using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudTranslationQuotaStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider clock) : ICloudTranslationQuotaStore
{
    public async Task<CloudQuotaResult> ReserveAsync(CloudQuotaRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var application = await db.Applications.FromSqlInterpolated($"SELECT * FROM cloud.applications WHERE \"Id\" = {request.ApplicationId} AND \"TenantId\" = {request.TenantId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (application is null || !await EfCloudTranslationPrivacyAdmission.IsAllowedAsync(db, request.TenantId, request.ApplicationId,
            request.RequestId, request.FingerprintKeyId, request.RequestFingerprint, request.Amount, cancellationToken)) return new("privacy_unavailable");
        now = clock.GetUtcNow();
        if (request.ExpiresAt <= now) return new("invalid_request");
        var existing = await FindExistingAsync(db, request, cancellationToken);
        if (existing is not null) return existing;
        var buckets = await db.QuotaBuckets.AsNoTracking().Where(x => x.TenantId == request.TenantId && x.ApplicationId == request.ApplicationId &&
            x.CapabilityId == "translation.translate" && x.Unit == "character" && x.PeriodStart <= now && x.PeriodEnd > now)
            .Select(x => new { x.Id, x.PeriodEnd }).Take(2).ToArrayAsync(cancellationToken);
        if (buckets.Length != 1) return new("quota_unavailable");
        if (request.ExpiresAt > buckets[0].PeriodEnd) return new("invalid_request");
        var updated = await db.QuotaBuckets.Where(x => x.Id == buckets[0].Id && x.TenantId == request.TenantId && x.ApplicationId == request.ApplicationId &&
                x.PeriodStart <= now && x.PeriodEnd > now && x.PeriodEnd >= request.ExpiresAt && x.Limit - x.Consumed - x.Reserved >= request.Amount)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Reserved, y => y.Reserved + request.Amount), cancellationToken);
        if (updated != 1) return new("quota_exceeded");
        var reservation = new CloudQuotaReservation { Id = Guid.NewGuid(), BucketId = buckets[0].Id, TenantId = request.TenantId,
            ApplicationId = request.ApplicationId, RequestId = request.RequestId, RequestFingerprint = request.RequestFingerprint,
            FingerprintKeyId = request.FingerprintKeyId, Amount = request.Amount, ExpiresAt = request.ExpiresAt };
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

    private static async Task<CloudQuotaResult?> FindExistingAsync(CloudControlPlaneDbContext db, CloudQuotaRequest request, CancellationToken cancellationToken)
    {
        var existing = await (from reservation in db.QuotaReservations.AsNoTracking()
            join bucket in db.QuotaBuckets.AsNoTracking() on reservation.BucketId equals bucket.Id
            where reservation.TenantId == request.TenantId && reservation.ApplicationId == request.ApplicationId && reservation.RequestId == request.RequestId
            select new { reservation.Id, reservation.Amount, reservation.ExpiresAt, reservation.RequestFingerprint, reservation.FingerprintKeyId, bucket.CapabilityId, bucket.Unit }).SingleOrDefaultAsync(cancellationToken);
        if (existing is null) return null;
        return new(string.Equals(existing.FingerprintKeyId, request.FingerprintKeyId, StringComparison.Ordinal) &&
            existing.RequestFingerprint is { Length: 32 } && CryptographicOperations.FixedTimeEquals(existing.RequestFingerprint, request.RequestFingerprint) &&
            existing.Amount == request.Amount && existing.ExpiresAt == request.ExpiresAt && existing.CapabilityId == request.CapabilityId && existing.Unit == request.Unit
            ? "duplicate" : "idempotency_conflict", existing.Id);
    }
}
