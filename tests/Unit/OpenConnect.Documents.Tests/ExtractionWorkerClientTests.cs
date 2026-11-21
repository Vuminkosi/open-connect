using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.Documents.Tests;

public sealed class ExtractionWorkerClientTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)0xab, 32).ToArray();
    private const string Schema = """{"type":"object","additionalProperties":false,"required":["total"],"properties":{"total":{"type":"number"}}}""";
    private static EgressContext Context => new(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);

    [Fact]
    public async Task EncodedSensitiveDocumentNeverReachesExtractionWorker()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var schema = JsonDocument.Parse(Schema);
        var firewall = new PrivacyFirewall(Microsoft.Extensions.Options.Options.Create(new PrivacyOptions()),
            new OpenConnect.Privacy.Policy.PolicyEngine(new OpenConnect.Privacy.Rules.RulesSecurityDetector()));
        using var client = new ExtractionWorkerClient(http, "token", Key, firewall, (_, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.ExtractAsync(Context, "alice%40example.com", schema.RootElement));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void Network_endpoint_requires_tls_or_explicit_private_network_opt_in()
    {
        var firewall = new Firewall();
        static ValueTask<bool> Authorize(EgressContext _, string __, JsonElement ___, CancellationToken ____) => ValueTask.FromResult(true);
        Assert.Throws<ArgumentException>(() => new ExtractionWorkerClient(new Uri("http://worker-inference:8080"), "token", Key, firewall, Authorize));
        Assert.Throws<ArgumentException>(() => new ExtractionWorkerClient(new Uri("https://user:password@worker.test"), "token", Key, firewall, Authorize));
        using var allowed = new ExtractionWorkerClient(new Uri("http://worker-inference:8080"), "token", Key, firewall, Authorize, allowInsecureHttp: true);
    }

    [Fact]
    public async Task Authorized_request_is_signed_and_citations_are_validated()
    {
        var context = Context;
        var handler = new Handler(async request =>
        {
            Assert.Equal("/internal/v1/extract", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsByteArrayAsync();
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(Key, body)).ToLowerInvariant(),
                request.Headers.GetValues("X-OpenConnect-Signature").Single());
            using var json = JsonDocument.Parse(body);
            Assert.Equal(context.Tenant.Value.ToString(), json.RootElement.GetProperty("tenantId").GetString());
            var taskId = json.RootElement.GetProperty("taskId").GetString();
            return Reply(taskId!, "42");
        });
        using var http = new HttpClient(handler);
        using var client = new ExtractionWorkerClient(http, "token", Key, new Firewall(),
            (scope, text, _, _) => ValueTask.FromResult(scope == context && text == "Total: 42"));
        using var schema = JsonDocument.Parse(Schema);
        using var result = await client.ExtractAsync(context, "Total: 42", schema.RootElement);
        Assert.Equal(42, result.RootElement.GetProperty("result").GetProperty("fields").GetProperty("total").GetProperty("value").GetInt32());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Rejected_authorization_or_privacy_never_reaches_worker()
    {
        var handler = new Handler(_ => throw new InvalidOperationException());
        using var http = new HttpClient(handler);
        using var schema = JsonDocument.Parse(Schema);
        using var denied = new ExtractionWorkerClient(http, "token", Key, new Firewall(), (_, _, _, _) => ValueTask.FromResult(false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.ExtractAsync(Context, "Total: 42", schema.RootElement));
        using var masked = new ExtractionWorkerClient(http, "token", Key, new Firewall(mask: true), (_, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => masked.ExtractAsync(Context, "Total: 42", schema.RootElement));
        using var detected = new ExtractionWorkerClient(http, "token", Key, new DetectedFirewall(), (_, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => detected.ExtractAsync(Context, "Total: 42", schema.RootElement));
        using var conflicting = new ExtractionWorkerClient(http, "token", Key, new ConflictingFirewall(), (_, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => conflicting.ExtractAsync(Context, "Total: 42", schema.RootElement));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Disabled_decision_prevents_extraction_dispatch()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var schema = JsonDocument.Parse(Schema);
        using var client = new ExtractionWorkerClient(http, "token", Key, new DisabledDecisionFirewall(), (_, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.ExtractAsync(Context, "Total: 42", schema.RootElement));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CancellationDuringAuthorizationPreventsDispatch()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var schema = JsonDocument.Parse(Schema);
        using var cancellation = new CancellationTokenSource();
        using var client = new ExtractionWorkerClient(http, "token", Key, new Firewall(), (_, _, _, token) =>
        {
            Assert.True(token.CanBeCanceled);
            cancellation.Cancel();
            return ValueTask.FromResult(true);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ExtractAsync(Context, "Total: 42", schema.RootElement, cancellation.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DisabledRecordedFirewallBlocksAndReportsUnavailableEnforcement()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        var sink = new EvidenceSink();
        using var http = new HttpClient(handler);
        using var schema = JsonDocument.Parse(Schema);
        using var client = new ExtractionWorkerClient(http, "token", Key,
            new EvidenceRecordingPrivacyFirewall(new DisabledFirewall(), sink), (_, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.ExtractAsync(Context, "Total: 42", schema.RootElement));
        Assert.Equal(0, handler.Calls);
        Assert.Equal("enforcement_unavailable", Assert.Single(sink.Events).ReasonCode);
    }

    [Fact]
    public async Task Forged_value_is_rejected_without_returning_content()
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Reply(json.RootElement.GetProperty("taskId").GetString()!, "43");
        });
        using var http = new HttpClient(handler);
        using var schema = JsonDocument.Parse(Schema);
        using var client = new ExtractionWorkerClient(http, "token", Key, new Firewall(), (_, _, _, _) => ValueTask.FromResult(true));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExtractAsync(Context, "Total: 42", schema.RootElement));
        Assert.DoesNotContain("Total: 42", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputInspectionReceivesTheRequestDeadline()
    {
        var firewall = new DeadlineFirewall();
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Reply(json.RootElement.GetProperty("taskId").GetString()!, "42");
        });
        using var http = new HttpClient(handler);
        using var schema = JsonDocument.Parse(Schema);
        using var client = new ExtractionWorkerClient(http, "token", Key, firewall, (_, _, _, _) => ValueTask.FromResult(true));
        using var result = await client.ExtractAsync(Context, "Total: 42", schema.RootElement);
        Assert.True(firewall.OutputWasBounded);
    }

    private static HttpResponseMessage Reply(string taskId, string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { schemaVersion = "openconnect.v1", taskId,
            kind = "documents.extract", result = new { fields = new { total = new { value = int.Parse(value), start = 7, end = 9 } },
                warnings = new[] { "source_support_not_semantic_validation" }, schema_valid = true, confidence_method = "not_estimated" } }), Encoding.UTF8, "application/json")
    };

    private sealed class Firewall(bool mask = false) : IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, mask ? "[MASKED]" : content, [], SecurityPolicyAction.Allow, "test"));
    }

    private sealed class ConflictingFirewall : IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Block, "test"));
    }

    private sealed class DetectedFirewall : IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [new(0, 1, "PERSONAL_DATA")], SecurityPolicyAction.Allow, "test"));
    }

    private sealed class DeadlineFirewall : IPrivacyFirewall
    {
        public bool Enabled => true;
        public bool OutputWasBounded { get; private set; }
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            if (context.Destination == EgressDestination.Output) OutputWasBounded = cancellationToken.CanBeCanceled;
            return ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
        }
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
        { Calls++; return send(request); }
    }
}
