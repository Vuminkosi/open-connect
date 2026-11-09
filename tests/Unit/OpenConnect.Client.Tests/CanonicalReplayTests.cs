using System.Net;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Client;
using Xunit;
namespace OpenConnect.Client.Tests;
public sealed class CanonicalReplayTests
{
    private static readonly Guid SessionUuid = Guid.Parse("123e4567-e89b-42d3-a456-426614174001");
    private static string Envelope(Guid eventId, long sequence, string occurredAt, string payloadJson, string? correlationId = null)
    {
        var correlation = correlationId is null ? string.Empty : $"\"correlationId\":\"{correlationId}\",";
        return $"{{\"schemaVersion\":\"openconnect.v1\",\"eventId\":\"{eventId:D}\",\"eventType\":\"agent.completed\",\"sessionId\":\"{SessionUuid:D}\",\"sequence\":{sequence},\"occurredAt\":\"{occurredAt}\",{correlation}\"payload\":{payloadJson}}}";
    }
    [Fact]
    public async Task ReplayEvents_sends_canonical_cursor_and_parses_typed_envelopes()
    {
        var firstId = Guid.Parse("123e4567-e89b-42d3-a456-426614174010");
        var secondId = Guid.Parse("123e4567-e89b-42d3-a456-426614174011");
        var body = $"[{Envelope(firstId, 18, "2026-01-01T00:00:00Z", """{"taskId":"task-1"}""")},{Envelope(secondId, 19, "2026-01-02T00:00:00Z", """{"taskId":"task-1"}""", "corr-7")}]";
        var handler = new ReplayStubHandler((request, _) =>
        {
            Assert.EndsWith($"/sessions/{SessionUuid:D}/events", request.RequestUri!.AbsolutePath);
            Assert.Equal("afterSequence=17", request.RequestUri.Query.TrimStart('?'));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/openconnect/v1/") };

        var events = new List<CanonicalAgentEvent>();
        await foreach (var item in new OpenConnectClient(http).ReplayEventsAsync(SessionUuid, 17)) events.Add(item);

        Assert.Equal(2, events.Count);
        Assert.Equal(firstId, events[0].EventId);
        Assert.Equal("agent.completed", events[0].EventType);
        Assert.Equal(SessionUuid, events[0].SessionId);
        Assert.Equal(18, events[0].Sequence);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), events[0].OccurredAt);
        Assert.Equal("task-1", events[0].Payload["taskId"]!.GetValue<string>());
        Assert.Null(events[0].CorrelationId);
        Assert.Equal("corr-7", events[1].CorrelationId);
        Assert.Equal("openconnect.v1", events[1].SchemaVersion);
    }
    [Fact]
    public async Task ReplayEvents_resumes_from_last_received_sequence()
    {
        var firstId = Guid.Parse("123e4567-e89b-42d3-a456-426614174010");
        var secondId = Guid.Parse("123e4567-e89b-42d3-a456-426614174011");
        var queries = new List<string?>();
        var handler = new ReplayStubHandler((request, _) =>
        {
            queries.Add(request.RequestUri!.Query.TrimStart('?'));
            var body = queries.Count == 1
                ? $"[{Envelope(firstId, 1, "2026-01-01T00:00:00Z", """{"n":1}""")},{Envelope(secondId, 2, "2026-01-01T00:00:00Z", """{"n":2}""")}]"
                : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/openconnect/v1/") };
        var client = new OpenConnectClient(http);

        var first = new List<CanonicalAgentEvent>();
        await foreach (var item in client.ReplayEventsAsync(SessionUuid)) first.Add(item);
        var cursor = first.Max(item => item.Sequence);
        var resumed = new List<CanonicalAgentEvent>();
        await foreach (var item in client.ReplayEventsAsync(SessionUuid, cursor)) resumed.Add(item);

        Assert.Equal(["afterSequence=0", "afterSequence=2"], queries);
        Assert.Empty(resumed);
    }
    [Fact]
    public async Task ReplayEvents_legacy_without_identity_is_explicit_never_silent()
    {
        var handler = new ReplayStubHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)422)
        {
            Content = new StringContent("{\"type\":\"https://openconnect.dev/problems/legacy-event-without-canonical-identity\",\"title\":\"A stored event lacks genuine canonical identity and cannot be projected.\",\"status\":422}", System.Text.Encoding.UTF8, "application/problem+json")
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };

        var error = await Assert.ThrowsAsync<OpenConnectClientException>(async () =>
        {
            await foreach (var _ in new OpenConnectClient(http).ReplayEventsAsync(SessionUuid)) { }
        });

        Assert.Equal(422, error.StatusCodeValue);
        Assert.Equal("legacy_event_without_identity", error.ErrorCode);
        Assert.DoesNotContain("https://openconnect.dev/problems", error.Message);
    }
    [Fact]
    public async Task ReplayEvents_unprocessable_without_problem_body_uses_stable_code()
    {
        var handler = new ReplayStubHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)422)
        {
            Content = new StringContent("{not-json", System.Text.Encoding.UTF8, "application/json")
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };

        var error = await Assert.ThrowsAsync<OpenConnectClientException>(async () =>
        {
            await foreach (var _ in new OpenConnectClient(http).ReplayEventsAsync(SessionUuid)) { }
        });

        Assert.Equal(422, error.StatusCodeValue);
        Assert.Equal("unprocessable_entity", error.ErrorCode);
    }
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ReplayEvents_auth_and_missing_session_pass_through(HttpStatusCode status)
    {
        var handler = new ReplayStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"code\":\"session_not_found\"}")
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };

        var error = await Assert.ThrowsAsync<OpenConnectClientException>(async () =>
        {
            await foreach (var _ in new OpenConnectClient(http).ReplayEventsAsync(SessionUuid)) { }
        });

        Assert.Equal((int)status, error.StatusCodeValue);
    }
    [Fact]
    public async Task ReplayEvents_rejects_empty_session_and_negative_cursor_before_request()
    {
        var handler = new ReplayStubHandler((_, _) => throw new InvalidOperationException("Request should not be sent."));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var client = new OpenConnectClient(http);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in client.ReplayEventsAsync(Guid.Empty)) { }
        });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in client.ReplayEventsAsync(SessionUuid, -1)) { }
        });
    }
    [Fact]
    public async Task ReplayEvents_cancellation_during_http_request_propagates_caller_token()
    {
        using var cts = new CancellationTokenSource();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ReplayStubHandler(async (_, token) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var enumerator = new OpenConnectClient(http)
            .ReplayEventsAsync(SessionUuid, cancellationToken: cts.Token)
            .GetAsyncEnumerator();

        var moveNext = enumerator.MoveNextAsync().AsTask();
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await moveNext.WaitAsync(TimeSpan.FromSeconds(5)));
        await enumerator.DisposeAsync();
    }
    private sealed class ReplayStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}
