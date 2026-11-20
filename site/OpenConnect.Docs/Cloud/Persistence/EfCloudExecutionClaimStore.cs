using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudExecutionClaimStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudExecutionClaimStore
{
    public async Task<CloudExecutionClaimResult> ClaimAsync(Guid tenantId, Guid applicationId, Guid reservationId, string capabilityId, string fingerprintKeyId, byte[] fingerprint, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Lock the reservation against both finalization and competing claims until the claim commits.
        var reservation = await db.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {reservationId} AND \"TenantId\" = {tenantId} AND \"ApplicationId\" = {applicationId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (reservation is null || reservation.FinalizedAt is not null || reservation.ExpiresAt <= now ||
            reservation.RequestFingerprint is not { Length: 32 } || !CloudRequestFingerprint.IsValidKeyId(reservation.FingerprintKeyId) ||
            !await db.QuotaBuckets.AnyAsync(x => x.Id == reservation.BucketId && x.TenantId == tenantId && x.ApplicationId == applicationId && x.CapabilityId == capabilityId, cancellationToken))
            return new("unavailable");
        if (!string.Equals(reservation.FingerprintKeyId, fingerprintKeyId, StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(reservation.RequestFingerprint, fingerprint)) return new("idempotency_conflict");
        if (await db.ExecutionClaims.AnyAsync(x => x.ReservationId == reservationId, cancellationToken)) return new("already_claimed");
        var claim = new CloudExecutionClaim { ReservationId = reservationId, TenantId = tenantId, ApplicationId = applicationId, ClaimId = Guid.NewGuid(), ClaimedAt = now };
        db.ExecutionClaims.Add(claim);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new("claimed", claim.ClaimId);
    }
}
