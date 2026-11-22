using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.Knowledge.Tests;

public sealed class KnowledgeSearchClientTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)0xab, 32).ToArray();
    private static EgressContext Context() => new(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
        new SessionId(Ulid.NewUlid()), EgressDestination.Worker);

    [Fact]
    public async Task EncodedSensitiveQueryNeverReachesSearchWorker()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        var firewall = new PrivacyFirewall(Microsoft.Extensions.Options.Options.Create(new PrivacyOptions()),
            new OpenConnect.Privacy.Policy.PolicyEngine(new OpenConnect.Privacy.Rules.RulesSecurityDetector()));
        using var client = new KnowledgeSearchClient(http, new OpenConnect.Privacy.Gates.EgressGate(firewall),
            new Uri("https://worker.test"), "token", Key, (_, _, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.SearchAsync(Context(), "app", "collection", [], "alice%40example.com"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AuthorizesScopeAndSignsSanitizedPrincipalBoundRequest()
    {
        var context = Context();
        var checkedScope = false;
        var handler = new Handler(async request =>
        {
            Assert.True(checkedScope);
            var body = await request.Content!.ReadAsByteArrayAsync();
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(Key, body)).ToLowerInvariant(),
                request.Headers.GetValues("X-OpenConnect-Signature").Single());
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            Assert.Equal(context.Tenant.Value.ToString(), root.GetProperty("tenantId").GetString());
            Assert.Equal(context.Principal.Value.ToString(), root.GetProperty("payload").GetProperty("principalId").GetString());
            Assert.Equal("safe", root.GetProperty("payload").GetProperty("query").GetString());
            return Reply(root.GetProperty("taskId").GetString()!);
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, true, (scope, app, collection, roles, _) =>
        {
            Assert.Equal(context, scope);
            Assert.Equal("app", app);
            Assert.Equal("collection", collection);
            Assert.Equal(["reader"], roles);
            checkedScope = true;
            return ValueTask.FromResult(true);
        });
        using var result = await client.SearchAsync(context, "app", "collection", ["reader"], "private query");
        Assert.Equal("doc", result.RootElement.GetProperty("result").GetProperty("hits")[0].GetProperty("hit")
            .GetProperty("document_id").GetString());
    }

    [Fact]
    public async Task DeniedAuthorizationAndPrivacyNeverSendData()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var denied = Client(http, true, (_, _, _, _, _) => ValueTask.FromResult(false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.SearchAsync(Context(), "app", "collection", [], "secret"));
        using var privateClient = Client(http, false, (_, _, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => privateClient.SearchAsync(Context(), "app", "collection", [], "secret"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DetectedButUnmaskedQueryNeverReachesSearchWorker()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = new KnowledgeSearchClient(http, new UnmaskedQueryGate(), new Uri("https://worker.test"),
            "token", Key, (_, _, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.SearchAsync(Context(), "app", "collection", [], "secret"));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class UnmaskedQueryGate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, content == "secret" ? [new(0, 6, "SECRET")] : [],
                SecurityPolicyAction.Allow, "test"));
    }

    [Fact]
    public async Task RoleSnapshotRemainsBoundToTheAuthorizedScope()
    {
        var roles = new List<string> { "reader" };
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            Assert.Equal("reader", json.RootElement.GetProperty("payload").GetProperty("roleIds")[0].GetString());
            return Reply(json.RootElement.GetProperty("taskId").GetString()!);
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, true, (_, _, _, authorizedRoles, _) =>
        {
            Assert.Equal("reader", authorizedRoles[0]);
            roles[0] = "administrator";
            return ValueTask.FromResult(true);
        });
        using var result = await client.SearchAsync(Context(), "app", "collection", roles, "query");
        Assert.Equal("knowledge.search", result.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ValidatesTheSameRoleSnapshotUsedForAuthorizationAndDispatch()
    {
        var roles = new ChangingRoles();
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            Assert.Equal("reader", json.RootElement.GetProperty("payload").GetProperty("roleIds")[0].GetString());
            return Reply(json.RootElement.GetProperty("taskId").GetString()!);
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, true, (_, _, _, authorizedRoles, _) =>
        {
            Assert.Equal("reader", Assert.Single(authorizedRoles));
            return ValueTask.FromResult(true);
        });
        using var result = await client.SearchAsync(Context(), "app", "collection", roles, "query");
        Assert.Equal(1, roles.Enumerations);
    }

    private sealed class ChangingRoles : IReadOnlyList<string>
    {
        public int Enumerations { get; private set; }
        public int Count => 1;
        public string this[int index] => index == 0 ? "reader" : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<string> GetEnumerator()
        {
            Enumerations++;
            yield return Enumerations == 1 ? "reader" : "\u0001invalid";
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task HybridEmbedsOnlyTheSanitizedQueryAndBindsModelSpace()
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            var payload = json.RootElement.GetProperty("payload");
            Assert.Equal("safe", payload.GetProperty("query").GetString());
            var hybrid = payload.GetProperty("hybrid");
            Assert.Equal("fixture", hybrid.GetProperty("modelId").GetString());
            Assert.Equal(new string('a', 64), hybrid.GetProperty("embeddingSpaceId").GetString());
            Assert.Equal(2, hybrid.GetProperty("vector").GetArrayLength());
            return Reply(json.RootElement.GetProperty("taskId").GetString()!);
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, true, (_, _, _, _, _) => ValueTask.FromResult(true));
        var called = false;
        using var result = await client.SearchHybridAsync(Context(), "app", "collection", [], "private query",
            (_, sanitized, _) =>
            {
                called = true;
                Assert.Equal("safe", sanitized);
                return Task.FromResult(Embedding());
            });
        Assert.True(called);
        Assert.Equal("knowledge.search", result.RootElement.GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("norm")]
    [InlineData("fingerprint")]
    public async Task InvalidHybridEmbeddingNeverReachesWorker(string mutation)
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = Client(http, true, (_, _, _, _, _) => ValueTask.FromResult(true));
        var item = mutation == "truncated" ? new KnowledgeEmbedding([0.6f, 0.8f], true, 1) :
            mutation == "norm" ? new KnowledgeEmbedding([0, 0], false, 1) : new KnowledgeEmbedding([0.6f, 0.8f], false, 1);
        var embedding = new KnowledgeEmbeddingResult([item], "fixture", mutation == "fingerprint" ? "bad" : new string('b', 64),
            2, new string('a', 64));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchHybridAsync(Context(), "app", "collection", [],
            "query", (_, _, _) => Task.FromResult(embedding)));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task PrivacyDenialDoesNotInvokeHybridProvider()
    {
        var called = false;
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = Client(http, false, (_, _, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.SearchHybridAsync(Context(), "app", "collection", [],
            "secret", (_, _, _) => { called = true; return Task.FromResult(Embedding()); }));
        Assert.False(called);
        Assert.Equal(0, handler.Calls);
    }

    private static KnowledgeEmbeddingResult Embedding() => new([new KnowledgeEmbedding([0.6f, 0.8f], false, 1)],
        "fixture", new string('b', 64), 2, new string('a', 64));

    [Fact]
    public async Task Blocks_sensitive_scope_metadata_before_sending()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = new KnowledgeSearchClient(http, new SensitiveScopeGate(), new Uri("https://worker.test"), "token", Key,
            (_, _, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.SearchAsync(Context(), "person@example.com", "collection", [], "query"));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.SearchAsync(Context(), "app", "collection", ["person@example.com"], "query"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Blocks_sensitive_search_hit_even_when_json_escapes_it()
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Reply(json.RootElement.GetProperty("taskId").GetString()!, end: 18, text: "person@example.com", escape: true);
        });
        using var http = new HttpClient(handler);
        using var client = new KnowledgeSearchClient(http, new OutputEmailGate(), new Uri("https://worker.test"), "token", Key,
            (_, _, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.SearchAsync(Context(), "app", "collection", [], "query"));
    }

    [Fact]
    public async Task InvalidCitationIsRejected()
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Reply(json.RootElement.GetProperty("taskId").GetString()!, end: 9);
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, true, (_, _, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync(Context(), "app", "collection", [], "query"));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task CitationOffsetsUseUnicodeScalarsRatherThanUtf16Units(int end, bool valid)
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Reply(json.RootElement.GetProperty("taskId").GetString()!, end: end, text: "\U0001F600x");
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, true, (_, _, _, _, _) => ValueTask.FromResult(true));
        if (!valid)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync(Context(), "app", "collection", [], "query"));
            return;
        }
        using var response = await client.SearchAsync(Context(), "app", "collection", [], "query");
        var hit = response.RootElement.GetProperty("result").GetProperty("hits")[0].GetProperty("hit");
        Assert.Equal("\U0001F600x", hit.GetProperty("text").GetString());
        Assert.Equal(2, hit.GetProperty("end").GetInt32());
    }

    [LiveSearchFact]
    public async Task AuthorizedClientSearchesLiveWorker()
    {
        var context = new EgressContext(new TenantId(Ulid.Parse(Environment.GetEnvironmentVariable("OC_TEST_SEARCH_TENANT")!)),
            new PrincipalId(Ulid.Parse(Environment.GetEnvironmentVariable("OC_TEST_SEARCH_PRINCIPAL")!)),
            new SessionId(Ulid.NewUlid()), EgressDestination.Worker);
        var count = 0;
        using var client = new KnowledgeSearchClient(new PassGate(),
            new Uri(Environment.GetEnvironmentVariable("OC_TEST_SEARCH_ENDPOINT")!),
            Environment.GetEnvironmentVariable("OC_TEST_SEARCH_TOKEN")!,
            Convert.FromHexString(Environment.GetEnvironmentVariable("OC_TEST_SEARCH_KEY")!),
            (scope, app, collection, roles, _) =>
            {
                count++;
                return ValueTask.FromResult(scope == context && app == "app" && collection == "collection" && roles.Count == 0);
            }, allowInsecureHttp: true);
        using var result = await client.SearchAsync(context, "app", "collection", [], "planet");
        var hits = result.RootElement.GetProperty("result").GetProperty("hits");
        Assert.Single(hits.EnumerateArray());
        Assert.Equal("visible", hits[0].GetProperty("hit").GetProperty("document_id").GetString());
        using var hybrid = await client.SearchHybridAsync(context, "app", "collection", [], "planet",
            (_, sanitized, _) =>
            {
                Assert.Equal("planet", sanitized);
                return Task.FromResult(Embedding());
            });
        var hybridHits = hybrid.RootElement.GetProperty("result").GetProperty("hits");
        Assert.Single(hybridHits.EnumerateArray());
        Assert.Equal("visible", hybridHits[0].GetProperty("hit").GetProperty("document_id").GetString());
        Assert.Equal(2, count);
    }

    private static KnowledgeSearchClient Client(HttpClient http, bool allow,
        Func<EgressContext, string, string, IReadOnlyList<string>, CancellationToken, ValueTask<bool>> authorize) =>
        new(http, new Gate(allow), new Uri("https://worker.test"), "token", Key, authorize);

    private static HttpResponseMessage Reply(string taskId, int end = 4, string text = "Mars", bool escape = false) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            schemaVersion = "openconnect.v1",
            taskId,
            kind = "knowledge.search",
            result = new
            {
                model_id = "model",
                fingerprint = new string('a', 64),
                score_type = "raw_logit",
                warnings = Array.Empty<string>(),
                hits = new[] { new { rerank_score = 1.0, hit = new
                {
                    document_id = "doc", source_revision = "v1", chunk_id = new string('b', 64), page_id = "page",
                    page_index = 0, start = 0, end, text, score = 0.5
                } } }
            }
        }).Replace("@", escape ? "\\u0040" : "@", StringComparison.Ordinal), Encoding.UTF8, "application/json")
    };

    private sealed class Gate(bool allowed) : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(allowed, allowed ? context.Destination == EgressDestination.Worker &&
                content is "private query" or "query" or "secret" ? "safe" : content : null, Array.Empty<EntitySpan>(),
                allowed ? SecurityPolicyAction.Allow : SecurityPolicyAction.Block, "test"));
    }

    private sealed class SensitiveScopeGate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content.Contains('@') ? "[MASKED]" : content,
                Array.Empty<EntitySpan>(), SecurityPolicyAction.Allow, "test"));
    }

    private sealed class OutputEmailGate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, context.Destination == EgressDestination.Output && content.Contains('@') ? "[MASKED]" : content,
                Array.Empty<EntitySpan>(), SecurityPolicyAction.Allow, "test"));
    }

    private sealed class PassGate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, Array.Empty<EntitySpan>(), SecurityPolicyAction.Allow, "test"));
    }

    private sealed class LiveSearchFactAttribute : FactAttribute
    {
        public LiveSearchFactAttribute()
        {
            string[] names = ["OC_TEST_SEARCH_ENDPOINT", "OC_TEST_SEARCH_TOKEN", "OC_TEST_SEARCH_KEY",
                "OC_TEST_SEARCH_TENANT", "OC_TEST_SEARCH_PRINCIPAL"];
            if (names.Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
                Skip = "Set the disposable search endpoint, credentials and fixture identities to enable this test.";
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
