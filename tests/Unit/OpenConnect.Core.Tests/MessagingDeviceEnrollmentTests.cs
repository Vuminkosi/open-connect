using System.Collections.Concurrent;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Core.Messaging;
using Xunit;

namespace OpenConnect.Core.Tests;

public sealed class MessagingDeviceEnrollmentTests
{
    private readonly TenantId _tenant = new(Ulid.NewUlid());
    private readonly PrincipalId _desktopUser = new(Ulid.NewUlid());
    private readonly PrincipalId _otherUser = new(Ulid.NewUlid());

    [Fact]
    public async Task A14_ValidQrPairingFlow_NeverStoresPlaintextToken_AndIssuesScopedCredentials()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var crypto = new MessagingCryptoEnvelopeService(clock);
        var store = new InMemoryEnrollmentStore();
        var coordinator = new DeviceEnrollmentCoordinator(store, new AllowEnrollmentAuth(), new StubTokenIssuer(clock), crypto, clock);

        var created = await coordinator.InitiateEnrollmentAsync(
            _tenant, "crm", _desktopUser, "https://idp.company.example", "emp-42",
            new Uri("https://gateway.company.example/"));

        Assert.Equal(EnrollmentState.Pending, created.Record.State);
        Assert.Equal(TimeSpan.FromSeconds(120), created.Record.ExpiresAt - created.Record.CreatedAt);
        Assert.NotEqual(created.PlaintextEnrollmentToken, created.Record.TokenHashSha256);
        Assert.DoesNotContain(created.PlaintextEnrollmentToken, created.Record.TokenHashSha256, StringComparison.Ordinal);
        Assert.Matches("^[23456789A-HJ-NP-Z]{3}-[23456789A-HJ-NP-Z]{3}$", created.Record.VerificationCode);

        var (pubKey, privKey) = MessagingCryptoEnvelopeService.GenerateDeviceKeyPair();
        var deviceId = new DeviceId(Ulid.NewUlid());
        var claimChallenge = DeviceEnrollmentCoordinator.BuildClaimChallengeBytes(created.Record.EnrollmentId, created.Record.TokenHashSha256, deviceId);
        var claimSig = MessagingCryptoEnvelopeService.SignProofOfPossession(privKey, claimChallenge);

        var claimed = await coordinator.ClaimEnrollmentAsync(
            created.PlaintextEnrollmentToken, deviceId, pubKey, "Pixel 9", DevicePlatform.Android, claimSig);
        Assert.Equal(EnrollmentState.Claimed, claimed.State);
        Assert.NotNull(claimed.ChallengeNonceBase64);

        // Cannot complete before desktop principal approval
        var unapprovedSig = MessagingCryptoEnvelopeService.SignProofOfPossession(privKey, Convert.FromBase64String(claimed.ChallengeNonceBase64!));
        var unapprovedEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.CompleteEnrollmentAsync(_tenant, claimed.EnrollmentId, deviceId, unapprovedSig, "mobile-api"));
        Assert.Equal(ErrorCodes.EnrollmentInvalidState, unapprovedEx.ErrorCode);

        // Different user cannot approve
        var wrongUserEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.ApproveEnrollmentAsync(_tenant, "crm", _otherUser, claimed.EnrollmentId));
        Assert.Equal(ErrorCodes.Unauthorized, wrongUserEx.ErrorCode);

        // Original desktop user approves
        var approved = await coordinator.ApproveEnrollmentAsync(_tenant, "crm", _desktopUser, claimed.EnrollmentId);
        Assert.Equal(EnrollmentState.Approved, approved.State);

        // Mobile completes once
        var bundle = await coordinator.CompleteEnrollmentAsync(_tenant, approved.EnrollmentId, deviceId, unapprovedSig, "mobile-api");
        Assert.Equal(deviceId, bundle.DeviceId);
        Assert.False(string.IsNullOrWhiteSpace(bundle.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(bundle.RefreshToken));

        // Replay of completion fails closed
        var replayEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.CompleteEnrollmentAsync(_tenant, approved.EnrollmentId, deviceId, unapprovedSig, "mobile-api"));
        Assert.Equal(ErrorCodes.EnrollmentInvalidState, replayEx.ErrorCode);
    }

    [Fact]
    public async Task A15_ExpiredAndConcurrentScan_AndBadGatewayUrl_RejectedFailClosed()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var crypto = new MessagingCryptoEnvelopeService(clock);
        var store = new InMemoryEnrollmentStore();
        var coordinator = new DeviceEnrollmentCoordinator(store, new AllowEnrollmentAuth(), new StubTokenIssuer(clock), crypto, clock);

        // Reject insecure HTTP or embedded credentials
        await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.InitiateEnrollmentAsync(_tenant, "crm", _desktopUser, "iss", "sub", new Uri("http://gateway.example/")));
        await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.InitiateEnrollmentAsync(_tenant, "crm", _desktopUser, "iss", "sub", new Uri("https://user:pass@gateway.example/")));

        // Expired QR rejected with 410
        var created = await coordinator.InitiateEnrollmentAsync(_tenant, "crm", _desktopUser, "iss", "sub", new Uri("https://gateway.example/"));
        clock.Advance(TimeSpan.FromSeconds(121));
        var (pub, priv) = MessagingCryptoEnvelopeService.GenerateDeviceKeyPair();
        var dev = new DeviceId(Ulid.NewUlid());
        var sig = MessagingCryptoEnvelopeService.SignProofOfPossession(priv, DeviceEnrollmentCoordinator.BuildClaimChallengeBytes(created.Record.EnrollmentId, created.Record.TokenHashSha256, dev));
        var expEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.ClaimEnrollmentAsync(created.PlaintextEnrollmentToken, dev, pub, "Phone", DevicePlatform.Android, sig));
        Assert.Equal(ErrorCodes.EnrollmentExpired, expEx.ErrorCode);
        Assert.Equal(410, expEx.StatusCode);

        // Single-winner claim: second scan fails with 409
        var fresh = await coordinator.InitiateEnrollmentAsync(_tenant, "crm", _desktopUser, "iss", "sub", new Uri("https://gateway.example/"));
        var sig1 = MessagingCryptoEnvelopeService.SignProofOfPossession(priv, DeviceEnrollmentCoordinator.BuildClaimChallengeBytes(fresh.Record.EnrollmentId, fresh.Record.TokenHashSha256, dev));
        await coordinator.ClaimEnrollmentAsync(fresh.PlaintextEnrollmentToken, dev, pub, "Phone 1", DevicePlatform.Android, sig1);

        var dev2 = new DeviceId(Ulid.NewUlid());
        var sig2 = MessagingCryptoEnvelopeService.SignProofOfPossession(priv, DeviceEnrollmentCoordinator.BuildClaimChallengeBytes(fresh.Record.EnrollmentId, fresh.Record.TokenHashSha256, dev2));
        var raceEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.ClaimEnrollmentAsync(fresh.PlaintextEnrollmentToken, dev2, pub, "Phone 2", DevicePlatform.Android, sig2));
        Assert.Equal(ErrorCodes.EnrollmentInvalidState, raceEx.ErrorCode);
        Assert.Equal(409, raceEx.StatusCode);
    }

    [Fact]
    public async Task A16_And_A17_RefreshRotation_ReuseRevokesEntireFamily_AndDeviceRevocationDeniesRefresh()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var crypto = new MessagingCryptoEnvelopeService(clock);
        var store = new InMemoryEnrollmentStore();
        var coordinator = new DeviceEnrollmentCoordinator(store, new AllowEnrollmentAuth(), new StubTokenIssuer(clock), crypto, clock);

        var deviceId = new DeviceId(Ulid.NewUlid());
        var bundle1 = await CompletePairingAsync(coordinator, deviceId);

        // Normal rotation succeeds
        var bundle2 = await coordinator.RotateRefreshTokenAsync(bundle1.RefreshToken, deviceId);
        Assert.Equal(bundle1.FamilyId, bundle2.FamilyId);
        Assert.NotEqual(bundle1.RefreshToken, bundle2.RefreshToken);

        // Reusing old refresh token triggers reuse detection and revokes ENTIRE family
        var reuseEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.RotateRefreshTokenAsync(bundle1.RefreshToken, deviceId));
        Assert.Equal(ErrorCodes.TokenReuseDetected, reuseEx.ErrorCode);
        Assert.Equal(401, reuseEx.StatusCode);

        // Even the previously valid bundle2 token is now revoked because the whole family was revoked
        var familyRevokedEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.RotateRefreshTokenAsync(bundle2.RefreshToken, deviceId));
        Assert.Equal(401, familyRevokedEx.StatusCode);

        // Fresh pairing + explicit desktop device revocation denies subsequent refresh
        var device2 = new DeviceId(Ulid.NewUlid());
        var bundle3 = await CompletePairingAsync(coordinator, device2);
        var revoked = await coordinator.RevokeDeviceAsync(_tenant, "crm", _desktopUser, device2);
        Assert.Equal(DeviceStatus.Revoked, revoked.Status);
        Assert.Contains(device2, store.PushRevokedDevices);

        var postRevokeEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.RotateRefreshTokenAsync(bundle3.RefreshToken, device2));
        Assert.Equal(401, postRevokeEx.StatusCode);
    }

    private async Task<MobileTokenBundle> CompletePairingAsync(DeviceEnrollmentCoordinator coordinator, DeviceId deviceId)
    {
        var created = await coordinator.InitiateEnrollmentAsync(_tenant, "crm", _desktopUser, "iss", "sub", new Uri("https://gateway.example/"));
        var (pub, priv) = MessagingCryptoEnvelopeService.GenerateDeviceKeyPair();
        var claimSig = MessagingCryptoEnvelopeService.SignProofOfPossession(priv, DeviceEnrollmentCoordinator.BuildClaimChallengeBytes(created.Record.EnrollmentId, created.Record.TokenHashSha256, deviceId));
        var claimed = await coordinator.ClaimEnrollmentAsync(created.PlaintextEnrollmentToken, deviceId, pub, "Mobile", DevicePlatform.Android, claimSig);
        await coordinator.ApproveEnrollmentAsync(_tenant, "crm", _desktopUser, claimed.EnrollmentId);
        var compSig = MessagingCryptoEnvelopeService.SignProofOfPossession(priv, Convert.FromBase64String(claimed.ChallengeNonceBase64!));
        return await coordinator.CompleteEnrollmentAsync(_tenant, claimed.EnrollmentId, deviceId, compSig, "mobile-api");
    }

    internal sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class AllowEnrollmentAuth : IDeviceEnrollmentAuthorizer
    {
        public ValueTask<MessagingAuthorizationDecision> AuthorizeInitiateAsync(TenantId tenantId, string applicationId, PrincipalId principalId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MessagingAuthorizationDecision(true));
        public ValueTask<MessagingAuthorizationDecision> AuthorizeConfirmAsync(TenantId tenantId, string applicationId, PrincipalId principalId, EnrollmentId enrollmentId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MessagingAuthorizationDecision(true));
        public ValueTask<MessagingAuthorizationDecision> AuthorizeRevokeAsync(TenantId tenantId, string applicationId, PrincipalId principalId, DeviceId deviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MessagingAuthorizationDecision(true));
    }

    private sealed class StubTokenIssuer(TimeProvider clock) : IMobileTokenIssuer
    {
        public ValueTask<MobileTokenBundle> IssueAsync(TenantId tenantId, string applicationId, PrincipalId principalId, DeviceId deviceId, string issuer, string subject, string audience, CancellationToken cancellationToken = default)
        {
            var now = clock.GetUtcNow();
            return ValueTask.FromResult(new MobileTokenBundle(
                $"acc-{Ulid.NewUlid()}", now.AddMinutes(15), $"ref-{Ulid.NewUlid()}", now.AddDays(7),
                new RefreshGrantId(Ulid.NewUlid()), $"fam-{Ulid.NewUlid()}", deviceId));
        }

        public ValueTask<MobileTokenBundle> RotateAsync(RefreshGrantRecord existingGrant, CancellationToken cancellationToken = default)
        {
            var now = clock.GetUtcNow();
            return ValueTask.FromResult(new MobileTokenBundle(
                $"acc-{Ulid.NewUlid()}", now.AddMinutes(15), $"ref-{Ulid.NewUlid()}", now.AddDays(7),
                new RefreshGrantId(Ulid.NewUlid()), existingGrant.FamilyId, existingGrant.DeviceId));
        }
    }

    private sealed class InMemoryEnrollmentStore : IDeviceEnrollmentStore
    {
        private readonly ConcurrentDictionary<EnrollmentId, DeviceEnrollmentRecord> _enrollments = new();
        private readonly ConcurrentDictionary<DeviceId, DeviceRecord> _devices = new();
        private readonly ConcurrentDictionary<RefreshGrantId, RefreshGrantRecord> _grants = new();
        public HashSet<DeviceId> PushRevokedDevices { get; } = new();

        public ValueTask<DeviceEnrollmentRecord> CreateEnrollmentAsync(DeviceEnrollmentRecord enrollment, CancellationToken cancellationToken = default)
        {
            _enrollments[enrollment.EnrollmentId] = enrollment;
            return ValueTask.FromResult(enrollment);
        }

        public ValueTask<DeviceEnrollmentRecord?> GetEnrollmentAsync(TenantId tenantId, EnrollmentId enrollmentId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_enrollments.TryGetValue(enrollmentId, out var r) && r.TenantId == tenantId ? r : null);

        public ValueTask<DeviceEnrollmentRecord?> GetEnrollmentByTokenHashAsync(string tokenHashSha256, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_enrollments.Values.FirstOrDefault(e => e.TokenHashSha256 == tokenHashSha256));

        public ValueTask<bool> TryUpdateEnrollmentAsync(DeviceEnrollmentRecord updated, EnrollmentState expectedState, CancellationToken cancellationToken = default)
        {
            lock (_enrollments)
            {
                if (!_enrollments.TryGetValue(updated.EnrollmentId, out var current) || current.State != expectedState)
                    return ValueTask.FromResult(false);
                _enrollments[updated.EnrollmentId] = updated;
                return ValueTask.FromResult(true);
            }
        }

        public ValueTask<DeviceRecord> UpsertDeviceAsync(DeviceRecord device, CancellationToken cancellationToken = default)
        {
            _devices[device.DeviceId] = device;
            return ValueTask.FromResult(device);
        }

        public ValueTask<DeviceRecord?> GetDeviceAsync(TenantId tenantId, DeviceId deviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_devices.TryGetValue(deviceId, out var d) && d.TenantId == tenantId ? d : null);

        public ValueTask<IReadOnlyList<DeviceRecord>> ListDevicesAsync(TenantId tenantId, PrincipalId principalId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<DeviceRecord>>(_devices.Values.Where(d => d.TenantId == tenantId && d.PrincipalId == principalId).ToArray());

        public ValueTask<RefreshGrantRecord> CreateRefreshGrantAsync(RefreshGrantRecord grant, CancellationToken cancellationToken = default)
        {
            _grants[grant.GrantId] = grant;
            return ValueTask.FromResult(grant);
        }

        public ValueTask<RefreshGrantRecord?> GetRefreshGrantByTokenHashAsync(string tokenHashSha256, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_grants.Values.FirstOrDefault(g => g.TokenHashSha256 == tokenHashSha256));

        public ValueTask<bool> TryUpdateRefreshGrantAsync(RefreshGrantRecord updated, RefreshGrantStatus expectedStatus, CancellationToken cancellationToken = default)
        {
            lock (_grants)
            {
                if (!_grants.TryGetValue(updated.GrantId, out var current) || current.Status != expectedStatus)
                    return ValueTask.FromResult(false);
                _grants[updated.GrantId] = updated;
                return ValueTask.FromResult(true);
            }
        }

        public ValueTask RevokeRefreshFamilyAsync(TenantId tenantId, string familyId, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
        {
            lock (_grants)
            {
                foreach (var kv in _grants.Where(g => g.Value.TenantId == tenantId && g.Value.FamilyId == familyId).ToArray())
                    _grants[kv.Key] = kv.Value with { Status = RefreshGrantStatus.Revoked, RevokedAt = revokedAt };
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask RevokeAllDeviceRefreshGrantsAsync(TenantId tenantId, DeviceId deviceId, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
        {
            lock (_grants)
            {
                foreach (var kv in _grants.Where(g => g.Value.TenantId == tenantId && g.Value.DeviceId == deviceId).ToArray())
                    _grants[kv.Key] = kv.Value with { Status = RefreshGrantStatus.Revoked, RevokedAt = revokedAt };
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask RevokeDevicePushRegistrationsAsync(TenantId tenantId, DeviceId deviceId, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
        {
            PushRevokedDevices.Add(deviceId);
            return ValueTask.CompletedTask;
        }
    }
}
