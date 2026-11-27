using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Persistence.EFCore.Messaging;

public sealed class EfDeviceEnrollmentStore<TContext>(TContext context)
    : IDeviceEnrollmentStore
    where TContext : DbContext
{
    public async ValueTask<DeviceEnrollmentRecord> CreateEnrollmentAsync(
        DeviceEnrollmentRecord enrollment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        var applicationId = MessagingPersistenceHelpers.RequireApplicationId(enrollment.ApplicationId);

        var entity = new MessagingEnrollmentEntity
        {
            TenantId = MessagingPersistenceHelpers.Scope(enrollment.TenantId),
            ApplicationId = applicationId,
            EnrollmentId = MessagingPersistenceHelpers.Scope(enrollment.EnrollmentId),
            InitiatingPrincipalId = MessagingPersistenceHelpers.Scope(enrollment.InitiatingPrincipalId),
            Issuer = enrollment.Issuer,
            Subject = enrollment.Subject,
            TokenHashSha256 = enrollment.TokenHashSha256,
            VerificationCode = enrollment.VerificationCode,
            State = enrollment.State.ToString(),
            CreatedAtUtc = MessagingPersistenceHelpers.Utc(enrollment.CreatedAt),
            ExpiresAtUtc = MessagingPersistenceHelpers.Utc(enrollment.ExpiresAt),
            ClaimedDeviceId = enrollment.ClaimedDeviceId.HasValue ? MessagingPersistenceHelpers.Scope(enrollment.ClaimedDeviceId.Value) : null,
            ClaimedDevicePublicKeyBase64 = enrollment.ClaimedDevicePublicKeyBase64,
            ClaimedDeviceName = enrollment.ClaimedDeviceName,
            ClaimedPlatform = enrollment.ClaimedPlatform?.ToString(),
            ChallengeNonceBase64 = enrollment.ChallengeNonceBase64,
            ClaimedAtUtc = MessagingPersistenceHelpers.Utc(enrollment.ClaimedAt),
            ApprovedAtUtc = MessagingPersistenceHelpers.Utc(enrollment.ApprovedAt),
            ConsumedAtUtc = MessagingPersistenceHelpers.Utc(enrollment.ConsumedAt),
            Version = 1
        };

        context.Add(entity);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            context.ChangeTracker.Clear();
            throw new MessagingDomainException("conflict", "Enrollment already exists or token hash collided.", 409, ex);
        }

        context.ChangeTracker.Clear();
        return ToEnrollmentRecord(entity);
    }

    public async ValueTask<DeviceEnrollmentRecord?> GetEnrollmentAsync(
        TenantId tenantId,
        EnrollmentId enrollmentId,
        CancellationToken cancellationToken = default)
    {
        var tenant = MessagingPersistenceHelpers.Scope(tenantId);
        var id = MessagingPersistenceHelpers.Scope(enrollmentId);

        var entity = await context.Set<MessagingEnrollmentEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.TenantId == tenant && e.EnrollmentId == id, cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ToEnrollmentRecord(entity);
    }

    public async ValueTask<DeviceEnrollmentRecord?> GetEnrollmentByTokenHashAsync(
        string tokenHashSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHashSha256);

        var entity = await context.Set<MessagingEnrollmentEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.TokenHashSha256 == tokenHashSha256, cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ToEnrollmentRecord(entity);
    }

    public async ValueTask<bool> TryUpdateEnrollmentAsync(
        DeviceEnrollmentRecord updated,
        EnrollmentState expectedState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updated);
        var tenant = MessagingPersistenceHelpers.Scope(updated.TenantId);
        var id = MessagingPersistenceHelpers.Scope(updated.EnrollmentId);
        var expected = expectedState.ToString();
        var nextState = updated.State.ToString();
        var claimedDeviceId = updated.ClaimedDeviceId.HasValue ? MessagingPersistenceHelpers.Scope(updated.ClaimedDeviceId.Value) : null;
        var claimedPlatform = updated.ClaimedPlatform?.ToString();
        var claimedAt = MessagingPersistenceHelpers.Utc(updated.ClaimedAt);
        var approvedAt = MessagingPersistenceHelpers.Utc(updated.ApprovedAt);
        var consumedAt = MessagingPersistenceHelpers.Utc(updated.ConsumedAt);

        var affected = await context.Set<MessagingEnrollmentEntity>()
            .Where(e => e.TenantId == tenant && e.EnrollmentId == id && e.State == expected)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.State, nextState)
                .SetProperty(e => e.ClaimedDeviceId, claimedDeviceId)
                .SetProperty(e => e.ClaimedDevicePublicKeyBase64, updated.ClaimedDevicePublicKeyBase64)
                .SetProperty(e => e.ClaimedDeviceName, updated.ClaimedDeviceName)
                .SetProperty(e => e.ClaimedPlatform, claimedPlatform)
                .SetProperty(e => e.ChallengeNonceBase64, updated.ChallengeNonceBase64)
                .SetProperty(e => e.ClaimedAtUtc, claimedAt)
                .SetProperty(e => e.ApprovedAtUtc, approvedAt)
                .SetProperty(e => e.ConsumedAtUtc, consumedAt)
                .SetProperty(e => e.Version, e => e.Version + 1),
                cancellationToken)
            .ConfigureAwait(false);

        return affected == 1;
    }

    public async ValueTask<DeviceRecord> UpsertDeviceAsync(
        DeviceRecord device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var applicationId = MessagingPersistenceHelpers.RequireApplicationId(device.ApplicationId);
        var tenant = MessagingPersistenceHelpers.Scope(device.TenantId);
        var deviceId = MessagingPersistenceHelpers.Scope(device.DeviceId);
        var principalId = MessagingPersistenceHelpers.Scope(device.PrincipalId);

        var existing = await context.Set<MessagingDeviceEntity>()
            .SingleOrDefaultAsync(
                d => d.TenantId == tenant && d.ApplicationId == applicationId && d.DeviceId == deviceId,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            existing = new MessagingDeviceEntity
            {
                TenantId = tenant,
                ApplicationId = applicationId,
                DeviceId = deviceId,
                PrincipalId = principalId,
                Platform = device.Platform.ToString(),
                DisplayName = device.DisplayName,
                PublicKeyBase64 = device.PublicKeyBase64,
                KeyFingerprint = device.KeyFingerprint,
                Status = device.Status.ToString(),
                CreatedAtUtc = MessagingPersistenceHelpers.Utc(device.CreatedAt),
                LastSeenAtUtc = MessagingPersistenceHelpers.Utc(device.LastSeenAt),
                RevokedAtUtc = MessagingPersistenceHelpers.Utc(device.RevokedAt)
            };
            context.Add(existing);
        }
        else
        {
            existing.Platform = device.Platform.ToString();
            existing.DisplayName = device.DisplayName;
            existing.PublicKeyBase64 = device.PublicKeyBase64;
            existing.KeyFingerprint = device.KeyFingerprint;
            existing.Status = device.Status.ToString();
            existing.LastSeenAtUtc = MessagingPersistenceHelpers.Utc(device.LastSeenAt);
            existing.RevokedAtUtc = MessagingPersistenceHelpers.Utc(device.RevokedAt);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var result = ToDeviceRecord(existing);
        context.ChangeTracker.Clear();
        return result;
    }

    public async ValueTask<DeviceRecord?> GetDeviceAsync(
        TenantId tenantId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var tenant = MessagingPersistenceHelpers.Scope(tenantId);
        var id = MessagingPersistenceHelpers.Scope(deviceId);

        var entity = await context.Set<MessagingDeviceEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.TenantId == tenant && d.DeviceId == id, cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ToDeviceRecord(entity);
    }

    public async ValueTask<IReadOnlyList<DeviceRecord>> ListDevicesAsync(
        TenantId tenantId,
        PrincipalId principalId,
        CancellationToken cancellationToken = default)
    {
        var tenant = MessagingPersistenceHelpers.Scope(tenantId);
        var principal = MessagingPersistenceHelpers.Scope(principalId);

        var rows = await context.Set<MessagingDeviceEntity>()
            .AsNoTracking()
            .Where(d => d.TenantId == tenant && d.PrincipalId == principal)
            .OrderBy(d => d.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToDeviceRecord).ToArray();
    }

    public async ValueTask<RefreshGrantRecord> CreateRefreshGrantAsync(
        RefreshGrantRecord grant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var applicationId = MessagingPersistenceHelpers.RequireApplicationId(grant.ApplicationId);

        var entity = new MessagingRefreshGrantEntity
        {
            TenantId = MessagingPersistenceHelpers.Scope(grant.TenantId),
            ApplicationId = applicationId,
            GrantId = MessagingPersistenceHelpers.Scope(grant.GrantId),
            FamilyId = grant.FamilyId,
            DeviceId = MessagingPersistenceHelpers.Scope(grant.DeviceId),
            PrincipalId = MessagingPersistenceHelpers.Scope(grant.PrincipalId),
            Issuer = grant.Issuer,
            Subject = grant.Subject,
            Audience = grant.Audience,
            TokenHashSha256 = grant.TokenHashSha256,
            Status = grant.Status.ToString(),
            IssuedAtUtc = MessagingPersistenceHelpers.Utc(grant.IssuedAt),
            ExpiresAtUtc = MessagingPersistenceHelpers.Utc(grant.ExpiresAt),
            RotatedAtUtc = MessagingPersistenceHelpers.Utc(grant.RotatedAt),
            RevokedAtUtc = MessagingPersistenceHelpers.Utc(grant.RevokedAt),
            Version = 1
        };

        context.Add(entity);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            context.ChangeTracker.Clear();
            throw new MessagingDomainException("conflict", "Refresh grant already exists or token hash collided.", 409, ex);
        }

        context.ChangeTracker.Clear();
        return ToRefreshGrantRecord(entity);
    }

    public async ValueTask<RefreshGrantRecord?> GetRefreshGrantByTokenHashAsync(
        string tokenHashSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHashSha256);

        var entity = await context.Set<MessagingRefreshGrantEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(g => g.TokenHashSha256 == tokenHashSha256, cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ToRefreshGrantRecord(entity);
    }

    public async ValueTask<bool> TryUpdateRefreshGrantAsync(
        RefreshGrantRecord updated,
        RefreshGrantStatus expectedStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updated);
        var tenant = MessagingPersistenceHelpers.Scope(updated.TenantId);
        var grantId = MessagingPersistenceHelpers.Scope(updated.GrantId);
        var expected = expectedStatus.ToString();
        var nextStatus = updated.Status.ToString();
        var rotatedAt = MessagingPersistenceHelpers.Utc(updated.RotatedAt);
        var revokedAt = MessagingPersistenceHelpers.Utc(updated.RevokedAt);

        var affected = await context.Set<MessagingRefreshGrantEntity>()
            .Where(g => g.TenantId == tenant && g.GrantId == grantId && g.Status == expected)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(g => g.Status, nextStatus)
                .SetProperty(g => g.RotatedAtUtc, rotatedAt)
                .SetProperty(g => g.RevokedAtUtc, revokedAt)
                .SetProperty(g => g.Version, g => g.Version + 1),
                cancellationToken)
            .ConfigureAwait(false);

        return affected == 1;
    }

    public async ValueTask RevokeRefreshFamilyAsync(
        TenantId tenantId,
        string familyId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyId);
        var tenant = MessagingPersistenceHelpers.Scope(tenantId);
        var revokedAtUtc = MessagingPersistenceHelpers.Utc(revokedAt);

        await context.Set<MessagingRefreshGrantEntity>()
            .Where(g => g.TenantId == tenant && g.FamilyId == familyId && g.Status != nameof(RefreshGrantStatus.Revoked))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(g => g.Status, nameof(RefreshGrantStatus.Revoked))
                .SetProperty(g => g.RevokedAtUtc, revokedAtUtc)
                .SetProperty(g => g.Version, g => g.Version + 1),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask RevokeAllDeviceRefreshGrantsAsync(
        TenantId tenantId,
        DeviceId deviceId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default)
    {
        var tenant = MessagingPersistenceHelpers.Scope(tenantId);
        var device = MessagingPersistenceHelpers.Scope(deviceId);
        var revokedAtUtc = MessagingPersistenceHelpers.Utc(revokedAt);

        await context.Set<MessagingRefreshGrantEntity>()
            .Where(g => g.TenantId == tenant && g.DeviceId == device && g.Status != nameof(RefreshGrantStatus.Revoked))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(g => g.Status, nameof(RefreshGrantStatus.Revoked))
                .SetProperty(g => g.RevokedAtUtc, revokedAtUtc)
                .SetProperty(g => g.Version, g => g.Version + 1),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask RevokeDevicePushRegistrationsAsync(
        TenantId tenantId,
        DeviceId deviceId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default)
    {
        var tenant = MessagingPersistenceHelpers.Scope(tenantId);
        var device = MessagingPersistenceHelpers.Scope(deviceId);
        var revokedAtUtc = MessagingPersistenceHelpers.Utc(revokedAt);

        await context.Set<MessagingPushRegistrationEntity>()
            .Where(p => p.TenantId == tenant && p.DeviceId == device && p.Status != nameof(PushRegistrationStatus.Revoked))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.Status, nameof(PushRegistrationStatus.Revoked))
                .SetProperty(p => p.ConsentGranted, false)
                .SetProperty(p => p.UpdatedAtUtc, revokedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Publishes a single-use device public key package scoped by tenant and application.</summary>
    public async ValueTask<DeviceKeyPackage> PublishKeyPackageAsync(
        DeviceKeyPackage keyPackage,
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyPackage);
        var appId = MessagingPersistenceHelpers.RequireApplicationId(applicationId);

        var entity = new MessagingKeyPackageEntity
        {
            TenantId = MessagingPersistenceHelpers.Scope(keyPackage.TenantId),
            ApplicationId = appId,
            KeyPackageId = MessagingPersistenceHelpers.Scope(keyPackage.KeyPackageId),
            OwnerPrincipalId = keyPackage.OwnerPrincipalId.HasValue ? MessagingPersistenceHelpers.Scope(keyPackage.OwnerPrincipalId.Value) : null,
            OwnerAgentId = keyPackage.OwnerAgentId.HasValue ? MessagingPersistenceHelpers.Scope(keyPackage.OwnerAgentId.Value) : null,
            DeviceId = MessagingPersistenceHelpers.Scope(keyPackage.DeviceId),
            Protocol = keyPackage.Protocol,
            PublicPackageBase64 = keyPackage.PublicPackageBase64,
            CreatedAtUtc = MessagingPersistenceHelpers.Utc(keyPackage.CreatedAt),
            ExpiresAtUtc = MessagingPersistenceHelpers.Utc(keyPackage.ExpiresAt),
            Consumed = false,
            ConsumedAtUtc = null
        };

        context.Add(entity);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.ChangeTracker.Clear();
        return ToKeyPackageRecord(entity);
    }

    /// <summary>Atomically allocates and consumes a single unexpired key package for a device (consume-once CAS).</summary>
    public async ValueTask<DeviceKeyPackage?> TryConsumeKeyPackageAsync(
        TenantId tenantId,
        string applicationId,
        DeviceId deviceId,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        var appId = MessagingPersistenceHelpers.RequireApplicationId(applicationId);
        var tenant = MessagingPersistenceHelpers.Scope(tenantId);
        var device = MessagingPersistenceHelpers.Scope(deviceId);
        var nowUtc = MessagingPersistenceHelpers.Utc(utcNow);

        var candidates = await context.Set<MessagingKeyPackageEntity>()
            .AsNoTracking()
            .Where(k => k.TenantId == tenant
                && k.ApplicationId == appId
                && k.DeviceId == device
                && !k.Consumed
                && k.ExpiresAtUtc > nowUtc)
            .OrderBy(k => k.CreatedAtUtc)
            .ThenBy(k => k.KeyPackageId)
            .Take(8)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            var affected = await context.Set<MessagingKeyPackageEntity>()
                .Where(k => k.TenantId == tenant
                    && k.ApplicationId == appId
                    && k.KeyPackageId == candidate.KeyPackageId
                    && !k.Consumed)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(k => k.Consumed, true)
                    .SetProperty(k => k.ConsumedAtUtc, nowUtc),
                    cancellationToken)
                .ConfigureAwait(false);

            if (affected == 1)
            {
                candidate.Consumed = true;
                candidate.ConsumedAtUtc = nowUtc;
                return ToKeyPackageRecord(candidate);
            }
        }

        return null;
    }

    /// <summary>Registers or updates a device push registration scoped by tenant and application.</summary>
    public async ValueTask<PushRegistrationRecord> UpsertPushRegistrationAsync(
        PushRegistrationRecord registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var appId = MessagingPersistenceHelpers.RequireApplicationId(registration.ApplicationId);
        var tenant = MessagingPersistenceHelpers.Scope(registration.TenantId);
        var regId = MessagingPersistenceHelpers.Scope(registration.RegistrationId);

        var existing = await context.Set<MessagingPushRegistrationEntity>()
            .SingleOrDefaultAsync(
                p => p.TenantId == tenant && p.ApplicationId == appId && p.RegistrationId == regId,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            existing = new MessagingPushRegistrationEntity
            {
                TenantId = tenant,
                ApplicationId = appId,
                RegistrationId = regId,
                PrincipalId = MessagingPersistenceHelpers.Scope(registration.PrincipalId),
                DeviceId = MessagingPersistenceHelpers.Scope(registration.DeviceId),
                Provider = registration.Provider.ToString(),
                ProtectedTokenHandle = registration.ProtectedTokenHandle,
                ConsentGranted = registration.ConsentGranted,
                Status = registration.Status.ToString(),
                UpdatedAtUtc = MessagingPersistenceHelpers.Utc(registration.UpdatedAt)
            };
            context.Add(existing);
        }
        else
        {
            existing.ProtectedTokenHandle = registration.ProtectedTokenHandle;
            existing.ConsentGranted = registration.ConsentGranted;
            existing.Status = registration.Status.ToString();
            existing.UpdatedAtUtc = MessagingPersistenceHelpers.Utc(registration.UpdatedAt);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var result = ToPushRegistrationRecord(existing);
        context.ChangeTracker.Clear();
        return result;
    }

    private static DeviceEnrollmentRecord ToEnrollmentRecord(MessagingEnrollmentEntity e) =>
        new(
            new TenantId(Ulid.Parse(e.TenantId)),
            e.ApplicationId,
            new EnrollmentId(Ulid.Parse(e.EnrollmentId)),
            new PrincipalId(Ulid.Parse(e.InitiatingPrincipalId)),
            e.Issuer,
            e.Subject,
            e.TokenHashSha256,
            e.VerificationCode,
            Enum.Parse<EnrollmentState>(e.State),
            MessagingPersistenceHelpers.FromUtc(e.CreatedAtUtc),
            MessagingPersistenceHelpers.FromUtc(e.ExpiresAtUtc),
            e.ClaimedDeviceId is null ? null : new DeviceId(Ulid.Parse(e.ClaimedDeviceId)),
            e.ClaimedDevicePublicKeyBase64,
            e.ClaimedDeviceName,
            e.ClaimedPlatform is null ? null : Enum.Parse<DevicePlatform>(e.ClaimedPlatform),
            e.ChallengeNonceBase64,
            MessagingPersistenceHelpers.FromUtc(e.ClaimedAtUtc),
            MessagingPersistenceHelpers.FromUtc(e.ApprovedAtUtc),
            MessagingPersistenceHelpers.FromUtc(e.ConsumedAtUtc));

    private static DeviceRecord ToDeviceRecord(MessagingDeviceEntity d) =>
        new(
            new TenantId(Ulid.Parse(d.TenantId)),
            d.ApplicationId,
            new PrincipalId(Ulid.Parse(d.PrincipalId)),
            new DeviceId(Ulid.Parse(d.DeviceId)),
            Enum.Parse<DevicePlatform>(d.Platform),
            d.DisplayName,
            d.PublicKeyBase64,
            d.KeyFingerprint,
            Enum.Parse<DeviceStatus>(d.Status),
            MessagingPersistenceHelpers.FromUtc(d.CreatedAtUtc),
            MessagingPersistenceHelpers.FromUtc(d.LastSeenAtUtc),
            MessagingPersistenceHelpers.FromUtc(d.RevokedAtUtc));

    private static RefreshGrantRecord ToRefreshGrantRecord(MessagingRefreshGrantEntity g) =>
        new(
            new TenantId(Ulid.Parse(g.TenantId)),
            g.ApplicationId,
            new RefreshGrantId(Ulid.Parse(g.GrantId)),
            g.FamilyId,
            new DeviceId(Ulid.Parse(g.DeviceId)),
            new PrincipalId(Ulid.Parse(g.PrincipalId)),
            g.Issuer,
            g.Subject,
            g.Audience,
            g.TokenHashSha256,
            Enum.Parse<RefreshGrantStatus>(g.Status),
            MessagingPersistenceHelpers.FromUtc(g.IssuedAtUtc),
            MessagingPersistenceHelpers.FromUtc(g.ExpiresAtUtc),
            MessagingPersistenceHelpers.FromUtc(g.RotatedAtUtc),
            MessagingPersistenceHelpers.FromUtc(g.RevokedAtUtc));

    private static DeviceKeyPackage ToKeyPackageRecord(MessagingKeyPackageEntity k) =>
        new(
            new TenantId(Ulid.Parse(k.TenantId)),
            new KeyPackageId(Ulid.Parse(k.KeyPackageId)),
            k.OwnerPrincipalId is null ? null : new PrincipalId(Ulid.Parse(k.OwnerPrincipalId)),
            k.OwnerAgentId is null ? null : new AgentId(k.OwnerAgentId),
            new DeviceId(Ulid.Parse(k.DeviceId)),
            k.Protocol,
            k.PublicPackageBase64,
            MessagingPersistenceHelpers.FromUtc(k.CreatedAtUtc),
            MessagingPersistenceHelpers.FromUtc(k.ExpiresAtUtc),
            k.Consumed,
            MessagingPersistenceHelpers.FromUtc(k.ConsumedAtUtc));

    private static PushRegistrationRecord ToPushRegistrationRecord(MessagingPushRegistrationEntity p) =>
        new(
            new TenantId(Ulid.Parse(p.TenantId)),
            p.ApplicationId,
            new PushRegistrationId(Ulid.Parse(p.RegistrationId)),
            new PrincipalId(Ulid.Parse(p.PrincipalId)),
            new DeviceId(Ulid.Parse(p.DeviceId)),
            Enum.Parse<PushProviderKind>(p.Provider),
            p.ProtectedTokenHandle,
            p.ConsentGranted,
            Enum.Parse<PushRegistrationStatus>(p.Status),
            MessagingPersistenceHelpers.FromUtc(p.UpdatedAtUtc));
}
