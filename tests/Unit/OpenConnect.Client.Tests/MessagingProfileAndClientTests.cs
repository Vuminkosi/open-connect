using System.Net;
using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Client.Messaging;
using Xunit;

namespace OpenConnect.Client.Tests;

public sealed class MessagingProfileAndClientTests
{
    [Fact]
    public void QrValidator_AcceptsValidHttpsPayload_AndRejectsHostileUrlsOrExpiredTokens()
    {
        var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var validJson = JsonSerializer.Serialize(new DeviceEnrollmentQrPayload(
            Version: 1,
            Type: "openconnect.device.enrollment",
            BaseGatewayUrl: "https://gateway.company-a.example/api",
            TenantId: Ulid.NewUlid().ToString(),
            ApplicationId: "claims-app",
            PairingId: Ulid.NewUlid().ToString(),
            EnrollmentToken: "high-entropy-single-use-token-0123456789abcdef",
            ExpiresAt: now.AddMinutes(2)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var parsed = MessagingQrEnrollmentValidator.ParseAndValidate(validJson, now);
        Assert.Equal("claims-app", parsed.ApplicationId);

        var hostileUrls = new[]
        {
            "http://gateway.company-a.example/",
            "https://user:pass@gateway.company-a.example/",
            "https://gateway.company-a.example/#fragment",
            "https://gateway.company-a.example/?token=leak",
            "https://gateway.company-a.example/../escape"
        };

        foreach (var badUrl in hostileUrls)
        {
            var badJson = JsonSerializer.Serialize(new DeviceEnrollmentQrPayload(
                1,
                "openconnect.device.enrollment",
                badUrl,
                "tenant-1",
                "app-1",
                "pair-1",
                "high-entropy-single-use-token-0123456789abcdef",
                now.AddMinutes(2)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.Throws<MessagingDomainException>(() => MessagingQrEnrollmentValidator.ParseAndValidate(badJson, now));
        }

        var expiredJson = JsonSerializer.Serialize(new DeviceEnrollmentQrPayload(
            1,
            "openconnect.device.enrollment",
            "https://gateway.company-a.example/",
            "tenant-1",
            "app-1",
            "pair-1",
            "high-entropy-single-use-token-0123456789abcdef",
            now.AddSeconds(-1)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var expiredEx = Assert.Throws<MessagingDomainException>(() => MessagingQrEnrollmentValidator.ParseAndValidate(expiredJson, now));
        Assert.Equal("enrollment_expired", expiredEx.ErrorCode);
    }

    [Fact]
    public async Task ProfileManager_EnforcesSingleActiveProfile_EpochFencing_AndActiveCallConfirmation()
    {
        var storage = new InMemoryMessagingProfileStorage();
        await using var manager = new MessagingProfileManager(storage);

        var profileA = CreateProfile("https://gateway.alpha.example/", "Alpha Insurance", "Claims");
        var profileB = CreateProfile("https://gateway.beta.example/", "Beta Logistics", "Dispatch");

        await manager.AddOrUpdateProfileAsync(profileA.Record, profileA.Creds, activate: true);
        var epochA = manager.CurrentEpoch;
        Assert.Equal(profileA.Record.Identity.ProfileId, manager.ActiveProfile!.Identity.ProfileId);
        Assert.True(manager.IsCurrentEpoch(epochA, profileA.Record.Identity.ProfileId));

        await manager.AddOrUpdateProfileAsync(profileB.Record, profileB.Creds, activate: false);
        Assert.Equal(profileA.Record.Identity.ProfileId, manager.ActiveProfile!.Identity.ProfileId);

        // Simulate active call: switch without confirmation MUST fail closed.
        var callEnded = false;
        manager.SetActiveCallState(true, _ =>
        {
            callEnded = true;
            return ValueTask.CompletedTask;
        });

        var blocked = await manager.SwitchProfileAsync(profileB.Record.Identity.ProfileId, confirmEndActiveCall: false);
        Assert.False(blocked.Succeeded);
        Assert.True(blocked.ActiveCallConfirmationRequired);
        Assert.False(callEnded);
        Assert.Equal(profileA.Record.Identity.ProfileId, manager.ActiveProfile!.Identity.ProfileId);

        // With explicit confirmation: call ends, epoch increments, late callbacks from profile A rejected.
        var switched = await manager.SwitchProfileAsync(profileB.Record.Identity.ProfileId, confirmEndActiveCall: true);
        Assert.True(switched.Succeeded);
        Assert.True(callEnded);
        Assert.True(switched.ProfileEpoch > epochA);
        Assert.Equal(profileB.Record.Identity.ProfileId, manager.ActiveProfile!.Identity.ProfileId);
        Assert.False(manager.IsCurrentEpoch(epochA, profileA.Record.Identity.ProfileId));
        Assert.True(manager.IsCurrentEpoch(switched.ProfileEpoch, profileB.Record.Identity.ProfileId));

        var all = await manager.ListProfilesAsync();
        Assert.Equal(1, all.Count(p => p.State == MessagingProfileState.Active));
    }

    [Fact]
    public async Task ProfileManager_SingleFlightRefresh_RotatesOnce_AndRevokesOnReuseDetected()
    {
        var storage = new InMemoryMessagingProfileStorage();
        await using var manager = new MessagingProfileManager(storage);
        var profile = CreateProfile(
            "https://gateway.alpha.example/",
            "Alpha Insurance",
            "Claims",
            accessExpiresAt: DateTimeOffset.UtcNow.AddSeconds(-5));

        await manager.AddOrUpdateProfileAsync(profile.Record, profile.Creds, activate: true);

        var refreshCalls = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask<MobileTokenBundle> RefreshOnce(MessagingProfileIdentity id, string refreshToken, CancellationToken ct)
        {
            Interlocked.Increment(ref refreshCalls);
            await gate.Task.WaitAsync(ct);
            return new MobileTokenBundle(
                AccessToken: "rotated-access-token-2",
                AccessTokenExpiresAt: DateTimeOffset.UtcNow.AddMinutes(15),
                RefreshToken: "rotated-refresh-token-2",
                RefreshTokenExpiresAt: DateTimeOffset.UtcNow.AddDays(14),
                RefreshGrantId: new RefreshGrantId(Ulid.NewUlid()),
                FamilyId: profile.Creds.FamilyId,
                DeviceId: id.DeviceId);
        }

        var t1 = manager.GetValidAccessTokenAsync(profile.Record.Identity.ProfileId, RefreshOnce).AsTask();
        var t2 = manager.GetValidAccessTokenAsync(profile.Record.Identity.ProfileId, RefreshOnce).AsTask();
        gate.SetResult();

        var tokens = await Task.WhenAll(t1, t2);
        Assert.Equal(1, refreshCalls);
        Assert.All(tokens, t => Assert.Equal("rotated-access-token-2", t));

        var reuseEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await manager.GetValidAccessTokenAsync(
                profile.Record.Identity.ProfileId,
                (_, _, _) => throw new MessagingDomainException("refresh_token_reuse_detected", "Token reuse detected", 401),
                forceRefresh: true));
        Assert.Equal("refresh_token_reuse_detected", reuseEx.ErrorCode);

        var storedProfile = await storage.GetProfileAsync(profile.Record.Identity.ProfileId);
        Assert.Equal(MessagingProfileState.Revoked, storedProfile!.State);
        Assert.Null(await storage.GetCredentialsAsync(profile.Record.Identity.ProfileId));
        Assert.Null(manager.ActiveProfile);
    }

    [Fact]
    public async Task MessagingClient_RejectsCrossOriginRedirect_AndUsesIsolatedPerProfileClients()
    {
        var observedHosts = new List<(string Host, string? Auth)>();
        var storage = new InMemoryMessagingProfileStorage();
        await using var manager = new MessagingProfileManager(
            storage,
            handlerFactory: _ => new StubHttpMessageHandler((req, _) =>
            {
                observedHosts.Add((req.RequestUri!.Host, req.Headers.Authorization?.Parameter));
                if (req.RequestUri.Host == "redirect.evil.example")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri("https://attacker.example/steal");
                    return Task.FromResult(redirect);
                }

                var payload = JsonSerializer.Serialize(Array.Empty<OperationalAgentDiscoveryItem>(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            }));

        var client = new OpenConnectMessagingClient(manager);

        var profileA = CreateProfile("https://alpha.company.example/", "Alpha", "CRM");
        var profileB = CreateProfile("https://beta.company.example/", "Beta", "ERP");
        var redirectProfile = CreateProfile("https://redirect.evil.example/", "Redirect", "App");

        await client.DiscoverAgentsAsync(profileA.Record.Identity, "token-alpha");
        await client.DiscoverAgentsAsync(profileB.Record.Identity, "token-beta");

        Assert.Contains(("alpha.company.example", "token-alpha"), observedHosts);
        Assert.Contains(("beta.company.example", "token-beta"), observedHosts);

        var redirectEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await client.DiscoverAgentsAsync(redirectProfile.Record.Identity, "token-secret"));
        Assert.Equal("redirect_origin_mismatch", redirectEx.ErrorCode);
        Assert.Equal(403, redirectEx.StatusCode);
    }

    private static (MessagingProfileRecord Record, MessagingProfileCredentials Creds) CreateProfile(
        string gatewayUrl,
        string companyName,
        string appName,
        DateTimeOffset? accessExpiresAt = null)
    {
        var tenantId = new TenantId(Ulid.NewUlid());
        var deviceId = new DeviceId(Ulid.NewUlid());
        var identity = MessagingProfileIdentity.Create(
            new Uri(gatewayUrl),
            tenantId,
            appName.ToLowerInvariant(),
            "https://issuer.example",
            "user-123",
            deviceId);

        var record = new MessagingProfileRecord(
            identity,
            companyName,
            appName,
            MessagingProfileState.Inactive);

        var creds = new MessagingProfileCredentials(
            AccessToken: "access-token-" + companyName,
            AccessTokenExpiresAt: accessExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10),
            RefreshToken: "refresh-token-" + companyName,
            RefreshTokenExpiresAt: DateTimeOffset.UtcNow.AddDays(7),
            RefreshGrantId: new RefreshGrantId(Ulid.NewUlid()),
            FamilyId: "fam-" + companyName,
            DeviceId: deviceId);

        return (record, creds);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
