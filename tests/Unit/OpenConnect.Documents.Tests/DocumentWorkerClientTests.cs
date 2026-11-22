using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.Documents.Tests;

public sealed class DocumentWorkerClientTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)0xab, 32).ToArray();
    private static EgressContext Context() => new(new(Ulid.NewUlid()), new(Ulid.NewUlid()),
        new(Ulid.NewUlid()), EgressDestination.Worker);

    [Theory]
    [InlineData("documents.fields")]
    [InlineData("documents.layout")]
    [InlineData("documents.tables")]
    public async Task AuthorizesContentAndSignsTheExactTenantBoundRequest(string kind)
    {
        var context = Context();
        var authorized = false;
        var handler = new Handler(async request =>
        {
            Assert.True(authorized);
            var body = await request.Content!.ReadAsByteArrayAsync();
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(Key, body)).ToLowerInvariant(),
                request.Headers.GetValues("X-OpenConnect-Signature").Single());
            Assert.Equal("worker-token", request.Headers.Authorization!.Parameter);
            using var payload = JsonDocument.Parse(body);
            Assert.Equal(context.Tenant.Value.ToString(), payload.RootElement.GetProperty("tenantId").GetString());
            Assert.Equal("ZG9j", payload.RootElement.GetProperty("payload").GetProperty("contentBase64").GetString());
            return Reply(payload.RootElement.GetProperty("taskId").GetString()!, kind);
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, (scope, authorizedKind, content, _) =>
        {
            Assert.Equal(context, scope);
            Assert.Equal(kind, authorizedKind);
            Assert.Equal("doc", Encoding.UTF8.GetString(content.Span));
            authorized = true;
            return ValueTask.FromResult(true);
        });
        using var result = await client.ExecuteAsync(context, kind, Encoding.UTF8.GetBytes("doc"));
        Assert.Equal(kind, result.RootElement.GetProperty("kind").GetString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Checks_decoded_ocr_response_before_returning_it()
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            var taskId = json.RootElement.GetProperty("taskId").GetString()!;
            const string text = "person@example.com";
            var body = JsonSerializer.Serialize(new
            {
                schemaVersion = "openconnect.v1",
                taskId,
                kind = "documents.ocr",
                result = new
                {
                    pages = new[] { new { text, width = 10, height = 10, model_id = "model",
                    model_fingerprint = new string('a', 64), language = "eng",
                    spans = new[] { new { text, start = 0, end = text.Length, box = new[] { 0.0, 0.0, 1.0, 1.0 }, confidence = 0.9 } } } }
                }
            });
            body = body.Replace("@", "\\u0040", StringComparison.Ordinal);
            Assert.Contains("\\u0040", body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler);
        using var client = new DocumentWorkerClient(http, "worker-token", Key, new Firewall(blockEmail: true),
            Options.Create(new PrivacyOptions()), (_, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.ExecuteAsync(Context(), "documents.ocr", new byte[] { 1 }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalysisValidatesPageStatusAndSourceReferences(bool invalidReference)
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            var taskId = json.RootElement.GetProperty("taskId").GetString()!;
            var page = new
            {
                text = "Hello",
                width = 10,
                height = 10,
                model_id = "model",
                model_fingerprint = new string('a', 64),
                language = "eng",
                spans = new[] { new { text = "Hello", start = 0, end = 5, box = new[] { 0d, 0d, 1d, 1d }, confidence = 0.9 } }
            };
            var line = new
            {
                page_index = invalidReference ? 0 : 1,
                start = 0,
                end = 5,
                span_indexes = new[] { 0 },
                box = new[] { 0d, 0d, 1d, 1d },
                minimum_ocr_confidence = 0.9
            };
            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = "openconnect.v1",
                taskId,
                kind = "documents.analyze",
                result = new
                {
                    pages = new object[] { new { page_index = 0, status = "failed", page = (object?)null, reason = "page_unavailable" },
                    new { page_index = 1, status = "completed", page, reason = (string?)null } },
                    layout = new { status = "completed", items = new[] { line }, warnings = Array.Empty<string>() },
                    tables = new { status = "completed", items = Array.Empty<object>(), warnings = Array.Empty<string>() },
                    fields = new { status = "completed", items = Array.Empty<object>(), warnings = Array.Empty<string>() },
                    status = "partial",
                    warnings = new[] { "heuristic_candidates_require_review", "page_results_incomplete" }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, (_, _, _, _) => ValueTask.FromResult(true));
        if (invalidReference)
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(Context(), "documents.analyze", "document"u8.ToArray()));
        else
        {
            using var result = await client.ExecuteAsync(Context(), "documents.analyze", "document"u8.ToArray());
            Assert.Equal("partial", result.RootElement.GetProperty("result").GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task SensitiveTableOutputIsBlockedAndReportedWithoutSourceContent()
    {
        var fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "document-tables-response.json"));
        const string email = "alice@example.test";
        var handler = new Handler(async request =>
        {
            using var input = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            var root = System.Text.Json.Nodes.JsonNode.Parse(fixture)!;
            root["taskId"] = input.RootElement.GetProperty("taskId").GetString();
            var page = root["result"]!["pages"]![0]!;
            page["text"] = "Item Price\nWood " + email;
            page["spans"]![3]!["text"] = email;
            page["spans"]![3]!["end"] = 16 + email.Length;
            root["result"]!["tables"]![0]!["cells"]![3]!["end"] = 16 + email.Length;
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler);
        var sink = new EvidenceSink();
        var inner = new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new OpenConnect.Privacy.Policy.PolicyEngine(new OpenConnect.Privacy.Rules.RulesSecurityDetector()));
        using var client = new DocumentWorkerClient(http, "worker-token", Key,
            new EvidenceRecordingPrivacyFirewall(inner, sink), Options.Create(new PrivacyOptions()), (_, _, _, _) => ValueTask.FromResult(true));

        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.ExecuteAsync(Context(), "documents.tables", new byte[] { 1 }));
        var evidence = Assert.Single(sink.Events, item => item.Destination == EgressDestination.Output);
        Assert.False(evidence.Allowed);
        Assert.Equal(SecurityPolicyAction.Block, evidence.Action);
        Assert.True(evidence.Categories["EMAIL"] > 0);
        Assert.DoesNotContain(email, JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_firewall_prevents_document_dispatch()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = new DocumentWorkerClient(http, "worker-token", Key, new Firewall(enabled: false),
            Options.Create(new PrivacyOptions()), (_, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.ExecuteAsync(Context(), "documents.ocr", new byte[] { 1 }));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Disabled_decision_prevents_document_dispatch()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = new DocumentWorkerClient(http, "worker-token", Key, new DisabledDecisionFirewall(),
            Options.Create(new PrivacyOptions()), (_, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.ExecuteAsync(Context(), "documents.ocr", new byte[] { 1 }));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Request_deadline_covers_authorization_before_dispatch()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        ReadOnlyMemory<byte> captured = default;
        using var client = new DocumentWorkerClient(http, "worker-token", Key, new Firewall(),
            Options.Create(new PrivacyOptions()), async (_, _, content, token) =>
            {
                captured = content;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            }, requestTimeout: TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExecuteAsync(Context(), "documents.ocr", new byte[] { 1, 2 }));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(new byte[] { 0, 0 }, captured.ToArray());
    }

    [Fact]
    public async Task Request_deadline_covers_privacy_readiness_before_dispatch()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = new DocumentWorkerClient(http, "worker-token", Key, new WaitingFirewall(),
            Options.Create(new PrivacyOptions()), (_, _, _, _) => ValueTask.FromResult(true), requestTimeout: TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExecuteAsync(Context(), "documents.ocr", new byte[] { 1 }));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class WaitingFirewall : IPrivacyFirewall
    {
        public bool Enabled => true;
        public async ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(true, content, [], SecurityPolicyAction.Allow, "test");
        }
    }

    [Fact]
    public async Task FirewallDisabledDuringReadinessPreventsDispatch()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = new DocumentWorkerClient(http, "worker-token", Key, new DisablingFirewall(),
            Options.Create(new PrivacyOptions()), (_, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.ExecuteAsync(Context(), "documents.ocr", new byte[] { 1 }));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class DisablingFirewall : IPrivacyFirewall
    {
        public bool Enabled { get; private set; } = true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            Enabled = false;
            return ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
        }
    }

    [Fact]
    public async Task DisabledRecordedFirewallReportsUnavailableDocumentEnforcement()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        var sink = new EvidenceSink();
        using var http = new HttpClient(handler);
        using var client = new DocumentWorkerClient(http, "worker-token", Key,
            new EvidenceRecordingPrivacyFirewall(new Firewall(enabled: false), sink), Options.Create(new PrivacyOptions()),
            (_, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.ExecuteAsync(Context(), "documents.ocr", new byte[] { 1 }));
        Assert.Equal(0, handler.Calls);
        Assert.Equal("enforcement_unavailable", Assert.Single(sink.Events).ReasonCode);
    }

    [Fact]
    public async Task MissingDetectorBlocksDocumentBeforeDispatchAndRecordsReason()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        var sink = new EvidenceSink();
        var options = Options.Create(new PrivacyOptions());
        var firewall = new EvidenceRecordingPrivacyFirewall(
            new PrivacyFirewall(options, new OpenConnect.Privacy.Policy.PolicyEngine()), sink);
        using var http = new HttpClient(handler);
        using var client = new DocumentWorkerClient(http, "worker-token", Key, firewall, options,
            (_, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.ExecuteAsync(Context(), "documents.ocr", new byte[] { 1 }));
        Assert.Equal(0, handler.Calls);
        Assert.Equal("enforcement_unavailable", Assert.Single(sink.Events).ReasonCode);
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

    [Fact]
    public async Task DocumentCopyIsClearedWhenAuthorizationFails()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        ReadOnlyMemory<byte> captured = default;
        using var client = Client(http, (_, _, bytes, _) => { captured = bytes; return ValueTask.FromResult(false); });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ExecuteAsync(Context(), "documents.ocr", new byte[] { 1, 2 }));
        Assert.Equal(new byte[] { 0, 0 }, captured.ToArray());
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SignedRequestBufferIsClearedAfterCompletionOrFailure(bool succeeds)
    {
        using var capture = new CapturingWriteStream();
        var handler = new Handler(async request =>
        {
            await request.Content!.CopyToAsync(capture);
            using var input = JsonDocument.Parse(capture.LastWrite);
            return succeeds ? Reply(input.RootElement.GetProperty("taskId").GetString()!, "documents.ocr") :
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, (_, _, _, _) => ValueTask.FromResult(true));
        if (succeeds)
        {
            using var result = await client.ExecuteAsync(Context(), "documents.ocr", Encoding.UTF8.GetBytes("sensitive"));
            Assert.Equal("documents.ocr", result.RootElement.GetProperty("kind").GetString());
        }
        else
            await Assert.ThrowsAsync<HttpRequestException>(() => client.ExecuteAsync(Context(), "documents.ocr", Encoding.UTF8.GetBytes("sensitive")));
        Assert.False(capture.LastWrite.IsEmpty);
        Assert.All(capture.LastWrite.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task AuthorizationDenialDoesNotSendDocument()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = Client(http, (_, _, _, _) => ValueTask.FromResult(false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            client.ExecuteAsync(Context(), "documents.ocr", new byte[] { 1 }));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("box")]
    [InlineData("span")]
    [InlineData("confidence")]
    public async Task LayoutRejectsUnverifiedRegionEvidence(string mutation)
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            using var original = Reply(json.RootElement.GetProperty("taskId").GetString()!, "documents.layout");
            var root = System.Text.Json.Nodes.JsonNode.Parse(await original.Content.ReadAsStringAsync())!;
            var line = root["result"]!["lines"]![0]!;
            if (mutation == "box")
                line["box"]![2] = .5;
            if (mutation == "span")
                line["span_indexes"]![0] = 1;
            if (mutation == "confidence")
                line["minimum_ocr_confidence"] = .2;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, (_, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(Context(), "documents.layout", new byte[] { 1 }));
    }

    [Fact]
    public async Task InvalidContextAndCapabilityDoNotSendDocument()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = Client(http, (_, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExecuteAsync(
            Context() with { Destination = EgressDestination.ExternalModel }, "documents.ocr", new byte[] { 1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExecuteAsync(Context(), "documents.unsupported", new byte[] { 1 }));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AuthorizesAndSendsAnImmutableSnapshotOfCallerBuffer()
    {
        var original = Encoding.UTF8.GetBytes("doc");
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            Assert.Equal("ZG9j", json.RootElement.GetProperty("payload").GetProperty("contentBase64").GetString());
            return Reply(json.RootElement.GetProperty("taskId").GetString()!, "documents.ocr");
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, (_, _, _, _) =>
        {
            original[0] = (byte)'X';
            return ValueTask.FromResult(true);
        });
        using var result = await client.ExecuteAsync(Context(), "documents.ocr", original);
        Assert.Equal("documents.ocr", result.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task RejectsOversizedWorkerResponse()
    {
        var handler = new Handler(_ =>
        {
            var content = new ByteArrayContent(new byte[1]);
            content.Headers.ContentLength = 8 * 1024 * 1024 + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, (_, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(Context(), "documents.ocr", new byte[] { 1 }));
    }

    [Fact]
    public async Task TimesOutWhenResponseBodyStallsAfterHeaders()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream())
        }));
        using var http = new HttpClient(handler);
        using var client = new DocumentWorkerClient(http, "worker-token", Key, new Firewall(),
            Options.Create(new PrivacyOptions()), (_, _, _, _) => ValueTask.FromResult(true),
            requestTimeout: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ExecuteAsync(Context(), "documents.ocr", new byte[] { 1 }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsMismatchedOrDuplicateEnvelopeFields(bool duplicate)
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            var taskId = json.RootElement.GetProperty("taskId").GetString()!;
            if (!duplicate)
                return Reply("other", "documents.ocr");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                $"{{\"schemaVersion\":\"openconnect.v1\",\"taskId\":\"{taskId}\",\"kind\":\"documents.ocr\",\"kind\":\"documents.ocr\",\"result\":{{}}}}")
            };
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, (_, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(Context(), "documents.ocr", new byte[] { 1 }));
    }

    [Fact]
    public async Task UsesUnixSocketAndPropagatesWorkerFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oc-{Guid.NewGuid():N}.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            var server = Task.Run(async () =>
            {
                using var socket = await listener.AcceptAsync(timeout.Token);
                await using var stream = new NetworkStream(socket, ownsSocket: false);
                var request = new byte[4096];
                var count = await stream.ReadAsync(request, timeout.Token);
                Assert.Contains("POST /internal/v1/execute HTTP/1.1", Encoding.ASCII.GetString(request, 0, count));
                await stream.WriteAsync("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
            }, timeout.Token);
            using var client = new DocumentWorkerClient(path, "worker-token", Key, new Firewall(),
                Options.Create(new PrivacyOptions()), (_, _, _, _) => ValueTask.FromResult(true));
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.ExecuteAsync(
                Context(), "documents.ocr", new byte[] { 1 }, timeout.Token));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
            await server;
        }
        finally
        {
            listener.Dispose();
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [LiveDocumentFact]
    public async Task AuthorizedClientRunsOcrAndClamAvAgainstLiveWorker()
    {
        var context = Context();
        var authorizationCalls = 0;
        using var client = new DocumentWorkerClient(
            Environment.GetEnvironmentVariable("OC_TEST_DOCUMENT_SOCKET")!,
            Environment.GetEnvironmentVariable("OC_TEST_DOCUMENT_TOKEN")!,
            Convert.FromHexString(Environment.GetEnvironmentVariable("OC_TEST_DOCUMENT_KEY")!),
            new Firewall(), Options.Create(new PrivacyOptions()), (scope, _, content, _) =>
            {
                authorizationCalls++;
                return ValueTask.FromResult(scope == context && content.Length > 0);
            });
        var fixture = await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable("OC_TEST_DOCUMENT_IMAGE")!);
        using var result = await client.ExecuteAsync(context, "documents.fields", fixture);
        var pages = result.RootElement.GetProperty("result").GetProperty("pages");
        Assert.Single(pages.EnumerateArray());
        Assert.NotEmpty(pages[0].GetProperty("spans").EnumerateArray());
        var testFile = Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}$" +
            "EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ExecuteAsync(context, "documents.ocr", testFile));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, error.StatusCode);
        Assert.Equal(2, authorizationCalls);
    }

    private static DocumentWorkerClient Client(HttpClient http,
        Func<EgressContext, string, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> authorize) =>
        new(http, "worker-token", Key, new Firewall(), Options.Create(new PrivacyOptions()), authorize);

    private static HttpResponseMessage Reply(string taskId, string kind) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            schemaVersion = "openconnect.v1",
            taskId,
            kind,
            result = kind == "documents.fields"
                ? (object)new { pages = Pages(), fields = Array.Empty<object>(), warnings = Array.Empty<string>() }
                : kind == "documents.layout" ? new
                {
                    pages = Pages(),
                    lines = new[] { new { page_index = 0, start = 0, end = 3,
                    span_indexes = new[] { 0 }, box = new[] { 0.0, 0.0, 1.0, 1.0 }, minimum_ocr_confidence = .9 } },
                    warnings = Array.Empty<string>()
                }
                : kind == "documents.tables" ? new { pages = Pages(), tables = Array.Empty<object>(), warnings = Array.Empty<string>() }
                : new { pages = Pages() }
        }), Encoding.UTF8, "application/json")
    };

    private static object[] Pages() => [new
    {
        text = "doc", width = 10, height = 10, model_id = "model", model_fingerprint = new string('a', 64),
        language = "eng", spans = new[] { new { text = "doc", start = 0, end = 3,
            box = new[] { 0.0, 0.0, 1.0, 1.0 }, confidence = 0.9 } }
    }];

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request);
        }
    }

    private sealed class CapturingWriteStream : Stream
    {
        public ReadOnlyMemory<byte> LastWrite { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => LastWrite = buffer.AsMemory(offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { LastWrite = buffer.AsMemory(offset, count); return Task.CompletedTask; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { LastWrite = buffer; return ValueTask.CompletedTask; }
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

    private sealed class Firewall(bool blockEmail = false, bool enabled = true) : IPrivacyFirewall
    {
        public bool Enabled => enabled;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, blockEmail && content.Contains('@') ? "[MASKED]" : content,
                [], SecurityPolicyAction.Allow, "fixture"));
    }

    private sealed class LiveDocumentFactAttribute : FactAttribute
    {
        public LiveDocumentFactAttribute()
        {
            string[] names = ["OC_TEST_DOCUMENT_SOCKET", "OC_TEST_DOCUMENT_TOKEN", "OC_TEST_DOCUMENT_KEY", "OC_TEST_DOCUMENT_IMAGE"];
            if (names.Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
                Skip = "Set the document-worker test socket, credentials and image to enable this integration check.";
        }
    }
}
