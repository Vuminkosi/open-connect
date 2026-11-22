using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public interface ICloudAnalyticsCompletionStore
{
    Task<CloudExecutionCompletionResult> CompleteAsync(Guid tenantId, Guid applicationId, Guid reservationId, Guid claimId, int computeMilliseconds, CancellationToken cancellationToken);
}

public sealed class EfCloudAnalyticsCompletionStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider)
    : ICloudAnalyticsCompletionStore
{
    public async Task<CloudExecutionCompletionResult> CompleteAsync(Guid tenantId, Guid applicationId, Guid reservationId, Guid claimId, int computeMilliseconds, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || applicationId == Guid.Empty || reservationId == Guid.Empty || claimId == Guid.Empty || computeMilliseconds is < 1 or > 120000) return new("invalid_request");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Match privacy writes and claim admission lock ordering before locking the reservation.
        var application = await db.Applications.FromSqlInterpolated($"SELECT * FROM cloud.applications WHERE \"Id\" = {applicationId} AND \"TenantId\" = {tenantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (application is null) return new("unavailable");
        var reservation = await db.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {reservationId} AND \"TenantId\" = {tenantId} AND \"ApplicationId\" = {applicationId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (reservation is null || reservation.Amount != 1 || reservation.RequestFingerprint is not { Length: 32 } ||
            !CloudRequestFingerprint.IsValidKeyId(reservation.FingerprintKeyId) ||
            !await db.ExecutionClaims.AnyAsync(x => x.ReservationId == reservationId && x.ClaimId == claimId && x.TenantId == tenantId && x.ApplicationId == applicationId && x.DeploymentId != null && x.Region != null, cancellationToken) ||
            !await db.ExecutionConsumptions.AnyAsync(x => x.ReservationId == reservationId && x.ClaimId == claimId, cancellationToken) ||
            !await db.QuotaBuckets.AnyAsync(x => x.Id == reservation.BucketId && x.TenantId == tenantId && x.ApplicationId == applicationId && x.CapabilityId == "analytics.analyze" && x.Unit == "request", cancellationToken)) return new("unavailable");
        if (!await EfCloudAnalyticsPrivacyAdmission.IsAllowedAsync(db, tenantId, applicationId, reservation.RequestId, reservation.FingerprintKeyId!, reservation.RequestFingerprint, cancellationToken) ||
            !await EfCloudAnalyticsPrivacyAdmission.IsDirectionAllowedAsync(db, tenantId, applicationId, reservation.RequestId, reservation.FingerprintKeyId!, reservation.RequestFingerprint, "response", cancellationToken)) return new("unavailable");
        if (reservation.FinalizedAt is not null)
        {
            var completed = await db.ExecutionCompletions.AsNoTracking().AnyAsync(x => x.ReservationId == reservationId && x.ClaimId == claimId && x.Outcome == "completed", cancellationToken);
            var sameUsage = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS (SELECT 1 FROM cloud.analytics_compute_usage WHERE "ReservationId" = {reservationId}
                    AND "TenantId" = {tenantId} AND "ApplicationId" = {applicationId} AND "ClaimId" = {claimId}
                    AND "ComputeMilliseconds" = {computeMilliseconds}) AS "Value"
                """).SingleAsync(cancellationToken);
            return new(completed && reservation.Actual == 1 && sameUsage ? "already_completed" : "conflict");
        }
        if (reservation.ExpiresAt <= timeProvider.GetUtcNow()) return new("unavailable");
        var now = timeProvider.GetUtcNow();
        db.ExecutionCompletions.Add(new CloudExecutionCompletion { ReservationId = reservationId, ClaimId = claimId, Outcome = "completed", CompletedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cloud.analytics_compute_usage ("ReservationId", "TenantId", "ApplicationId", "ClaimId", "ComputeMilliseconds", "RecordedAt")
            VALUES ({reservationId}, {tenantId}, {applicationId}, {claimId}, {computeMilliseconds}, {now})
            """, cancellationToken);
        var finalized = await db.QuotaReservations.Where(x => x.Id == reservationId && x.TenantId == tenantId && x.ApplicationId == applicationId && x.FinalizedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Actual, 1L).SetProperty(y => y.FinalizedAt, now), cancellationToken);
        if (finalized != 1) return new("unavailable");
        var updated = await db.QuotaBuckets.Where(x => x.Id == reservation.BucketId && x.TenantId == tenantId && x.ApplicationId == applicationId && x.Reserved >= 1)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Reserved, y => y.Reserved - 1).SetProperty(y => y.Consumed, y => y.Consumed + 1), cancellationToken);
        if (updated != 1) return new("unavailable");
        await transaction.CommitAsync(cancellationToken);
        return new("completed");
    }
}
