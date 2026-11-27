using System.Security.Cryptography;
using System.Text;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Core.Messaging;

/// <summary>
/// Coordinates desktop QR device pairing, single-winner claim, explicit desktop principal confirmation,
/// one-time completion, refresh token family rotation with reuse detection, and atomic device revocation.
/// </summary>
public sealed class DeviceEnrollmentCoordinator : IDeviceEnrollmentCoordinator
{
    public static readonly TimeSpan DefaultEnrollmentExpiry = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan MaxEnrollmentExpiry = TimeSpan.FromMinutes(10);
    private const string VerificationCodeAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    private readonly IDeviceEnrollmentStore _store;
    private readonly IDeviceEnrollmentAuthorizer _authorizer;
    private readonly IMobileTokenIssuer _tokenIssuer;
    private readonly IMessagingCryptoProvider _cryptoProvider;
    private readonly TimeProvider _timeProvider;

    public DeviceEnrollmentCoordinator(
        IDeviceEnrollmentStore store,
        IDeviceEnrollmentAuthorizer authorizer,
        IMobileTokenIssuer tokenIssuer,
        IMessagingCryptoProvider cryptoProvider,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _tokenIssuer = tokenIssuer ?? throw new ArgumentNullException(nameof(tokenIssuer));
        _cryptoProvider = cryptoProvider ?? throw new ArgumentNullException(nameof(cryptoProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<DeviceEnrollmentCreationResult> InitiateEnrollmentAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId initiatingPrincipalId,
        string issuer,
        string subject,
        Uri baseGatewayUrl,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default)
    {
        if (tenantId.Value == default || initiatingPrincipalId.Value == default ||
            string.IsNullOrWhiteSpace(applicationId) || string.IsNullOrWhiteSpace(issuer) ||
            string.IsNullOrWhiteSpace(subject))
        {
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Valid tenant, application, issuer, subject, and principal are required.", 400);
        }

        ValidateGatewayUrl(baseGatewayUrl);

        var ttl = expiry ?? DefaultEnrollmentExpiry;
        if (ttl <= TimeSpan.Zero || ttl > MaxEnrollmentExpiry)
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Enrollment expiry must be between 1 second and 10 minutes.", 400);

        var auth = await _authorizer.AuthorizeInitiateAsync(tenantId, applicationId, initiatingPrincipalId, cancellationToken).ConfigureAwait(false);
        if (!auth.Allowed)
            throw new MessagingDomainException(auth.ErrorCode ?? ErrorCodes.Unauthorized, auth.Reason ?? "Device enrollment initiation denied.", 403);

        var tokenBytes = RandomNumberGenerator.GetBytes(32); // 256-bit CSPRNG token
        var plaintextToken = ToBase64Url(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);

        var tokenHash = ComputeTokenHashSha256(plaintextToken);
        var verificationCode = GenerateVerificationCode();
        var now = _timeProvider.GetUtcNow();
        var expiresAt = now.Add(ttl);
        var enrollmentId = new EnrollmentId(Ulid.NewUlid());

        var record = new DeviceEnrollmentRecord(
            tenantId,
            applicationId,
            enrollmentId,
            initiatingPrincipalId,
            issuer,
            subject,
            tokenHash,
            verificationCode,
            EnrollmentState.Pending,
            now,
            expiresAt);

        var persisted = await _store.CreateEnrollmentAsync(record, cancellationToken).ConfigureAwait(false);
        var qrPayload = new DeviceEnrollmentQrPayload(
            Version: 1,
            Type: "openconnect.device.enrollment",
            BaseGatewayUrl: baseGatewayUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/",
            TenantId: tenantId.Value.ToString(),
            ApplicationId: applicationId,
            PairingId: enrollmentId.Value.ToString(),
            EnrollmentToken: plaintextToken,
            ExpiresAt: expiresAt);

        return new DeviceEnrollmentCreationResult(persisted, plaintextToken, qrPayload);
    }

    public async ValueTask<DeviceEnrollmentRecord> ClaimEnrollmentAsync(
        string plaintextEnrollmentToken,
        DeviceId deviceId,
        string devicePublicKeyBase64,
        string deviceDisplayName,
        DevicePlatform platform,
        ReadOnlyMemory<byte> challengeProofSignature,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(plaintextEnrollmentToken) || deviceId.Value == default ||
            string.IsNullOrWhiteSpace(devicePublicKeyBase64) || string.IsNullOrWhiteSpace(deviceDisplayName) ||
            !Enum.IsDefined(platform) || challengeProofSignature.IsEmpty)
        {
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Invalid device claim request.", 400);
        }

        var tokenHash = ComputeTokenHashSha256(plaintextEnrollmentToken);
        var existing = await _store.GetEnrollmentByTokenHashAsync(tokenHash, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            throw new MessagingDomainException(ErrorCodes.EnrollmentNotFound, "Enrollment pairing was not found.", 404);

        var now = _timeProvider.GetUtcNow();
        if (now >= existing.ExpiresAt)
        {
            if (existing.State is EnrollmentState.Pending or EnrollmentState.Claimed or EnrollmentState.Approved)
                await _store.TryUpdateEnrollmentAsync(existing with { State = EnrollmentState.Expired }, existing.State, cancellationToken).ConfigureAwait(false);
            throw new MessagingDomainException(ErrorCodes.EnrollmentExpired, "Enrollment pairing has expired.", 410);
        }

        if (existing.State != EnrollmentState.Pending)
            throw new MessagingDomainException(ErrorCodes.EnrollmentInvalidState, "Enrollment pairing is no longer claimable.", 409);

        var claimChallenge = BuildClaimChallengeBytes(existing.EnrollmentId, existing.TokenHashSha256, deviceId);
        var validProof = await _cryptoProvider.VerifyDeviceProofOfPossessionAsync(
            devicePublicKeyBase64,
            claimChallenge,
            challengeProofSignature,
            cancellationToken).ConfigureAwait(false);
        if (!validProof)
            throw new MessagingDomainException(ErrorCodes.CryptoVerificationFailed, "Device proof-of-possession signature is invalid.", 401);

        var completionNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var claimed = existing with
        {
            State = EnrollmentState.Claimed,
            ClaimedDeviceId = deviceId,
            ClaimedDevicePublicKeyBase64 = devicePublicKeyBase64,
            ClaimedDeviceName = deviceDisplayName.Trim(),
            ClaimedPlatform = platform,
            ChallengeNonceBase64 = completionNonce,
            ClaimedAt = now
        };

        var wonCas = await _store.TryUpdateEnrollmentAsync(claimed, EnrollmentState.Pending, cancellationToken).ConfigureAwait(false);
        if (!wonCas)
            throw new MessagingDomainException(ErrorCodes.Conflict, "Enrollment pairing was already claimed by another device.", 409);

        return claimed;
    }

    public async ValueTask<DeviceEnrollmentRecord> ApproveEnrollmentAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId approvingPrincipalId,
        EnrollmentId enrollmentId,
        CancellationToken cancellationToken = default)
    {
        var existing = await _store.GetEnrollmentAsync(tenantId, enrollmentId, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.TenantId != tenantId ||
            !string.Equals(existing.ApplicationId, applicationId, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(ErrorCodes.EnrollmentNotFound, "Enrollment pairing was not found.", 404);
        }

        if (existing.InitiatingPrincipalId != approvingPrincipalId)
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Only the initiating desktop principal may approve this enrollment.", 403);

        var auth = await _authorizer.AuthorizeConfirmAsync(tenantId, applicationId, approvingPrincipalId, enrollmentId, cancellationToken).ConfigureAwait(false);
        if (!auth.Allowed)
            throw new MessagingDomainException(auth.ErrorCode ?? ErrorCodes.Unauthorized, auth.Reason ?? "Enrollment confirmation denied.", 403);

        var now = _timeProvider.GetUtcNow();
        if (now >= existing.ExpiresAt)
        {
            if (existing.State is EnrollmentState.Pending or EnrollmentState.Claimed)
                await _store.TryUpdateEnrollmentAsync(existing with { State = EnrollmentState.Expired }, existing.State, cancellationToken).ConfigureAwait(false);
            throw new MessagingDomainException(ErrorCodes.EnrollmentExpired, "Enrollment pairing has expired.", 410);
        }

        if (existing.State != EnrollmentState.Claimed)
            throw new MessagingDomainException(ErrorCodes.EnrollmentInvalidState, "Enrollment pairing must be claimed before approval.", 409);

        var approved = existing with
        {
            State = EnrollmentState.Approved,
            ApprovedAt = now
        };

        var wonCas = await _store.TryUpdateEnrollmentAsync(approved, EnrollmentState.Claimed, cancellationToken).ConfigureAwait(false);
        if (!wonCas)
            throw new MessagingDomainException(ErrorCodes.Conflict, "Enrollment state changed concurrently.", 409);

        return approved;
    }

    public async ValueTask<DeviceEnrollmentRecord> DenyOrCancelEnrollmentAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        EnrollmentId enrollmentId,
        bool isDenial,
        CancellationToken cancellationToken = default)
    {
        var existing = await _store.GetEnrollmentAsync(tenantId, enrollmentId, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.TenantId != tenantId ||
            !string.Equals(existing.ApplicationId, applicationId, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(ErrorCodes.EnrollmentNotFound, "Enrollment pairing was not found.", 404);
        }

        if (existing.InitiatingPrincipalId != principalId)
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Only the initiating principal may cancel or deny this enrollment.", 403);

        if (existing.State is EnrollmentState.Consumed or EnrollmentState.Expired or EnrollmentState.Cancelled or EnrollmentState.Denied)
            throw new MessagingDomainException(ErrorCodes.EnrollmentInvalidState, "Enrollment pairing is already in a terminal state.", 409);

        var targetState = isDenial ? EnrollmentState.Denied : EnrollmentState.Cancelled;
        var updated = existing with { State = targetState };
        var wonCas = await _store.TryUpdateEnrollmentAsync(updated, existing.State, cancellationToken).ConfigureAwait(false);
        if (!wonCas)
            throw new MessagingDomainException(ErrorCodes.Conflict, "Enrollment state changed concurrently.", 409);

        return updated;
    }

    public async ValueTask<MobileTokenBundle> CompleteEnrollmentAsync(
        TenantId tenantId,
        EnrollmentId enrollmentId,
        DeviceId deviceId,
        ReadOnlyMemory<byte> completionProofSignature,
        string audience,
        CancellationToken cancellationToken = default)
    {
        if (deviceId.Value == default || completionProofSignature.IsEmpty || string.IsNullOrWhiteSpace(audience))
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Invalid completion request.", 400);

        var existing = await _store.GetEnrollmentAsync(tenantId, enrollmentId, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.TenantId != tenantId)
            throw new MessagingDomainException(ErrorCodes.EnrollmentNotFound, "Enrollment pairing was not found.", 404);

        var now = _timeProvider.GetUtcNow();
        if (now >= existing.ExpiresAt)
        {
            if (existing.State is EnrollmentState.Pending or EnrollmentState.Claimed or EnrollmentState.Approved)
                await _store.TryUpdateEnrollmentAsync(existing with { State = EnrollmentState.Expired }, existing.State, cancellationToken).ConfigureAwait(false);
            throw new MessagingDomainException(ErrorCodes.EnrollmentExpired, "Enrollment pairing has expired.", 410);
        }

        if (existing.State != EnrollmentState.Approved)
            throw new MessagingDomainException(ErrorCodes.EnrollmentInvalidState, "Enrollment pairing is not approved or was already consumed.", 409);

        if (existing.ClaimedDeviceId != deviceId ||
            string.IsNullOrWhiteSpace(existing.ClaimedDevicePublicKeyBase64) ||
            string.IsNullOrWhiteSpace(existing.ChallengeNonceBase64))
        {
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Device identity does not match the claimed pairing.", 403);
        }

        var challengeBytes = Convert.FromBase64String(existing.ChallengeNonceBase64);
        var validProof = await _cryptoProvider.VerifyDeviceProofOfPossessionAsync(
            existing.ClaimedDevicePublicKeyBase64,
            challengeBytes,
            completionProofSignature,
            cancellationToken).ConfigureAwait(false);
        if (!validProof)
            throw new MessagingDomainException(ErrorCodes.CryptoVerificationFailed, "Device completion proof signature is invalid.", 401);

        var consumed = existing with
        {
            State = EnrollmentState.Consumed,
            ConsumedAt = now
        };

        var wonCas = await _store.TryUpdateEnrollmentAsync(consumed, EnrollmentState.Approved, cancellationToken).ConfigureAwait(false);
        if (!wonCas)
            throw new MessagingDomainException(ErrorCodes.Conflict, "Enrollment pairing was already consumed.", 409);

        var fingerprint = await _cryptoProvider.ComputeKeyFingerprintAsync(existing.ClaimedDevicePublicKeyBase64, cancellationToken).ConfigureAwait(false);
        var device = new DeviceRecord(
            existing.TenantId,
            existing.ApplicationId,
            existing.InitiatingPrincipalId,
            deviceId,
            existing.ClaimedPlatform ?? DevicePlatform.Other,
            existing.ClaimedDeviceName ?? "Mobile Device",
            existing.ClaimedDevicePublicKeyBase64,
            fingerprint,
            DeviceStatus.Verified,
            now,
            now);

        await _store.UpsertDeviceAsync(device, cancellationToken).ConfigureAwait(false);

        var tokens = await _tokenIssuer.IssueAsync(
            existing.TenantId,
            existing.ApplicationId,
            existing.InitiatingPrincipalId,
            deviceId,
            existing.Issuer,
            existing.Subject,
            audience,
            cancellationToken).ConfigureAwait(false);

        var grant = new RefreshGrantRecord(
            existing.TenantId,
            existing.ApplicationId,
            tokens.RefreshGrantId,
            tokens.FamilyId,
            deviceId,
            existing.InitiatingPrincipalId,
            existing.Issuer,
            existing.Subject,
            audience,
            ComputeTokenHashSha256(tokens.RefreshToken),
            RefreshGrantStatus.Active,
            now,
            tokens.RefreshTokenExpiresAt);

        await _store.CreateRefreshGrantAsync(grant, cancellationToken).ConfigureAwait(false);
        return tokens;
    }

    public async ValueTask<MobileTokenBundle> RotateRefreshTokenAsync(
        string plaintextRefreshToken,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(plaintextRefreshToken) || deviceId.Value == default)
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Invalid refresh token request.", 401);

        var tokenHash = ComputeTokenHashSha256(plaintextRefreshToken);
        var grant = await _store.GetRefreshGrantByTokenHashAsync(tokenHash, cancellationToken).ConfigureAwait(false);
        if (grant is null || grant.DeviceId != deviceId)
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Refresh grant was not found or device mismatch.", 401);

        var now = _timeProvider.GetUtcNow();
        if (grant.Status is RefreshGrantStatus.Rotated or RefreshGrantStatus.ReuseDetected)
        {
            await _store.RevokeRefreshFamilyAsync(grant.TenantId, grant.FamilyId, now, cancellationToken).ConfigureAwait(false);
            throw new MessagingDomainException(
                ErrorCodes.TokenReuseDetected,
                "Refresh token reuse detected; the entire token family has been revoked.",
                401);
        }

        if (grant.Status == RefreshGrantStatus.Revoked)
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Refresh grant has been revoked.", 401);

        var device = await _store.GetDeviceAsync(grant.TenantId, deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null || device.Status == DeviceStatus.Revoked)
        {
            await _store.RevokeRefreshFamilyAsync(grant.TenantId, grant.FamilyId, now, cancellationToken).ConfigureAwait(false);
            throw new MessagingDomainException(ErrorCodes.DeviceRevoked, "Device has been revoked.", 401);
        }

        if (now >= grant.ExpiresAt)
            throw new MessagingDomainException(ErrorCodes.EnrollmentExpired, "Refresh grant has expired.", 401);

        var rotated = grant with
        {
            Status = RefreshGrantStatus.Rotated,
            RotatedAt = now
        };

        var wonCas = await _store.TryUpdateRefreshGrantAsync(rotated, RefreshGrantStatus.Active, cancellationToken).ConfigureAwait(false);
        if (!wonCas)
        {
            await _store.RevokeRefreshFamilyAsync(grant.TenantId, grant.FamilyId, now, cancellationToken).ConfigureAwait(false);
            throw new MessagingDomainException(
                ErrorCodes.TokenReuseDetected,
                "Concurrent refresh token reuse detected; token family revoked.",
                401);
        }

        var nextTokens = await _tokenIssuer.RotateAsync(grant, cancellationToken).ConfigureAwait(false);
        var nextGrant = new RefreshGrantRecord(
            grant.TenantId,
            grant.ApplicationId,
            nextTokens.RefreshGrantId,
            grant.FamilyId,
            grant.DeviceId,
            grant.PrincipalId,
            grant.Issuer,
            grant.Subject,
            grant.Audience,
            ComputeTokenHashSha256(nextTokens.RefreshToken),
            RefreshGrantStatus.Active,
            now,
            nextTokens.RefreshTokenExpiresAt);

        await _store.CreateRefreshGrantAsync(nextGrant, cancellationToken).ConfigureAwait(false);
        return nextTokens;
    }

    public async ValueTask<DeviceRecord> RevokeDeviceAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var auth = await _authorizer.AuthorizeRevokeAsync(tenantId, applicationId, principalId, deviceId, cancellationToken).ConfigureAwait(false);
        if (!auth.Allowed)
            throw new MessagingDomainException(auth.ErrorCode ?? ErrorCodes.Unauthorized, auth.Reason ?? "Device revocation denied.", 403);

        var device = await _store.GetDeviceAsync(tenantId, deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null || device.TenantId != tenantId ||
            !string.Equals(device.ApplicationId, applicationId, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Device was not found.", 404);
        }

        if (device.PrincipalId != principalId)
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Cannot revoke another principal's device.", 403);

        var now = _timeProvider.GetUtcNow();
        var revoked = device with
        {
            Status = DeviceStatus.Revoked,
            RevokedAt = now
        };

        var persisted = await _store.UpsertDeviceAsync(revoked, cancellationToken).ConfigureAwait(false);
        await _store.RevokeAllDeviceRefreshGrantsAsync(tenantId, deviceId, now, cancellationToken).ConfigureAwait(false);
        await _store.RevokeDevicePushRegistrationsAsync(tenantId, deviceId, now, cancellationToken).ConfigureAwait(false);
        return persisted;
    }

    public static byte[] BuildClaimChallengeBytes(EnrollmentId enrollmentId, string tokenHashSha256, DeviceId deviceId)
    {
        var input = $"openconnect.enrollment.claim.v1|{enrollmentId.Value}|{tokenHashSha256}|{deviceId.Value}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(input));
    }

    public static string ComputeTokenHashSha256(string plaintextToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintextToken);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintextToken));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ValidateGatewayUrl(Uri baseGatewayUrl)
    {
        if (baseGatewayUrl is null || !baseGatewayUrl.IsAbsoluteUri)
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "An absolute HTTPS gateway URL is required.", 400);
        if (!string.Equals(baseGatewayUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Gateway URL must use HTTPS.", 400);
        if (!string.IsNullOrEmpty(baseGatewayUrl.UserInfo) ||
            !string.IsNullOrEmpty(baseGatewayUrl.Fragment) ||
            !string.IsNullOrEmpty(baseGatewayUrl.Query))
        {
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Gateway URL must not contain credentials, query strings, or fragments.", 400);
        }
    }

    private static string GenerateVerificationCode()
    {
        Span<byte> random = stackalloc byte[6];
        RandomNumberGenerator.Fill(random);
        Span<char> chars = stackalloc char[7];
        for (var i = 0; i < 3; i++)
            chars[i] = VerificationCodeAlphabet[random[i] % VerificationCodeAlphabet.Length];
        chars[3] = '-';
        for (var i = 0; i < 3; i++)
            chars[i + 4] = VerificationCodeAlphabet[random[i + 3] % VerificationCodeAlphabet.Length];
        return new string(chars);
    }

    private static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
