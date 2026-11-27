using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.Knowledge.Tests;

public sealed class VisionEmbeddingClientTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)0xab, 32).ToArray();
    private static EgressContext Context() => new(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
        new SessionId(Ulid.NewUlid()), EgressDestination.Worker);

    [Fact]
    public async Task EncodedSensitiveTextNeverReachesVisionWorker()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        var options = Options.Create(new PrivacyOptions());
        var firewall = new PrivacyFirewall(options,
            new OpenConnect.Privacy.Policy.PolicyEngine(new OpenConnect.Privacy.Rules.RulesSecurityDetector()));
        using var client = new VisionEmbeddingClient(http, "token", Key, new OpenConnect.Privacy.Gates.EgressGate(firewall),
            firewall, options, (_, _, _) => ValueTask.FromResult(true), (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.EmbedTextAsync(Context(), "alice%40example.com"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TextIsAuthorizedSanitizedAndSigned()
    {
        var authorized = false;
        var context = Context();
        var handler = new Handler(async request =>
        {
            Assert.True(authorized);
            var body = await request.Content!.ReadAsByteArrayAsync();
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(Key, body)).ToLowerInvariant(),
                request.Headers.GetValues("X-OpenConnect-Signature").Single());
            using var json = JsonDocument.Parse(body);
            Assert.Equal(context.Tenant.Value.ToString(), json.RootElement.GetProperty("tenantId").GetString());
            Assert.Equal("safe", json.RootElement.GetProperty("payload").GetProperty("text").GetString());
            return Reply(json.RootElement.GetProperty("taskId").GetString()!);
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, true, (_, _, _) => ValueTask.FromResult(true), (_, input, _) =>
        {
            Assert.Equal("private", input);
            authorized = true;
            return ValueTask.FromResult(true);
        });
        var result = await client.EmbedTextAsync(context, "private");
        Assert.Equal(512, result.Dimension);
        Assert.Equal(new string('b', 64), result.SpaceId);
        Assert.Equal(1, result.Items[0].Norm, 5);
    }

    [Fact]
    public async Task ImageIsAuthorizedBeforeItIsSent()
    {
        byte[] image = [1, 2, 3];
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            Assert.Equal("image", json.RootElement.GetProperty("payload").GetProperty("modality").GetString());
            Assert.Equal(image, Convert.FromBase64String(json.RootElement.GetProperty("payload").GetProperty("contentBase64").GetString()!));
            return Reply(json.RootElement.GetProperty("taskId").GetString()!);
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, true, (_, value, _) =>
        {
            Assert.Equal(image, value.ToArray());
            return ValueTask.FromResult(true);
        }, (_, _, _) => ValueTask.FromResult(true));
        Assert.Equal(512, (await client.EmbedImageAsync(Context(), image)).Dimension);
    }

    [Fact]
    public async Task ImageCopyIsClearedWhenAuthorizationFails()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        ReadOnlyMemory<byte> captured = default;
        using var client = Client(http, true, (_, value, _) => { captured = value; return ValueTask.FromResult(false); },
            (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.EmbedImageAsync(Context(), new byte[] { 1, 2 }));
        Assert.Equal(new byte[] { 0, 0 }, captured.ToArray());
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DisabledRecordedFirewallBlocksImageAndReportsUnavailableEnforcement()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        var sink = new EvidenceSink();
        using var http = new HttpClient(handler);
        using var client = new VisionEmbeddingClient(http, "token", Key, new Gate(true),
            new EvidenceRecordingPrivacyFirewall(new DisabledFirewall(), sink), Options.Create(new PrivacyOptions()),
            (_, _, _) => ValueTask.FromResult(true), (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.EmbedImageAsync(Context(), new byte[] { 1 }));
        Assert.Equal(0, handler.Calls);
        Assert.Equal("enforcement_unavailable", Assert.Single(sink.Events).ReasonCode);
    }

    [Fact]
    public async Task Disabled_decision_prevents_image_embedding_dispatch()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = new VisionEmbeddingClient(http, "token", Key, new Gate(true), new DisabledDecisionFirewall(),
            Options.Create(new PrivacyOptions()), (_, _, _) => ValueTask.FromResult(true), (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.EmbedImageAsync(Context(), new byte[] { 1 }));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Serialized_image_request_is_cleared_after_worker_failure()
    {
        MemoryStream? body = null;
        var handler = new Handler(async request =>
        {
            body = Assert.IsType<MemoryStream>(await request.Content!.ReadAsStreamAsync());
            Assert.Contains(body.ToArray(), item => item != 0);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, true, (_, _, _) => ValueTask.FromResult(true), (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.EmbedImageAsync(Context(), new byte[] { 1, 2, 3 }));
        Assert.NotNull(body);
        Assert.All(body.ToArray(), item => Assert.Equal(0, item));
    }

    [Fact]
    public async Task Image_deadline_covers_authorization_and_clears_image_copy()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        ReadOnlyMemory<byte> captured = default;
        using var client = new VisionEmbeddingClient(http, "token", Key, new Gate(true), new Firewall(),
            Options.Create(new PrivacyOptions()), async (_, image, token) =>
            {
                captured = image;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            }, (_, _, _) => ValueTask.FromResult(true), requestTimeout: TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EmbedImageAsync(Context(), new byte[] { 1, 2 }));
        Assert.Equal(new byte[] { 0, 0 }, captured.ToArray());
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Text_deadline_covers_authorization_before_dispatch()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = new VisionEmbeddingClient(http, "token", Key, new Gate(true), new Firewall(),
            Options.Create(new PrivacyOptions()), (_, _, _) => ValueTask.FromResult(true), async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            }, requestTimeout: TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EmbedTextAsync(Context(), "forest"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DisabledModalityOptionsBlockImageEvenIfFirewallIsEnabled()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = new VisionEmbeddingClient(http, "token", Key, new Gate(true), new Firewall(),
            Options.Create(new PrivacyOptions { Mode = FirewallMode.Disabled }),
            (_, _, _) => ValueTask.FromResult(true), (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.EmbedImageAsync(Context(), new byte[] { 1 }));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MissingDetectorBlocksImageBeforeDispatchAndRecordsReason()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        var sink = new EvidenceSink();
        var options = Options.Create(new PrivacyOptions());
        var firewall = new EvidenceRecordingPrivacyFirewall(
            new PrivacyFirewall(options, new OpenConnect.Privacy.Policy.PolicyEngine()), sink);
        using var http = new HttpClient(handler);
        using var client = new VisionEmbeddingClient(http, "token", Key, new Gate(true), firewall, options,
            (_, _, _) => ValueTask.FromResult(true), (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.EmbedImageAsync(Context(), new byte[] { 1 }));
        Assert.Equal(0, handler.Calls);
        Assert.Equal("enforcement_unavailable", Assert.Single(sink.Events).ReasonCode);
    }

    [Fact]
    public async Task OutputPolicyCanBlockVisionResponseMetadata()
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Reply(json.RootElement.GetProperty("taskId").GetString()!);
        });
        using var http = new HttpClient(handler);
        using var client = new VisionEmbeddingClient(http, "token", Key, new OutputDenyGate(), new Firewall(),
            Options.Create(new PrivacyOptions()), (_, _, _) => ValueTask.FromResult(true), (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.EmbedTextAsync(Context(), "text"));
    }

    [Fact]
    public async Task DeniedAuthorizationOrPrivacyNeverSendsContent()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var denied = Client(http, true, (_, _, _) => ValueTask.FromResult(false), (_, _, _) => ValueTask.FromResult(false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.EmbedImageAsync(Context(), new byte[] { 1 }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.EmbedTextAsync(Context(), "secret"));
        using var blocked = Client(http, false, (_, _, _) => ValueTask.FromResult(true), (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => blocked.EmbedTextAsync(Context(), "secret"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DetectedButUnmaskedTextNeverReachesVisionWorker()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = new VisionEmbeddingClient(http, "token", Key, new UnmaskedGate(), new Firewall(),
            Options.Create(new PrivacyOptions()), (_, _, _) => ValueTask.FromResult(true), (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.EmbedTextAsync(Context(), "secret"));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class UnmaskedGate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [new(0, 6, "SECRET")], SecurityPolicyAction.Allow, "test"));
    }

    [Fact]
    public async Task RejectsUnnormalizedWorkerVector()
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Reply(json.RootElement.GetProperty("taskId").GetString()!, zero: true);
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, true, (_, _, _) => ValueTask.FromResult(true), (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedTextAsync(Context(), "query"));
    }

    private static VisionEmbeddingClient Client(HttpClient http, bool allowed,
        Func<EgressContext, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> image,
        Func<EgressContext, string, CancellationToken, ValueTask<bool>> text) =>
        new(http, "token", Key, new Gate(allowed), new Firewall(), Options.Create(new PrivacyOptions()), image, text);

    private static HttpResponseMessage Reply(string taskId, bool zero = false) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            schemaVersion = "openconnect.v1",
            taskId,
            kind = "vision.embed",
            result = new
            {
                vector = Enumerable.Range(0, 512).Select(i => !zero && i == 0 ? 1.0 : 0.0),
                model_id = "fixture",
                fingerprint = new string('a', 64),
                space_id = new string('b', 64),
                dimension = 512
            }
        }),
            Encoding.UTF8, "application/json")
    };

    private sealed class Gate(bool allowed) : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(allowed, allowed ? context.Destination == EgressDestination.Output ? content : "safe" : null, Array.Empty<EntitySpan>(),
                allowed ? SecurityPolicyAction.Allow : SecurityPolicyAction.Block, "test"));
    }

    private sealed class OutputDenyGate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(context.Destination == EgressDestination.Output
                ? new EgressDecision(false, null, [], SecurityPolicyAction.Block, "test")
                : new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
    }

    private sealed class Firewall : IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
    }

    private sealed class DisabledFirewall : IPrivacyFirewall
    {
        public bool Enabled => false;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Disabled firewall must not inspect content.");
    }

    private sealed class EvidenceSink : IHostPrivacyEvidenceSink
    {
        public List<HostPrivacyEvidence> Events { get; } = [];
        public ValueTask RecordAsync(HostPrivacyEvidence evidence, CancellationToken cancellationToken = default)
        {
            Events.Add(evidence);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request);
        }
    }
}
