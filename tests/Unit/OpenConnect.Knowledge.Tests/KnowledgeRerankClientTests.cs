using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.Knowledge.Tests;

public sealed class KnowledgeRerankClientTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)0xab, 32).ToArray();
    private static EgressContext Context() => new(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);

    [Fact]
    public async Task EncodedSensitiveValueSplitAcrossCandidatesNeverReachesWorker()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        var firewall = new PrivacyFirewall(Microsoft.Extensions.Options.Options.Create(new PrivacyOptions()),
            new OpenConnect.Privacy.Policy.PolicyEngine(new OpenConnect.Privacy.Rules.RulesSecurityDetector()));
        using var client = new KnowledgeRerankClient(http, new OpenConnect.Privacy.Gates.EgressGate(firewall),
            new Uri("https://worker.test"), "token", Key, (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.RerankAsync(Context(), "query",
            [new("a", "alice%40"), new("b", "example.com")]));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorizesSnapshotAndRejectsForgedCandidate(bool forged)
    {
        var candidates = new List<KnowledgeRerankCandidate> { new("a", "first"), new("b", "second") };
        var handler = new Handler(async request =>
        {
            var body = await request.Content!.ReadAsByteArrayAsync();
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(Key, body)).ToLowerInvariant(), request.Headers.GetValues("X-OpenConnect-Signature").Single());
            using var command = JsonDocument.Parse(body);
            Assert.Equal("a", command.RootElement.GetProperty("payload").GetProperty("candidates")[0].GetProperty("id").GetString());
            var taskId = command.RootElement.GetProperty("taskId").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    schemaVersion = "openconnect.v1",
                    taskId,
                    kind = "knowledge.rerank",
                    result = new
                    {
                        model_id = "model",
                        fingerprint = new string('a', 64),
                        score_type = "raw_logit",
                        candidates = new[] { new { id = "a", score = 1.0 }, new { id = forged ? "other" : "b", score = 0.5 } }
                    }
                }))
            };
        });
        using var http = new HttpClient(handler);
        using var client = new KnowledgeRerankClient(http, new Gate(), new Uri("https://worker.test"), "token", Key,
            (_, authorized, _) => { Assert.Equal("a", authorized[0].Id); candidates[0] = new("other", "changed"); return ValueTask.FromResult(true); });
        if (forged)
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.RerankAsync(Context(), "query", candidates));
        else
        { using var result = await client.RerankAsync(Context(), "query", candidates); Assert.Equal(2, result.RootElement.GetProperty("result").GetProperty("candidates").GetArrayLength()); }
    }

    [Fact]
    public async Task DeniedAuthorizationAndPrivacyDoNotDispatch()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var denied = new KnowledgeRerankClient(http, new Gate(), new Uri("https://worker.test"), "token", Key,
            (_, _, _) => ValueTask.FromResult(false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.RerankAsync(Context(), "query", [new("a", "text")]));
        using var blocked = new KnowledgeRerankClient(http, new Gate(block: true), new Uri("https://worker.test"), "token", Key,
            (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => blocked.RerankAsync(Context(), "query", [new("a", "text")]));
        using var conflicting = new KnowledgeRerankClient(http, new ContradictoryGate(), new Uri("https://worker.test"), "token", Key,
            (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => conflicting.RerankAsync(Context(), "query", [new("a", "text")]));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RejectsTiesReturnedInDifferentInputOrder()
    {
        var handler = new Handler(async request =>
        {
            using var command = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    schemaVersion = "openconnect.v1",
                    taskId = command.RootElement.GetProperty("taskId").GetString(),
                    kind = "knowledge.rerank",
                    result = new
                    {
                        model_id = "model",
                        fingerprint = new string('a', 64),
                        score_type = "raw_logit",
                        candidates = new[] { new { id = "b", score = 0.5 }, new { id = "a", score = 0.5 } }
                    }
                }))
            };
        });
        using var http = new HttpClient(handler);
        using var client = new KnowledgeRerankClient(http, new Gate(), new Uri("https://worker.test"), "token", Key,
            (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RerankAsync(Context(), "query", [new("a", "first"), new("b", "second")]));
    }

    [Fact]
    public async Task RejectsOversizedTotalBeforeDispatch()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = new KnowledgeRerankClient(http, new Gate(), new Uri("https://worker.test"), "token", Key,
            (_, _, _) => ValueTask.FromResult(true));
        var candidates = Enumerable.Range(0, 9).Select(index => new KnowledgeRerankCandidate($"id-{index}", new string('x', 8192))).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => client.RerankAsync(Context(), "query", candidates));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("invalid", false)]
    public async Task SharedResponseFixturesMatchClient(string folder, bool valid)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenConnect.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var fixture = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "contracts", "fixtures", folder,
            "knowledge-rerank-response.json"));
        var handler = new Handler(async request =>
        {
            using var command = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            var response = JsonNode.Parse(fixture)!;
            response["taskId"] = command.RootElement.GetProperty("taskId").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
        });
        using var http = new HttpClient(handler);
        using var client = new KnowledgeRerankClient(http, new Gate(), new Uri("https://worker.test"), "token", Key,
            (_, _, _) => ValueTask.FromResult(true));
        if (valid)
        { using var result = await client.RerankAsync(Context(), "query", [new("one", "text")]); Assert.Equal("knowledge.rerank", result.RootElement.GetProperty("kind").GetString()); }
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.RerankAsync(Context(), "query", [new("one", "text")]));
    }

    [Fact]
    public async Task SensitiveOutputMetadataIsBlocked()
    {
        var handler = new Handler(async request =>
        {
            using var command = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    schemaVersion = "openconnect.v1",
                    taskId = command.RootElement.GetProperty("taskId").GetString(),
                    kind = "knowledge.rerank",
                    result = new
                    {
                        model_id = "person@example.test",
                        fingerprint = new string('a', 64),
                        score_type = "raw_logit",
                        candidates = new[] { new { id = "one", score = 0.5 } }
                    }
                }))
            };
        });
        using var http = new HttpClient(handler);
        using var client = new KnowledgeRerankClient(http, new OutputGate(), new Uri("https://worker.test"), "token", Key,
            (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.RerankAsync(Context(), "query", [new("one", "text")]));
    }

    private sealed class OutputGate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, context.Destination == EgressDestination.Output && content.Contains('@') ? "[MASKED]" : content,
                [], SecurityPolicyAction.Allow, "test"));
    }

    private sealed class ContradictoryGate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Block, "test"));
    }

    private sealed class Gate(bool block = false) : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(!block, block ? null : content, [], block ? SecurityPolicyAction.Block : SecurityPolicyAction.Allow, "test"));
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return send(request); }
    }
}
