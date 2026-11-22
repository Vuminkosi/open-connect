using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.Knowledge.Tests;

public sealed class KnowledgeIndexClientTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)0xab, 32).ToArray();
    private static EgressContext Context() => new(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
        new SessionId(Ulid.NewUlid()), EgressDestination.Worker);

    [Fact]
    public void SubmissionValidatesTheCopiedAclRatherThanReenumeratingCallerData()
    {
        var principals = new ChangingAcl();
        var roles = new ChangingAcl();
        var submission = new KnowledgeIndexSubmission(Guid.NewGuid(), "app", "collection", "doc", "v1",
            new string('a', 64), new string('b', 64), new string('c', 64), 42, "version", 0, principals, roles);
        Assert.Equal("alice", Assert.Single(submission.Principals));
        Assert.Equal("alice", Assert.Single(submission.Roles));
        Assert.Equal(1, principals.Enumerations);
        Assert.Equal(1, roles.Enumerations);
    }

    [Fact]
    public void SubmissionRejectsMutableNullObjectVersion()
    {
        Assert.Throws<ArgumentException>(() => new KnowledgeIndexSubmission(Guid.NewGuid(), "app", "collection", "doc", "v1",
            new string('a', 64), new string('b', 64), new string('c', 64), 42, "null", 0, ["alice"], []));
    }

    private sealed class ChangingAcl : IReadOnlyList<string>
    {
        public int Enumerations { get; private set; }
        public int Count => 1;
        public string this[int index] => index == 0 ? "alice" : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<string> GetEnumerator()
        {
            Enumerations++;
            yield return Enumerations == 1 ? "alice" : "\u0001invalid";
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task SubmissionAuthorizesImmutableReferenceAndSignsTenantBoundMetadata()
    {
        var context = Context();
        var principals = new List<string> { "alice" };
        var submission = Submission(principals);
        principals[0] = "bob";
        var authorized = false;
        var handler = new Handler(async request =>
        {
            Assert.True(authorized);
            var body = await request.Content!.ReadAsByteArrayAsync();
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(Key, body)).ToLowerInvariant(),
                request.Headers.GetValues("X-OpenConnect-Signature").Single());
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            Assert.Equal(context.Tenant.Value.ToString(), root.GetProperty("tenantId").GetString());
            Assert.Equal("alice", root.GetProperty("payload").GetProperty("principals")[0].GetString());
            Assert.Equal(submission.ObjectId, root.GetProperty("payload").GetProperty("objectId").GetString());
            Assert.False(root.GetProperty("payload").TryGetProperty("content", out _));
            return Receipt(root.GetProperty("taskId").GetString()!, submission.JobId);
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, (scope, request, _) =>
        {
            Assert.Equal(context, scope); Assert.Same(submission, request);
            Assert.Equal("alice", request.Principals[0]); authorized = true;
            return ValueTask.FromResult(true);
        });
        using var result = await client.SubmitAsync(context, submission);
        Assert.True(result.RootElement.GetProperty("result").GetProperty("created").GetBoolean());
    }

    [Fact]
    public async Task DeniedSubmissionAndStatusNeverReachWorker()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = Client(http, (_, _, _) => ValueTask.FromResult(false),
            (_, _, _, _, _, _) => ValueTask.FromResult(false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.SubmitAsync(Context(), Submission(["alice"])));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.StatusAsync(Context(), Guid.NewGuid(), "app", "collection", "doc", []));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task PrivacyDenialPreventsIndexMetadataFromReachingWorker()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = new KnowledgeIndexClient(http, new Gate(EgressDestination.Worker), new Uri("https://worker.test"),
            "token", Key, (_, _, _) => ValueTask.FromResult(true), (_, _, _, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.SubmitAsync(Context(), Submission(["alice"])));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RulesFirewallBlocksSensitiveIndexMetadataAndRecordsContentFreeEvidence()
    {
        var sink = new EvidenceSink();
        var firewall = new PrivacyFirewall(Microsoft.Extensions.Options.Options.Create(new PrivacyOptions()),
            new OpenConnect.Privacy.Policy.PolicyEngine(new OpenConnect.Privacy.Rules.RulesSecurityDetector()));
        var gate = new OpenConnect.Privacy.Gates.EgressGate(new EvidenceRecordingPrivacyFirewall(firewall, sink));
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = new KnowledgeIndexClient(http, gate, new Uri("https://worker.test"), "token", Key,
            (_, _, _) => ValueTask.FromResult(true), (_, _, _, _, _, _) => ValueTask.FromResult(true));
        var submission = Submission(["alice@example.com"]);

        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.SubmitAsync(Context(), submission));
        Assert.Equal(0, handler.Calls);
        var evidence = Assert.Single(sink.Events);
        Assert.False(evidence.Allowed);
        Assert.Equal(SecurityPolicyAction.Block, evidence.Action);
        Assert.Equal(1, evidence.Categories["EMAIL"]);
        Assert.DoesNotContain("alice@example.com", JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrivacyDenialPreventsIndexReceiptFromReachingCaller()
    {
        var submission = Submission(["alice"]);
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Receipt(json.RootElement.GetProperty("taskId").GetString()!, submission.JobId);
        });
        using var http = new HttpClient(handler);
        using var client = new KnowledgeIndexClient(http, new Gate(EgressDestination.Output), new Uri("https://worker.test"),
            "token", Key, (_, _, _) => ValueTask.FromResult(true), (_, _, _, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.SubmitAsync(Context(), submission));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task StatusBindsPrincipalAndValidatesCompletedReceipt()
    {
        var context = Context();
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            Assert.Equal(context.Principal.Value.ToString(), json.RootElement.GetProperty("payload").GetProperty("principalId").GetString());
            var task = json.RootElement.GetProperty("taskId").GetString()!;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                schemaVersion = "openconnect.v1", taskId = task, kind = "knowledge.index.status",
                result = new { state = "completed", attempts = 1, result_generation = 1, retry_at = (string?)null }
            }), Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, (_, _, _) => ValueTask.FromResult(true));
        using var status = await client.StatusAsync(context, Guid.NewGuid(), "app", "collection", "doc", ["reader"]);
        Assert.Equal("completed", status.RootElement.GetProperty("result").GetProperty("state").GetString());
    }

    [Fact]
    public async Task ReceiptForDifferentJobIsRejected()
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Receipt(json.RootElement.GetProperty("taskId").GetString()!, Guid.NewGuid());
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SubmitAsync(Context(), Submission(["alice"])));
    }

    [Fact]
    public async Task DeleteRequiresSeparateAuthorizationAndSignsGenerationFence()
    {
        var context = Context();
        var checkedDeletion = false;
        var handler = new Handler(async request =>
        {
            Assert.True(checkedDeletion);
            var body = await request.Content!.ReadAsByteArrayAsync();
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(Key, body)).ToLowerInvariant(),
                request.Headers.GetValues("X-OpenConnect-Signature").Single());
            using var json = JsonDocument.Parse(body);
            Assert.Equal(context.Tenant.Value.ToString(), json.RootElement.GetProperty("tenantId").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("payload").GetProperty("expectedGeneration").GetInt64());
            return DeleteReceipt(json.RootElement.GetProperty("taskId").GetString()!, 2);
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, (_, _, _) => ValueTask.FromResult(true));
        using var result = await client.DeleteAsync(context, "app", "collection", "doc", 1,
            (scope, app, collection, doc, generation, _) =>
            {
                Assert.Equal(context, scope); Assert.Equal("app", app); Assert.Equal("collection", collection);
                Assert.Equal("doc", doc); Assert.Equal(1, generation); checkedDeletion = true;
                return ValueTask.FromResult(true);
            });
        Assert.Equal("pending", result.RootElement.GetProperty("result").GetProperty("reconciliation_state").GetString());
    }

    [Fact]
    public async Task DeleteDenialDoesNotSendAndMismatchedReceiptFailsClosed()
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return DeleteReceipt(json.RootElement.GetProperty("taskId").GetString()!, 42);
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.DeleteAsync(Context(), "app", "collection", "doc", 1,
            (_, _, _, _, _, _) => ValueTask.FromResult(false)));
        Assert.Equal(0, handler.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteAsync(Context(), "app", "collection", "doc", 1,
            (_, _, _, _, _, _) => ValueTask.FromResult(true)));
    }

    [LiveIndexFact]
    public async Task AuthorizedClientSubmitsAndObservesLiveIndexJob()
    {
        var context = new EgressContext(new TenantId(Ulid.Parse(Environment.GetEnvironmentVariable("OC_TEST_INDEX_TENANT")!)),
            new PrincipalId(Ulid.Parse(Environment.GetEnvironmentVariable("OC_TEST_INDEX_PRINCIPAL")!)),
            new SessionId(Ulid.NewUlid()), EgressDestination.Worker);
        var submission = new KnowledgeIndexSubmission(Guid.Parse(Environment.GetEnvironmentVariable("OC_TEST_INDEX_JOB")!),
            "app", "collection", "doc", "v1", Environment.GetEnvironmentVariable("OC_TEST_INDEX_SPACE")!,
            Environment.GetEnvironmentVariable("OC_TEST_INDEX_OBJECT")!, Environment.GetEnvironmentVariable("OC_TEST_INDEX_SHA")!,
            int.Parse(Environment.GetEnvironmentVariable("OC_TEST_INDEX_SIZE")!, System.Globalization.CultureInfo.InvariantCulture),
            Environment.GetEnvironmentVariable("OC_TEST_INDEX_VERSION")!, 0, [context.Principal.Value.ToString()], []);
        using var client = new KnowledgeIndexClient(new Gate(), new Uri(Environment.GetEnvironmentVariable("OC_TEST_INDEX_ENDPOINT")!),
            Environment.GetEnvironmentVariable("OC_TEST_INDEX_TOKEN")!, Convert.FromHexString(Environment.GetEnvironmentVariable("OC_TEST_INDEX_KEY")!),
            (scope, item, _) => ValueTask.FromResult(scope == context && ReferenceEquals(item, submission)),
            (scope, app, collection, doc, roles, _) => ValueTask.FromResult(scope == context && app == "app" &&
                collection == "collection" && doc == "doc" && roles.Count == 0), allowInsecureHttp: true);
        using var receipt = await client.SubmitAsync(context, submission);
        Assert.True(receipt.RootElement.GetProperty("result").GetProperty("created").GetBoolean());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            using var status = await client.StatusAsync(context, submission.JobId, "app", "collection", "doc", [], timeout.Token);
            var result = status.RootElement.GetProperty("result");
            var state = result.GetProperty("state").GetString();
            if (state == "completed")
            {
                Assert.Equal(1, result.GetProperty("result_generation").GetInt64());
                break;
            }
            Assert.NotEqual("dead", state);
            await Task.Delay(500, timeout.Token);
        }
        using var deletion = await client.DeleteAsync(context, "app", "collection", "doc", 1,
            (scope, app, collection, doc, generation, _) => ValueTask.FromResult(scope == context && app == "app" &&
                collection == "collection" && doc == "doc" && generation == 1), timeout.Token);
        Assert.Equal(2, deletion.RootElement.GetProperty("result").GetProperty("generation").GetInt64());
        Assert.Equal("pending", deletion.RootElement.GetProperty("result").GetProperty("reconciliation_state").GetString());
        var hidden = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.StatusAsync(context, submission.JobId, "app", "collection", "doc", [], timeout.Token));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    private static KnowledgeIndexSubmission Submission(IReadOnlyList<string> principals) => new(Guid.NewGuid(),
        "app", "collection", "doc", "v1", new string('a', 64), new string('b', 64), new string('c', 64),
        42, "version", 0, principals, []);

    private static KnowledgeIndexClient Client(HttpClient http,
        Func<EgressContext, KnowledgeIndexSubmission, CancellationToken, ValueTask<bool>> submit,
        Func<EgressContext, string, string, string, IReadOnlyList<string>, CancellationToken, ValueTask<bool>>? status = null) =>
        new(http, new Gate(), new Uri("https://worker.test"), "token", Key, submit, status ?? ((_, _, _, _, _, _) => ValueTask.FromResult(true)));

    private sealed class Gate(EgressDestination? denied = null) : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(context.Destination == denied
                ? new EgressDecision(false, null, [], SecurityPolicyAction.Block, "test")
                : new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
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

    private static HttpResponseMessage Receipt(string taskId, Guid jobId) => new(HttpStatusCode.Accepted)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { schemaVersion = "openconnect.v1", taskId,
            kind = "knowledge.index", result = new { job_id = jobId, created = true } }), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage DeleteReceipt(string taskId, long generation) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { schemaVersion = "openconnect.v1", taskId,
            kind = "knowledge.index.delete", result = new { generation, reconciliation_state = "pending" } }),
            Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request);
        }
    }

    private sealed class LiveIndexFactAttribute : FactAttribute
    {
        public LiveIndexFactAttribute()
        {
            string[] names = ["OC_TEST_INDEX_ENDPOINT", "OC_TEST_INDEX_TOKEN", "OC_TEST_INDEX_KEY", "OC_TEST_INDEX_TENANT",
                "OC_TEST_INDEX_PRINCIPAL", "OC_TEST_INDEX_JOB", "OC_TEST_INDEX_SPACE", "OC_TEST_INDEX_OBJECT", "OC_TEST_INDEX_SHA",
                "OC_TEST_INDEX_SIZE", "OC_TEST_INDEX_VERSION"];
            if (names.Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
                Skip = "Set the disposable indexing endpoint, credentials and fixture object to enable this test.";
        }
    }
}
