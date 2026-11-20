using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudExecutionClaimStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudExecutionClaimStore
{
    public async Task<CloudExecutionClaimResult> ClaimAsync(Guid credentialId, Guid tenantId, Guid applicationId, Guid reservationId, string capabilityId, string fingerprintKeyId, byte[] fingerprint, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Revocation and rotation update this row; keep a share lock through claim commit.
        var credential = await db.Credentials.FromSqlInterpolated($"SELECT * FROM cloud.credentials WHERE \"Id\" = {credentialId} AND \"TenantId\" = {tenantId} AND \"ApplicationId\" = {applicationId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var credentialNow = timeProvider.GetUtcNow();
        if (credential is null || credential.RevokedAt is not null || credential.ExpiresAt is null || credential.ExpiresAt <= credentialNow ||
            !(JsonSerializer.Deserialize<string[]>(credential.Scopes) ?? []).Contains(capabilityId, StringComparer.Ordinal) ||
            !await db.Applications.AnyAsync(x => x.TenantId == tenantId && x.Id == applicationId && x.Environment == credential.Environment, cancellationToken))
            return new("unavailable");
        // Lock the reservation against both finalization and competing claims until the claim commits.
        var reservation = await db.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {reservationId} AND \"TenantId\" = {tenantId} AND \"ApplicationId\" = {applicationId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (credential.ExpiresAt <= now || reservation is null || reservation.FinalizedAt is not null || reservation.ExpiresAt <= now ||
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
