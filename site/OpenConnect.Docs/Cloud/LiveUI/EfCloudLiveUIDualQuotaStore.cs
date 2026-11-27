using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace OpenConnect.Docs.Cloud;

/// <summary>Atomically reserves both LiveUI plan and token capacity in PostgreSQL.</summary>
public sealed class EfCloudLiveUIDualQuotaStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudLiveUIDualQuotaStore
{
    public async Task<CloudQuotaResult> ReserveAsync(CloudLiveUIDualQuotaRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var existing = await FindExistingAsync(db, request, cancellationToken);
        if (existing is not null)
            return existing;
        var buckets = await db.QuotaBuckets.FromSqlInterpolated($"""
            SELECT * FROM cloud.quota_buckets
            WHERE "TenantId" = {request.TenantId} AND "ApplicationId" = {request.ApplicationId}
              AND "CapabilityId" = 'inference.liveui' AND "Unit" IN ('plan', 'token')
              AND "PeriodStart" <= {now} AND "PeriodEnd" > {now}
            ORDER BY "Id" FOR UPDATE
            """).AsNoTracking().ToArrayAsync(cancellationToken);
        if (buckets.Length != 2 || buckets.Count(item => item.Unit == "plan") != 1 || buckets.Count(item => item.Unit == "token") != 1)
            return new("quota_unavailable");
        var plan = buckets.Single(item => item.Unit == "plan");
        var tokens = buckets.Single(item => item.Unit == "token");
        var current = timeProvider.GetUtcNow();
        if (request.ExpiresAt <= current || plan.PeriodStart > current || plan.PeriodEnd <= current ||
            tokens.PeriodStart > current || tokens.PeriodEnd <= current)
            return new("invalid_request");
        if (request.ExpiresAt > plan.PeriodEnd || request.ExpiresAt > tokens.PeriodEnd)
            return new("invalid_request");
        existing = await FindExistingAsync(db, request, cancellationToken);
        if (existing is not null)
            return existing;
        if (plan.Limit - plan.Consumed - plan.Reserved < 1 || tokens.Limit - tokens.Consumed - tokens.Reserved < request.TokenAmount)
            return new("quota_exceeded");
        var planUpdated = await db.QuotaBuckets.Where(item => item.Id == plan.Id && item.TenantId == request.TenantId && item.ApplicationId == request.ApplicationId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Reserved, item => item.Reserved + 1), cancellationToken);
        var tokenUpdated = await db.QuotaBuckets.Where(item => item.Id == tokens.Id && item.TenantId == request.TenantId && item.ApplicationId == request.ApplicationId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Reserved, item => item.Reserved + request.TokenAmount), cancellationToken);
        if (planUpdated != 1 || tokenUpdated != 1)
            return new("quota_unavailable");
        var reservation = new CloudQuotaReservation
        {
            Id = Guid.NewGuid(),
            BucketId = plan.Id,
            TenantId = request.TenantId,
            ApplicationId = request.ApplicationId,
            RequestId = request.RequestId,
            FingerprintKeyId = request.FingerprintKeyId,
            RequestFingerprint = request.RequestFingerprint,
            Amount = 1,
            ExpiresAt = request.ExpiresAt
        };
        db.QuotaReservations.Add(reservation);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cloud.liveui_token_reservations ("PlanReservationId", "TenantId", "ApplicationId", "TokenBucketId", "Amount")
                VALUES ({reservation.Id}, {request.TenantId}, {request.ApplicationId}, {tokens.Id}, {request.TokenAmount})
                """, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new("reserved", reservation.Id);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            return await FindExistingAsync(db, request, cancellationToken) ?? new("quota_unavailable");
        }
    }

    private static async Task<CloudQuotaResult?> FindExistingAsync(CloudControlPlaneDbContext db, CloudLiveUIDualQuotaRequest request, CancellationToken cancellationToken)
    {
        var existing = await (from reservation in db.QuotaReservations.AsNoTracking()
                              join bucket in db.QuotaBuckets.AsNoTracking() on reservation.BucketId equals bucket.Id
                              where reservation.TenantId == request.TenantId && reservation.ApplicationId == request.ApplicationId && reservation.RequestId == request.RequestId
                              select new
                              {
                                  reservation.Id,
                                  reservation.Amount,
                                  reservation.ExpiresAt,
                                  reservation.RequestFingerprint,
                                  reservation.FingerprintKeyId,
                                  bucket.CapabilityId,
                                  bucket.Unit
                              }).SingleOrDefaultAsync(cancellationToken);
        if (existing is null)
            return null;
        if (existing.CapabilityId != "inference.liveui" || existing.Unit != "plan" || existing.Amount != 1 ||
            existing.ExpiresAt != request.ExpiresAt || !string.Equals(existing.FingerprintKeyId, request.FingerprintKeyId, StringComparison.Ordinal) ||
            existing.RequestFingerprint is not { Length: 32 } ||
            !CryptographicOperations.FixedTimeEquals(existing.RequestFingerprint, request.RequestFingerprint))
            return new("idempotency_conflict", existing.Id);
        var tokenAmount = await db.Database.SqlQuery<long>($"""
            SELECT "Amount" AS "Value" FROM cloud.liveui_token_reservations
            WHERE "PlanReservationId" = {existing.Id} AND "TenantId" = {request.TenantId} AND "ApplicationId" = {request.ApplicationId}
            """).SingleOrDefaultAsync(cancellationToken);
        if (tokenAmount == 0)
            return new("quota_unavailable");
        return new(tokenAmount == request.TokenAmount ? "duplicate" : "idempotency_conflict", existing.Id);
    }
}
