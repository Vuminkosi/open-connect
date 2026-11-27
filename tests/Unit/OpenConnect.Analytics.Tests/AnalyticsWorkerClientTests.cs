using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.Analytics.Tests;

public sealed class AnalyticsWorkerClientTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)0xab, 32).ToArray();
    private static EgressContext Context => new(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
    private const string Payload = """{"feature":"weight","unit":"kg","lineage":"fixture","values":[1,2,3,4,5,100]}""";

    [AnalyticsLiveFact]
    public async Task SendsAuthorizedRequestToARealWorker()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OPENCONNECT_ANALYTICS_TEST_URL")!);
        var token = Environment.GetEnvironmentVariable("OPENCONNECT_ANALYTICS_TEST_TOKEN")!;
        var key = Convert.FromHexString(Environment.GetEnvironmentVariable("OPENCONNECT_ANALYTICS_TEST_KEY")!);
        using var client = new AnalyticsWorkerClient(endpoint, token, key, new Firewall(),
            (_, _, _) => ValueTask.FromResult(true), allowInsecureHttp: endpoint.IsLoopback);
        using var payload = JsonDocument.Parse(Payload);
        using var result = await client.AnalyzeAsync(Context, payload.RootElement);
        Assert.Equal("analytics.analyze", result.RootElement.GetProperty("kind").GetString());
        Assert.Equal(6, result.RootElement.GetProperty("result").GetProperty("sample_size").GetInt32());
    }

    [Fact]
    public async Task Signs_authorized_request_and_checks_returned_anomaly()
    {
        var context = Context;
        var handler = new Handler(async request =>
        {
            var body = await request.Content!.ReadAsByteArrayAsync();
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(Key, body)).ToLowerInvariant(),
                request.Headers.GetValues("X-OpenConnect-Signature").Single());
            using var json = JsonDocument.Parse(body);
            Assert.Equal(context.Tenant.Value.ToString(), json.RootElement.GetProperty("tenantId").GetString());
            return Reply(json.RootElement.GetProperty("taskId").GetString()!, 100);
        });
        using var http = new HttpClient(handler);
        using var client = new AnalyticsWorkerClient(http, new Uri("https://worker.test"), "token", Key, new Firewall(),
            (scope, _, _) => ValueTask.FromResult(scope == context));
        using var payload = JsonDocument.Parse(Payload);
        using var result = await client.AnalyzeAsync(context, payload.RootElement);
        Assert.Equal(5, result.RootElement.GetProperty("result").GetProperty("anomalies")[0].GetProperty("index").GetInt32());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClearsSignedAnalyticsBytesAfterSuccessOrFailure(bool succeeds)
    {
        using var capture = new CapturingWriteStream();
        var handler = new Handler(async request =>
        {
            await request.Content!.CopyToAsync(capture);
            using var input = JsonDocument.Parse(capture.LastWrite);
            return succeeds ? Reply(input.RootElement.GetProperty("taskId").GetString()!, 100) :
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var http = new HttpClient(handler);
        using var client = new AnalyticsWorkerClient(http, new Uri("https://worker.test"), "token", Key, new Firewall(),
            (_, _, _) => ValueTask.FromResult(true));
        using var payload = JsonDocument.Parse(Payload);
        if (succeeds)
        {
            using var result = await client.AnalyzeAsync(Context, payload.RootElement);
            Assert.Equal("analytics.analyze", result.RootElement.GetProperty("kind").GetString());
        }
        else
            await Assert.ThrowsAsync<HttpRequestException>(() => client.AnalyzeAsync(Context, payload.RootElement));
        Assert.False(capture.LastWrite.IsEmpty);
        Assert.All(capture.LastWrite.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Authorization_and_privacy_denials_do_not_send()
    {
        var handler = new Handler(_ => throw new InvalidOperationException());
        using var http = new HttpClient(handler);
        using var payload = JsonDocument.Parse(Payload);
        using var denied = new AnalyticsWorkerClient(http, new Uri("https://worker.test"), "token", Key, new Firewall(), (_, _, _) => ValueTask.FromResult(false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.AnalyzeAsync(Context, payload.RootElement));
        using var masked = new AnalyticsWorkerClient(http, new Uri("https://worker.test"), "token", Key, new Firewall(mask: true), (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => masked.AnalyzeAsync(Context, payload.RootElement));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CancellationDuringAuthorizationPreventsDispatch()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var payload = JsonDocument.Parse(Payload);
        using var cancellation = new CancellationTokenSource();
        using var client = new AnalyticsWorkerClient(http, new Uri("https://worker.test"), "token", Key, new Firewall(),
            (_, _, token) => { Assert.True(token.CanBeCanceled); cancellation.Cancel(); return ValueTask.FromResult(true); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AnalyzeAsync(Context, payload.RootElement, cancellation.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DisabledRecordedFirewallBlocksAndReportsUnavailableEnforcement()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        var sink = new EvidenceSink();
        using var http = new HttpClient(handler);
        using var payload = JsonDocument.Parse(Payload);
        using var client = new AnalyticsWorkerClient(http, new Uri("https://worker.test"), "token", Key,
            new EvidenceRecordingPrivacyFirewall(new DisabledFirewall(), sink), (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.AnalyzeAsync(Context, payload.RootElement));
        Assert.Equal(0, handler.Calls);
        Assert.Equal("enforcement_unavailable", Assert.Single(sink.Events).ReasonCode);
    }

    [Fact]
    public async Task Rejects_forged_result_without_echoing_input()
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Reply(json.RootElement.GetProperty("taskId").GetString()!, 999);
        });
        using var http = new HttpClient(handler);
        using var payload = JsonDocument.Parse(Payload);
        using var client = new AnalyticsWorkerClient(http, new Uri("https://worker.test"), "token", Key, new Firewall(), (_, _, _) => ValueTask.FromResult(true));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AnalyzeAsync(Context, payload.RootElement));
        Assert.DoesNotContain(Payload, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Escaped_sensitive_strings_are_checked_after_decoding()
    {
        using var payload = JsonDocument.Parse("""{"feature":"weight","unit":"kg","lineage":"person\u0040example.com","values":[1]}""");
        var handler = new Handler(_ => throw new InvalidOperationException());
        using var http = new HttpClient(handler);
        using var client = new AnalyticsWorkerClient(http, new Uri("https://worker.test"), "token", Key, new DecodedFirewall(), (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.AnalyzeAsync(Context, payload.RootElement));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task OutputInspectionReceivesTheRequestDeadline()
    {
        var firewall = new DeadlineFirewall();
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Reply(json.RootElement.GetProperty("taskId").GetString()!, 100);
        });
        using var http = new HttpClient(handler);
        using var payload = JsonDocument.Parse(Payload);
        using var client = new AnalyticsWorkerClient(http, new Uri("https://worker.test"), "token", Key, firewall,
            (_, _, _) => ValueTask.FromResult(true));
        using var result = await client.AnalyzeAsync(Context, payload.RootElement);
        Assert.True(firewall.OutputWasBounded);
    }

    [Fact]
    public void Accepts_unicode_scalar_length_used_by_python_and_json_schema()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            feature = string.Concat(Enumerable.Repeat("😀", 128)),
            unit = "kg",
            lineage = "fixture",
            values = new[] { 1 },
            forecast = (object?)null
        });
        AnalyticsValidation.ValidateInput(payload);
        var tooLong = JsonSerializer.SerializeToElement(new
        {
            feature = string.Concat(Enumerable.Repeat("😀", 129)),
            unit = "kg",
            lineage = "fixture",
            values = new[] { 1 }
        });
        Assert.Throws<ArgumentException>(() => AnalyticsValidation.ValidateInput(tooLong));
    }

    [Fact]
    public async Task Accepts_actual_python_algorithm_contract_fixtures()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "python-analytics.json");
        using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        foreach (var fixture in fixtures.RootElement.EnumerateObject())
        {
            var payload = fixture.Value.GetProperty("payload");
            AnalyticsValidation.ValidateInput(payload);
            AnalyticsValidation.ValidateOutput(fixture.Value.GetProperty("response"), payload, "fixture");
        }
    }

    [Fact]
    public async Task RejectsForgedTrendSlopeAndWarningsFromWorker()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "python-analytics.json");
        var fixtures = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var regular = fixtures["regular"]!;
        using var payload = JsonDocument.Parse(regular["payload"]!.ToJsonString());
        var response = regular["response"]!.DeepClone();
        response["result"]!["trend"]!["slope_per_sample"] = 999;
        using var wrongSlope = JsonDocument.Parse(response.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => AnalyticsValidation.ValidateOutput(wrongSlope.RootElement, payload.RootElement, "fixture"));
        response["result"]!["trend"]!["slope_per_sample"] = 1;
        response["result"]!["trend"]!["warnings"] = new JsonArray();
        using var wrongWarnings = JsonDocument.Parse(response.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => AnalyticsValidation.ValidateOutput(wrongWarnings.RootElement, payload.RootElement, "fixture"));
        response["result"]!["trend"]!["warnings"] = regular["response"]!["result"]!["trend"]!["warnings"]!.DeepClone();
        response["result"]!["trend"]!["slope_per_sample"] = null;
        using var missingSlope = JsonDocument.Parse(response.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => AnalyticsValidation.ValidateOutput(missingSlope.RootElement, payload.RootElement, "fixture"));
    }

    [Fact]
    public async Task Rejects_forged_summary_or_omitted_anomaly()
    {
        using var payload = JsonDocument.Parse(Payload);
        using var response = Reply("task", 100);
        var body = await response.Content.ReadAsStringAsync();
        var wrongSummary = JsonNode.Parse(body)!;
        wrongSummary["result"]!["median"] = 99;
        using var first = JsonDocument.Parse(wrongSummary.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => AnalyticsValidation.ValidateOutput(first.RootElement, payload.RootElement, "task"));
        var missingAnomaly = JsonNode.Parse(body)!;
        missingAnomaly["result"]!["anomalies"] = new JsonArray();
        using var second = JsonDocument.Parse(missingAnomaly.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => AnalyticsValidation.ValidateOutput(second.RootElement, payload.RootElement, "task"));
    }

    [Fact]
    public void Validates_forecast_provenance_and_expected_horizon()
    {
        using var input = JsonDocument.Parse("""{"feature":"weight","unit":"kg","lineage":"fixture","values":[1,2,3,4,5,6,7],"forecast":{"timestamps":["2026-01-01T00:00:00Z","2026-01-02T00:00:00Z","2026-01-03T00:00:00Z","2026-01-04T00:00:00Z","2026-01-05T00:00:00Z","2026-01-06T00:00:00Z","2026-01-07T00:00:00Z"],"intervalSeconds":86400,"seasonalPeriod":1,"horizon":1}}""");
        using var valid = ForecastReply("2026-01-08T00:00:00Z", 7, true);
        AnalyticsValidation.ValidateOutput(valid.RootElement, input.RootElement, "task");
        using var wrongTime = ForecastReply("2026-01-09T00:00:00Z", 7, true);
        using var wrongValue = ForecastReply("2026-01-08T00:00:00Z", 99, true);
        using var missingPoint = ForecastReply("2026-01-08T00:00:00Z", 7, false);
        var forgedBacktest = JsonNode.Parse(valid.RootElement.GetRawText())!;
        forgedBacktest["result"]!["forecast"]!["backtest"]!["mean_absolute_error"] = 0;
        using var wrongBacktest = JsonDocument.Parse(forgedBacktest.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => AnalyticsValidation.ValidateOutput(wrongTime.RootElement, input.RootElement, "task"));
        Assert.Throws<InvalidOperationException>(() => AnalyticsValidation.ValidateOutput(wrongValue.RootElement, input.RootElement, "task"));
        Assert.Throws<InvalidOperationException>(() => AnalyticsValidation.ValidateOutput(missingPoint.RootElement, input.RootElement, "task"));
        Assert.Throws<InvalidOperationException>(() => AnalyticsValidation.ValidateOutput(wrongBacktest.RootElement, input.RootElement, "task"));
    }

    private static JsonDocument ForecastReply(string timestamp, int value, bool includePoint) => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        schemaVersion = "openconnect.v1",
        taskId = "task",
        kind = "analytics.analyze",
        result = new
        {
            feature = "weight",
            unit = "kg",
            lineage = "fixture",
            sample_size = 7,
            observed_count = 7,
            missing_count = 0,
            coverage = 1,
            minimum = 1,
            maximum = 7,
            median = 4,
            median_absolute_deviation = 2,
            threshold = 3.5,
            anomalies = Array.Empty<object>(),
            warnings = Array.Empty<string>(),
            algorithm_version = "robust-mad-v1",
            forecast = new
            {
                algorithm_version = "seasonal-naive-v1",
                interval_seconds = 86400,
                seasonal_period = 1,
                sample_size = 7,
                observed_count = 7,
                coverage = 1,
                points = includePoint ? new[] { new { timestamp, value, lower = 8, upper = 8 } } : [],
                backtest = new { samples = 5, mean_absolute_error = 1, naive_mean_absolute_error = 1, residual_lower = 1, residual_upper = 1 },
                warnings = new[] { "empirical_bands_not_calibrated", "limited_backtest_samples" }
            }
        }
    }));

    [Fact]
    public async Task Rejects_duplicate_input_properties_before_authorization()
    {
        using var payload = JsonDocument.Parse("""{"feature":"weight","unit":"kg","lineage":"a","lineage":"b","values":[]}""");
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException()));
        using var client = new AnalyticsWorkerClient(http, new Uri("https://worker.test"), "token", Key, new Firewall(), (_, _, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<ArgumentException>(() => client.AnalyzeAsync(Context, payload.RootElement));
    }

    private static HttpResponseMessage Reply(string taskId, int anomalyValue) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            schemaVersion = "openconnect.v1",
            taskId,
            kind = "analytics.analyze",
            result = new
            {
                feature = "weight",
                unit = "kg",
                lineage = "fixture",
                sample_size = 6,
                observed_count = 6,
                missing_count = 0,
                coverage = 1.0,
                minimum = 1,
                maximum = 100,
                median = 3.5,
                median_absolute_deviation = 1.5,
                threshold = 3.5,
                anomalies = new[] { new { index = 5, value = anomalyValue, robust_score = 0.6744897501960817 * ((100.0 - 3.5) / 1.5) } },
                warnings = Array.Empty<string>(),
                algorithm_version = "robust-mad-v1"
            }
        }), Encoding.UTF8, "application/json")
    };

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

    private sealed class Firewall(bool mask = false) : IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, mask ? "[MASKED]" : content, [], SecurityPolicyAction.Allow, "fixture"));
    }

    private sealed class DecodedFirewall : IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content.Contains('@') ? "[MASKED]" : content, [], SecurityPolicyAction.Allow, "fixture"));
    }

    private sealed class DeadlineFirewall : IPrivacyFirewall
    {
        public bool Enabled => true;
        public bool OutputWasBounded { get; private set; }
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            if (context.Destination == EgressDestination.Output)
                OutputWasBounded = cancellationToken.CanBeCanceled;
            return ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "fixture"));
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

public sealed class AnalyticsLiveFactAttribute : FactAttribute
{
    public AnalyticsLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_ANALYTICS_TEST_URL")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_ANALYTICS_TEST_TOKEN")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_ANALYTICS_TEST_KEY")))
            Skip = "Set the disposable analytics worker endpoint and credentials to run the live test.";
    }
}
