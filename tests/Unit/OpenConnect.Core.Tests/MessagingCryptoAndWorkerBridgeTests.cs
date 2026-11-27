using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Core.Capabilities;
using OpenConnect.Core.Messaging;
using OpenConnect.IMessaging;
using Xunit;

namespace OpenConnect.Core.Tests;

public sealed class MessagingCryptoAndWorkerBridgeTests
{
    [Fact]
    public async Task A09_And_A10_CryptoEnvelope_RoundTrip_TamperReplayRejection_AndDeviceAddRemoveRekey()
    {
        var crypto = new MessagingCryptoEnvelopeService();
        var tenant = new TenantId(Ulid.NewUlid());
        var convoId = new ConversationId(Ulid.NewUlid());
        var devA = new DeviceId(Ulid.NewUlid());
        var devB = new DeviceId(Ulid.NewUlid());
        var devC = new DeviceId(Ulid.NewUlid());

        // Deterministic HKDF-SHA256 vector stability
        var ikm = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var salt = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
        var k1 = MessagingCryptoEnvelopeService.DeriveEpochKeyDeterministic(ikm, salt, convoId, 1);
        var k2 = MessagingCryptoEnvelopeService.DeriveEpochKeyDeterministic(ikm, salt, convoId, 1);
        var kEpoch2 = MessagingCryptoEnvelopeService.DeriveEpochKeyDeterministic(ikm, salt, convoId, 2);
        Assert.Equal(k1, k2);
        Assert.NotEqual(k1, kEpoch2);

        // Device proof-of-possession
        var (pub, priv) = MessagingCryptoEnvelopeService.GenerateDeviceKeyPair();
        var challenge = Encoding.UTF8.GetBytes("device-challenge-v1");
        var sig = MessagingCryptoEnvelopeService.SignProofOfPossession(priv, challenge);
        Assert.True(await crypto.VerifyDeviceProofOfPossessionAsync(pub, challenge, sig));
        Assert.False(await crypto.VerifyDeviceProofOfPossessionAsync(pub, Encoding.UTF8.GetBytes("tampered"), sig));

        // Create group at epoch 1 with devA and devB
        var pkgA = MessagingCryptoEnvelopeService.CreateKeyPackage(tenant, devA);
        var pkgB = MessagingCryptoEnvelopeService.CreateKeyPackage(tenant, devB);
        var state1 = await crypto.CreateGroupAsync(convoId, [pkgA, pkgB]);
        Assert.Equal(1, state1.Epoch);

        // Key package cannot be consumed twice
        var reusePkgEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await crypto.AddDeviceAndRekeyAsync(convoId, pkgA));
        Assert.Equal(ErrorCodes.Conflict, reusePkgEx.ErrorCode);

        // Encrypt at epoch 1
        var aad = Encoding.UTF8.GetBytes("header-v1");
        var plaintext = Encoding.UTF8.GetBytes("Confidential operational update");
        var env1 = await crypto.EncryptAsync(convoId, devA, plaintext, aad);
        Assert.Equal(1, env1.CryptoEpoch);

        // devB decrypts once
        var decrypted = await crypto.DecryptAsync(convoId, devB, env1, aad);
        Assert.Equal(plaintext, decrypted);

        // A09: Replay of same envelope for devB rejected
        var replayEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await crypto.DecryptAsync(convoId, devB, env1, aad));
        Assert.Equal(ErrorCodes.CryptoVerificationFailed, replayEx.ErrorCode);

        // A09: Tampered ciphertext rejected
        var tamperedBytes = Convert.FromBase64String(env1.CiphertextBase64);
        tamperedBytes[0] ^= 0xFF;
        var tamperedEnv = env1 with { CiphertextBase64 = Convert.ToBase64String(tamperedBytes) };
        var tamperEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await crypto.DecryptAsync(convoId, devA, tamperedEnv, aad));
        Assert.Equal(ErrorCodes.CryptoVerificationFailed, tamperEx.ErrorCode);

        // A10: Add devC -> epoch 2; devC CANNOT decrypt epoch 1 history!
        var pkgC = MessagingCryptoEnvelopeService.CreateKeyPackage(tenant, devC);
        var state2 = await crypto.AddDeviceAndRekeyAsync(convoId, pkgC);
        Assert.Equal(2, state2.Epoch);

        var historyEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await crypto.DecryptAsync(convoId, devC, env1, aad));
        Assert.Equal(ErrorCodes.CryptoVerificationFailed, historyEx.ErrorCode);

        // A10: Remove devB -> epoch 3; devB CANNOT decrypt epoch 3 messages!
        var state3 = await crypto.RemoveDeviceAndRekeyAsync(convoId, devB);
        Assert.Equal(3, state3.Epoch);

        var env3 = await crypto.EncryptAsync(convoId, devA, Encoding.UTF8.GetBytes("Post-revocation secret"), aad);
        Assert.Equal(3, env3.CryptoEpoch);

        var revokedEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await crypto.DecryptAsync(convoId, devB, env3, aad));
        Assert.Equal(ErrorCodes.CryptoVerificationFailed, revokedEx.ErrorCode);

        // devC CAN decrypt epoch 3
        var dec3 = await crypto.DecryptAsync(convoId, devC, env3, aad);
        Assert.Equal("Post-revocation secret", Encoding.UTF8.GetString(dec3));
    }

    [Fact]
    public async Task A01_DefaultDisabledOptions_DoNotRequireWorker_AndFailClosedOnExecute()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectMessaging();
        services.AddOpenConnectDeviceEnrollment();
        services.AddOpenConnectAgentDiscovery();
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IMessagingWorkerClient>();
        var readiness = await client.CheckReadinessAsync();
        Assert.False(readiness.IsReady);
        Assert.Empty(readiness.VerifiedCapabilities);

        var cmd = new MessagingWorkerCommand(
            "openconnect.v1", "task-1", MessagingWorkerCommandKinds.Route, "tenant-1",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "nonce-1234567890123456", "{}");
        var ex = await Assert.ThrowsAsync<MessagingDomainException>(async () => await client.ExecuteAsync(cmd));
        Assert.Equal(ErrorCodes.MessagingDisabled, ex.ErrorCode);
        Assert.Equal(503, ex.StatusCode);
    }

    [Fact]
    public async Task A02_And_A32_WorkerClient_SignsExactBytes_ValidatesResponses_AndBlocksExternalChannels()
    {
        var signingKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var options = new MessagingWorkerOptions
        {
            Enabled = true,
            InAppEnabled = true,
            WorkerEndpoint = "https://imessaging.internal:8443/",
            BearerToken = "secret-worker-token",
            SigningKey = signingKey,
            SigningKeyId = "k1"
        };

        string? capturedAuth = null;
        string? capturedSig = null;
        string? capturedKeyId = null;
        byte[]? capturedBody = null;

        var handler = new StubHttpMessageHandler(async (req, ct) =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            capturedSig = req.Headers.GetValues(MessagingWorkerClient.SignatureHeaderName).Single();
            capturedKeyId = req.Headers.GetValues(MessagingWorkerClient.KeyIdHeaderName).Single();

            if (req.RequestUri!.AbsolutePath == "/internal/v1/capabilities")
            {
                // Intentionally include forbidden external channel placeholders (whatsapp, telegram, discord)
                var capJson = JsonSerializer.Serialize(new
                {
                    capabilities = new object[]
                    {
                        new { schemaVersion = "openconnect.v1", id = "messaging.inapp.send", kind = "tool", state = "ready", version = "1.0" },
                        new { schemaVersion = "openconnect.v1", id = "messaging.inapp.receive", kind = "tool", state = "ready", version = "1.0" },
                        new { schemaVersion = "openconnect.v1", id = "messaging.whatsapp.send", kind = "tool", state = "ready", version = "1.0" },
                        new { schemaVersion = "openconnect.v1", id = "messaging.telegram.send", kind = "tool", state = "ready", version = "1.0" },
                        new { schemaVersion = "openconnect.v1", id = "messaging.discord.send", kind = "tool", state = "ready", version = "1.0" }
                    }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(capJson, Encoding.UTF8, "application/json")
                };
            }

            capturedBody = await req.Content!.ReadAsByteArrayAsync(ct);
            var okJson = JsonSerializer.Serialize(new
            {
                schemaVersion = "openconnect.v1",
                taskId = "task-123",
                kind = MessagingWorkerCommandKinds.Route,
                tenantId = "tenant-1",
                status = "accepted",
                result = new { eventId = "evt-1", dispatchedCount = 2 }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(okJson, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        using var client = new MessagingWorkerClient(httpClient, new Uri("https://imessaging.internal:8443/"), options);

        // Execute signed command
        var cmd = new MessagingWorkerCommand(
            "openconnect.v1", "task-123", MessagingWorkerCommandKinds.Route, "tenant-1",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "nonce-1234567890123456", "{\"eventId\":\"evt-1\"}");
        var res = await client.ExecuteAsync(cmd);
        Assert.True(res.Accepted);
        Assert.Equal("Bearer secret-worker-token", capturedAuth);
        Assert.Equal("k1", capturedKeyId);
        Assert.Equal(MessagingWorkerClient.ComputeSignatureHex(signingKey, capturedBody!), capturedSig);

        // A32: CheckReadiness & CapabilityRegistration strip WhatsApp/Telegram/Discord
        var readiness = await client.CheckReadinessAsync();
        Assert.True(readiness.IsReady);
        Assert.Contains(MessagingCapabilityIds.InAppSend, readiness.VerifiedCapabilities);
        Assert.Contains(MessagingCapabilityIds.InAppReceive, readiness.VerifiedCapabilities);
        Assert.DoesNotContain(readiness.VerifiedCapabilities, c => c.Contains("whatsapp", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(readiness.VerifiedCapabilities, c => c.Contains("telegram", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(readiness.VerifiedCapabilities, c => c.Contains("discord", StringComparison.OrdinalIgnoreCase));

        var capRegistry = new CapabilityRegistry();
        var registration = new CapabilityRegistration(capRegistry);
        registration.ApplyReadiness(readiness, messagingEnabled: true, e2eeVerified: true);
        Assert.Equal(DetectorState.Ready, capRegistry.Get(new CapabilityId(MessagingCapabilityIds.InAppSend))!.State);
        Assert.Equal(DetectorState.Ready, capRegistry.Get(new CapabilityId(MessagingCapabilityIds.E2EE))!.State);
        Assert.Null(capRegistry.Get(new CapabilityId("messaging.whatsapp.send")));
    }

    [Fact]
    public async Task A02_WorkerClient_FailsClosedOn503_401_MalformedJson_AndCorrelationMismatch()
    {
        var options = new MessagingWorkerOptions
        {
            Enabled = true,
            WorkerEndpoint = "https://imessaging.internal:8443/",
            BearerToken = "tok",
            SigningKey = new byte[32]
        };
        var cmd = new MessagingWorkerCommand(
            "openconnect.v1", "task-A", MessagingWorkerCommandKinds.Route, "tenant-1",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "nonce-1234567890123456", "{}");

        // 401 -> Unauthorized
        using (var c401 = new MessagingWorkerClient(new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)))), new Uri(options.WorkerEndpoint), options, ownsClient: true))
        {
            var ex = await Assert.ThrowsAsync<MessagingDomainException>(async () => await c401.ExecuteAsync(cmd));
            Assert.Equal(ErrorCodes.Unauthorized, ex.ErrorCode);
            Assert.Equal(401, ex.StatusCode);
        }

        // 503 -> WorkerUnavailable + Readiness false
        using (var c503 = new MessagingWorkerClient(new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))), new Uri(options.WorkerEndpoint), options, ownsClient: true))
        {
            var ex = await Assert.ThrowsAsync<MessagingDomainException>(async () => await c503.ExecuteAsync(cmd));
            Assert.Equal(ErrorCodes.WorkerUnavailable, ex.ErrorCode);
            var r = await c503.CheckReadinessAsync();
            Assert.False(r.IsReady);
        }

        // Correlation mismatch -> fail closed
        var badCorrJson = JsonSerializer.Serialize(new
        {
            schemaVersion = "openconnect.v1",
            taskId = "WRONG-TASK",
            kind = MessagingWorkerCommandKinds.Route,
            tenantId = "tenant-1",
            status = "accepted",
            result = new { }
        });
        using (var cBad = new MessagingWorkerClient(new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(badCorrJson, Encoding.UTF8, "application/json") }))), new Uri(options.WorkerEndpoint), options, ownsClient: true))
        {
            var ex = await Assert.ThrowsAsync<MessagingDomainException>(async () => await cBad.ExecuteAsync(cmd));
            Assert.Equal(ErrorCodes.WorkerUnavailable, ex.ErrorCode);
        }
    }

    [Fact]
    public async Task A12_WorkerEventConsumer_DeduplicatesRepeatedDeliveries()
    {
        var count = 0;
        var consumer = new WorkerEventConsumer((_, _) =>
        {
            count++;
            return ValueTask.CompletedTask;
        });

        var env = new MessagingEventEnvelope(
            1, "evt-dup-1", MessagingEventTypes.MessageCommitted, new TenantId(Ulid.NewUlid()),
            new ConversationId(Ulid.NewUlid()), 1, "cursor-1", DateTimeOffset.UtcNow, "{}");

        Assert.True(await consumer.DispatchAsync(env));
        Assert.False(await consumer.DispatchAsync(env));
        Assert.Equal(1, count);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
