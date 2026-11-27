using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.Knowledge.Tests;

public sealed class KnowledgeEmbeddingClientTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)0xab, 32).ToArray();
    private static EgressContext Context() => new(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
        new SessionId(Ulid.NewUlid()), EgressDestination.Worker);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncodedSensitiveTextNeverReachesEmbeddingWorker(bool split)
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        var firewall = new PrivacyFirewall(Microsoft.Extensions.Options.Options.Create(new PrivacyOptions()),
            new OpenConnect.Privacy.Policy.PolicyEngine(new OpenConnect.Privacy.Rules.RulesSecurityDetector()));
        using var client = new KnowledgeEmbeddingClient(http, new OpenConnect.Privacy.Gates.EgressGate(firewall),
            new Uri("https://worker.test"), "worker-token", Key, EgressDestination.Worker);
        var inputs = split ? new[] { "alice%40", "example.com" } : new[] { "alice%2540example.com" };
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.EmbedAsync(Context(), "query", inputs));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CloudEmbeddingRejectsMismatchedResponseIdentityAndTrace()
    {
        var mismatch = true;
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            var id = json.RootElement.GetProperty("requestId").GetString();
            using var workerResponse = Response(id!, dimension: 2);
            using var worker = JsonDocument.Parse(await workerResponse.Content.ReadAsByteArrayAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    schemaVersion = "openconnect.cloud.knowledge-embed.result/v1",
                    requestId = mismatch ? Guid.NewGuid().ToString("D") : id,
                    traceId = mismatch ? new string('a', 32) : "invalid",
                    result = worker.RootElement.GetProperty("result")
                }))
            };
        });
        using var http = new HttpClient(handler);
        using var client = new KnowledgeEmbeddingClient(http, new PassThroughGate(), new Uri("https://worker.test"), "cloud-token", Key,
            EgressDestination.ExternalModel, cloudGateway: true);
        var context = Context() with { Destination = EgressDestination.ExternalModel };
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedAsync(context, "query", ["forest"]));
        mismatch = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedAsync(context, "query", ["forest"]));
    }

    [Fact]
    public void CloudFactoryRejectsInvalidCredentialsAndInsecureOrigins()
    {
        Assert.Throws<ArgumentException>(() => KnowledgeEmbeddingClient.CreateCloud(new PassThroughGate(), new Uri("http://gateway.test"), "token"));
        Assert.Throws<ArgumentException>(() => KnowledgeEmbeddingClient.CreateCloud(new PassThroughGate(), new Uri("https://gateway.test"), new string('a', 161)));
        Assert.Throws<ArgumentException>(() => KnowledgeEmbeddingClient.CreateCloud(new PassThroughGate(), new Uri("https://gateway.test"), "token,other"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmbeddingScreensNestedResponseBeforeReturningIt(bool cloud)
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            var result = new
            {
                model_id = "model",
                fingerprint = new string('a', 64),
                dimension = 2,
                space_id = new string('b', 64),
                items = new[] { new { vector = new[] { 0.6, 0.8 }, truncated = false, norm = 1, note = "person@example.test" } }
            };
            var body = cloud
                ? JsonSerializer.Serialize(new { schemaVersion = "openconnect.cloud.knowledge-embed.result/v1", requestId = json.RootElement.GetProperty("requestId").GetString(), traceId = new string('a', 32), result })
                : JsonSerializer.Serialize(new { schemaVersion = "openconnect.v1", taskId = json.RootElement.GetProperty("taskId").GetString(), kind = "knowledge.embed", result });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        using var http = new HttpClient(handler);
        var firewall = new PrivacyFirewall(Microsoft.Extensions.Options.Options.Create(new PrivacyOptions()),
            new OpenConnect.Privacy.Policy.PolicyEngine(new OpenConnect.Privacy.Rules.RulesSecurityDetector()));
        var destination = cloud ? EgressDestination.ExternalModel : EgressDestination.Worker;
        using var client = new KnowledgeEmbeddingClient(http, new OpenConnect.Privacy.Gates.EgressGate(firewall),
            new Uri("https://worker.test"), "worker-token", Key, destination, cloudGateway: cloud);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.EmbedAsync(Context() with { Destination = destination }, "query", ["forest"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmbeddingBlocksSensitiveTextSplitAcrossInputs(bool cloud)
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        var firewall = new PrivacyFirewall(Microsoft.Extensions.Options.Options.Create(new PrivacyOptions()),
            new OpenConnect.Privacy.Policy.PolicyEngine(new OpenConnect.Privacy.Rules.RulesSecurityDetector()));
        var destination = cloud ? EgressDestination.ExternalModel : EgressDestination.Worker;
        using var client = new KnowledgeEmbeddingClient(http, new OpenConnect.Privacy.Gates.EgressGate(firewall),
            new Uri("https://worker.test"), "worker-token", Key, destination, cloudGateway: cloud);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.EmbedAsync(Context() with { Destination = destination },
            "query", ["alice@", "example.test"]));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CloudEmbeddingUsesAuthenticatedGatewayContractAndHostPrivacyGate()
    {
        var context = Context() with { Destination = EgressDestination.ExternalModel };
        var handler = new Handler(async request =>
        {
            Assert.Equal("https://worker.test/cloud/v1/knowledge/embeddings", request.RequestUri!.ToString());
            Assert.False(request.Headers.Contains("X-OpenConnect-Signature"));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            var root = json.RootElement;
            Assert.Equal("openconnect.cloud.knowledge-embed/v1", root.GetProperty("schemaVersion").GetString());
            Assert.Equal("knowledge.embed", root.GetProperty("capability").GetString());
            Assert.False(root.TryGetProperty("tenantId", out _));
            Assert.Equal("sanitized", root.GetProperty("input").GetProperty("texts")[0].GetString());
            var id = root.GetProperty("requestId").GetString();
            using var workerResponse = Response(id!, dimension: 2);
            using var worker = JsonDocument.Parse(await workerResponse.Content.ReadAsByteArrayAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                { schemaVersion = "openconnect.cloud.knowledge-embed.result/v1", requestId = id, traceId = new string('a', 32), result = worker.RootElement.GetProperty("result") }))
            };
        });
        using var http = new HttpClient(handler);
        using var client = new KnowledgeEmbeddingClient(http, new CloudMaskGate(), new Uri("https://worker.test"), "cloud-token", Key,
            EgressDestination.ExternalModel, cloudGateway: true);
        var result = await client.EmbedAsync(context, "query", ["private input"]);
        Assert.Equal(new float[] { 0.6f, 0.8f }, Assert.Single(result.Items).Vector);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task EmbeddingLimitsCountUnicodeScalarsAndRejectInvalidSurrogates()
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            Assert.Equal(8192, json.RootElement.GetProperty("payload").GetProperty("texts")[0].GetString()!.EnumerateRunes().Count());
            return Response(json.RootElement.GetProperty("taskId").GetString()!, dimension: 2);
        });
        using var http = new HttpClient(handler);
        using var client = new KnowledgeEmbeddingClient(http, new PassThroughGate(), new Uri("https://worker.test"),
            "worker-token", Key, EgressDestination.Worker);
        var input = string.Concat(Enumerable.Repeat("😀", 8192));
        Assert.Single((await client.EmbedAsync(Context(), "query", [input])).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => client.EmbedAsync(Context(), "query", [input + "😀"]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.EmbedAsync(Context(), "query", ["\ud800"]));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SignsExactlyTheSanitizedBodyAndBindsTenant()
    {
        var context = Context();
        var handler = new Handler(async request =>
        {
            var body = await request.Content!.ReadAsByteArrayAsync();
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(Key, body)).ToLowerInvariant(),
                request.Headers.GetValues("X-OpenConnect-Signature").Single());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("worker-token", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(context.Tenant.Value.ToString(), json.RootElement.GetProperty("tenantId").GetString());
            Assert.Equal("sanitized", json.RootElement.GetProperty("payload").GetProperty("texts")[0].GetString());
            var task = json.RootElement.GetProperty("taskId").GetString();
            return Response(task!, dimension: 2);
        });
        using var http = new HttpClient(handler);
        var client = new KnowledgeEmbeddingClient(http, new Gate(true), new Uri("https://worker.test"), "worker-token", Key, EgressDestination.Worker);
        var result = await client.EmbedAsync(context, "query", ["private input"]);
        Assert.Equal(2, result.Dimension);
        Assert.Equal(new float[] { 0.6f, 0.8f }, result.Items[0].Vector);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task EmbeddingSnapshotsTheBatchBeforePrivacyEvaluation()
    {
        var inputs = new List<string> { "first" };
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            var texts = json.RootElement.GetProperty("payload").GetProperty("texts");
            Assert.Equal("first", Assert.Single(texts.EnumerateArray()).GetString());
            return Response(json.RootElement.GetProperty("taskId").GetString()!, dimension: 2);
        });
        using var http = new HttpClient(handler);
        using var client = new KnowledgeEmbeddingClient(http, new MutatingGate(inputs), new Uri("https://worker.test"),
            "worker-token", Key, EgressDestination.Worker);
        var result = await client.EmbedAsync(Context(), "query", inputs);
        Assert.Single(result.Items);
        Assert.Equal(2, inputs.Count);
    }

    [Fact]
    public async Task PrivacyDenialDoesNotSendData()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        var client = new KnowledgeEmbeddingClient(http, new Gate(false), new Uri("https://worker.test"), "worker-token", Key, EgressDestination.Worker);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.EmbedAsync(Context(), "query", ["secret"]));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledStandardFirewallDoesNotDispatchText(bool disabledMode)
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        var options = Microsoft.Extensions.Options.Options.Create(disabledMode
            ? new PrivacyOptions { Mode = FirewallMode.Disabled } : new PrivacyOptions { Enabled = false });
        var firewall = new PrivacyFirewall(options, new OpenConnect.Privacy.Policy.PolicyEngine());
        using var client = new KnowledgeEmbeddingClient(http, new OpenConnect.Privacy.Gates.EgressGate(firewall),
            new Uri("https://worker.test"), "worker-token", Key, EgressDestination.Worker);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.EmbedAsync(Context(), "query", ["private input"]));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task OutputPolicyCanBlockEmbeddingResponseMetadata()
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Response(json.RootElement.GetProperty("taskId").GetString()!, dimension: 2);
        });
        using var http = new HttpClient(handler);
        using var client = new KnowledgeEmbeddingClient(http, new OutputDenyGate(), new Uri("https://worker.test"),
            "worker-token", Key, EgressDestination.Worker);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.EmbedAsync(Context(), "query", ["text"]));
    }

    [Fact]
    public async Task Detected_but_unmasked_text_never_reaches_worker()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = new KnowledgeEmbeddingClient(http, new UnmaskedGate(), new Uri("https://worker.test"),
            "worker-token", Key, EgressDestination.Worker);
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.EmbedAsync(Context(), "query", ["secret"]));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class UnmaskedGate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [new(0, 6, "SECRET")], SecurityPolicyAction.Allow, "test"));
    }

    [Fact]
    public async Task RejectsDimensionMismatch()
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Response(json.RootElement.GetProperty("taskId").GetString()!, dimension: 3);
        });
        using var http = new HttpClient(handler);
        var client = new KnowledgeEmbeddingClient(http, new Gate(true), new Uri("https://worker.test"), "worker-token", Key, EgressDestination.Worker);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedAsync(Context(), "query", ["text"]));
    }

    [Theory]
    [InlineData(0.0, 0.0, 1.0)]
    [InlineData(1.2, 1.6, 1.0)]
    [InlineData(0.6, 0.8, 0.5)]
    public async Task RejectsVectorsWithInvalidOrMisreportedNorm(double first, double second, double reportedNorm)
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Response(json.RootElement.GetProperty("taskId").GetString()!, 2, [first, second], reportedNorm);
        });
        using var http = new HttpClient(handler);
        using var client = new KnowledgeEmbeddingClient(http, new Gate(true), new Uri("https://worker.test"),
            "worker-token", Key, EgressDestination.Worker);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedAsync(Context(), "query", ["text"]));
    }

    [Fact]
    public async Task TimesOutIfWorkerResponseBodyStalls()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream())
        }));
        using var http = new HttpClient(handler);
        using var client = new KnowledgeEmbeddingClient(http, new Gate(true), new Uri("https://worker.test"),
            "worker-token", Key, EgressDestination.Worker, requestTimeout: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EmbedAsync(Context(), "query", ["text"]));
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("duplicate")]
    public async Task RejectsUnexpectedOrDuplicateResponseFields(string mutation)
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            using var original = Response(json.RootElement.GetProperty("taskId").GetString()!, dimension: 2);
            var body = await original.Content.ReadAsStringAsync();
            var extension = mutation == "extra" ? ",\"unknown\":1" : ",\"kind\":\"knowledge.embed\"";
            body = body.Insert(body.Length - 1, extension);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler);
        using var client = new KnowledgeEmbeddingClient(http, new Gate(true), new Uri("https://worker.test"),
            "worker-token", Key, EgressDestination.Worker);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedAsync(Context(), "query", ["text"]));
    }

    [LiveWorkerFact]
    public async Task SendsPrivacyScreenedRequestToRealWorker()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OC_TEST_KNOWLEDGE_ENDPOINT")!);
        var token = Environment.GetEnvironmentVariable("OC_TEST_KNOWLEDGE_TOKEN")!;
        var key = Convert.FromHexString(Environment.GetEnvironmentVariable("OC_TEST_KNOWLEDGE_KEY")!);
        using var client = new KnowledgeEmbeddingClient(new Gate(true), endpoint, token, key, EgressDestination.Worker, allowInsecureHttp: true);
        var result = await client.EmbedAsync(Context(), "query", ["private input"]);
        Assert.Single(result.Items);
        Assert.Equal(384, result.Dimension);
        Assert.Equal(1.0, result.Items[0].Norm, 4);
    }

    [Fact]
    public async Task RemoteDestinationCannotBeLabeledAsLocalWorker()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        var client = new KnowledgeEmbeddingClient(http, new Gate(true), new Uri("https://worker.test"), "worker-token", Key);
        await Assert.ThrowsAsync<ArgumentException>(() => client.EmbedAsync(Context(), "query", ["private"]));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RedirectDoesNotForwardWorkerRequest()
    {
        var source = new TcpListener(IPAddress.Loopback, 0);
        var target = new TcpListener(IPAddress.Loopback, 0);
        source.Start();
        target.Start();
        try
        {
            var sourcePort = ((IPEndPoint)source.LocalEndpoint).Port;
            var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var connection = await source.AcceptTcpClientAsync();
                await using var stream = connection.GetStream();
                var input = new byte[8192];
                Assert.True(await stream.ReadAsync(input) > 0);
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{targetPort}/stolen\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response);
            });
            using var client = new KnowledgeEmbeddingClient(new Gate(true), new Uri($"http://127.0.0.1:{sourcePort}"),
                "worker-token", Key, EgressDestination.Worker, allowInsecureHttp: true);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.EmbedAsync(Context(), "query", ["private"]));
            await server;
            Assert.False(target.Pending());
        }
        finally
        {
            source.Stop();
            target.Stop();
        }
    }

    private static HttpResponseMessage Response(string taskId, int dimension, double[]? vector = null, double norm = 1.0)
    {
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = "openconnect.v1",
            taskId,
            kind = "knowledge.embed",
            result = new
            {
                model_id = "model",
                fingerprint = new string('a', 64),
                space_id = new string('b', 64),
                dimension,
                items = new[] { new { vector = vector ?? [0.6, 0.8], truncated = false, norm } }
            }
        });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class Gate(bool allowed) : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(allowed, allowed ? content == "private input" && context.Destination != EgressDestination.Output ? "sanitized" : content : null,
                Array.Empty<EntitySpan>(), allowed ? SecurityPolicyAction.Allow : SecurityPolicyAction.Block, "test"));
    }

    private sealed class PassThroughGate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
    }

    private sealed class CloudMaskGate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content == "private input" ? "sanitized" : content, [], SecurityPolicyAction.Allow, "test"));
    }

    private sealed class OutputDenyGate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(context.Destination == EgressDestination.Output
                ? new EgressDecision(false, null, [], SecurityPolicyAction.Block, "test")
                : new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
    }

    private sealed class MutatingGate(List<string> inputs) : IEgressGate
    {
        private bool _changed;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            if (!_changed)
            { inputs.Add("second"); _changed = true; }
            return ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
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

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class LiveWorkerFactAttribute : FactAttribute
    {
        public LiveWorkerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OC_TEST_KNOWLEDGE_ENDPOINT")))
                Skip = "Set OC_TEST_KNOWLEDGE_ENDPOINT and test credentials to enable the live worker test.";
        }
    }
}
