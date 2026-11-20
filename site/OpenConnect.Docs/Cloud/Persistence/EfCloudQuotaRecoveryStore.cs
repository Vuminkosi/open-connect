using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudQuotaRecoveryStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudQuotaRecoveryStore
{
    public async Task<int> ReleaseUnclaimedAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            throw new InvalidOperationException("Quota recovery requires all control-plane migrations.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var reservations = await db.QuotaReservations.FromSqlInterpolated($"""
            SELECT r.* FROM cloud.quota_reservations r
            WHERE r."FinalizedAt" IS NULL AND r."ExpiresAt" <= {cutoff} AND r."Amount" = 1
              AND EXISTS (SELECT 1 FROM cloud.quota_buckets b WHERE b."Id" = r."BucketId" AND b."TenantId" = r."TenantId"
                  AND b."ApplicationId" = r."ApplicationId" AND b."CapabilityId" = 'classification.classify' AND b."Unit" = 'request')
              AND NOT EXISTS (SELECT 1 FROM cloud.execution_claims c WHERE c."ReservationId" = r."Id")
            ORDER BY r."BucketId", r."Id" LIMIT {limit} FOR UPDATE OF r SKIP LOCKED
            """).AsNoTracking().ToArrayAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var reservation in reservations)
        {
            var finalized = await db.QuotaReservations.Where(x => x.Id == reservation.Id && x.FinalizedAt == null)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Actual, 0L).SetProperty(y => y.FinalizedAt, now), cancellationToken);
            var updated = await db.QuotaBuckets.Where(x => x.Id == reservation.BucketId && x.TenantId == reservation.TenantId &&
                    x.ApplicationId == reservation.ApplicationId && x.Reserved >= reservation.Amount)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Reserved, y => y.Reserved - reservation.Amount), cancellationToken);
            if (finalized != 1 || updated != 1) throw new InvalidOperationException("Quota recovery could not preserve the reservation balance.");
        }
        await transaction.CommitAsync(cancellationToken);
        return reservations.Length;
    }
}
