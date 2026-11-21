using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Client;
using Xunit;
namespace OpenConnect.Client.Tests;

public sealed class OpenConnectClientTests
{
    [Fact]
    public async Task CreateTask_sends_idempotency_and_reads_typed_response()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("key-1", request.Headers.GetValues("Idempotency-Key").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{\"id\":{\"value\":\"00000000000000000000000000\"},\"tenantId\":{\"value\":{\"value\":\"00000000000000000000000000\"}},\"principalId\":{\"value\":{\"value\":\"00000000000000000000000000\"}},\"state\":\"queued\"}", System.Text.Encoding.UTF8, "application/json") });
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/openconnect/v1/") };
        await Assert.ThrowsAnyAsync<Exception>(() => new OpenConnectClient(http).CreateTaskAsync(new("task", "key-1")));
        Assert.Equal("tasks", handler.Path);
    }
    [Fact]
    public async Task CreateMiniComposerTask_posts_existing_session_request_and_reads_typed_response()
    {
        var sessionId = Ulid.NewUlid().ToString();
        var handler = new StubHandler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/minicomposer/tasks", request.RequestUri!.AbsolutePath);
            Assert.Equal("key-mini", request.Headers.GetValues("Idempotency-Key").Single());

            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("client-task", body.RootElement.GetProperty("clientTaskId").GetString());
            Assert.Equal("Write a summary", body.RootElement.GetProperty("message").GetString());
            Assert.Equal(sessionId, body.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal("client-connection", body.RootElement.GetProperty("clientConnectionId").GetString());

            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("{\"id\":\"01ARZ3NDEKTSV4RRFFQ69G5FAV\",\"tenantId\":\"01ARZ3NDEKTSV4RRFFQ69G5FAV\",\"principalId\":\"01ARZ3NDEKTSV4RRFFQ69G5FAW\",\"state\":\"accepted\"}", System.Text.Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/openconnect/v1/") };

        var result = await new OpenConnectClient(http).CreateMiniComposerTaskAsync(
            new CreateTaskRequest("client-task", "key-mini", "Write a summary", sessionId) { ClientConnectionId = "client-connection" });

        Assert.Equal("tasks", handler.Path);
        Assert.Equal(AgentRunState.Accepted, result.State);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateMiniComposerTask_rejects_missing_or_blank_session_before_http(string? sessionId)
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Request should not be sent."));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new OpenConnectClient(http).CreateMiniComposerTaskAsync(new CreateTaskRequest("client-task", "key-mini", "message", sessionId)));

        Assert.Null(handler.Path);
    }

    [Fact]
    public async Task CreateMiniComposerTask_rejects_missing_connection_before_http()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Request should not be sent."));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };

        await Assert.ThrowsAsync<ArgumentException>(() => new OpenConnectClient(http).CreateMiniComposerTaskAsync(
            new CreateTaskRequest("client-task", "key-mini", "message", Ulid.NewUlid().ToString())));
        Assert.Null(handler.Path);
    }
    [Fact] public void Rejects_non_https_base_address() => Assert.Throws<ArgumentException>(() => new ServiceCollection().AddOpenConnectClient(new Uri("http://localhost/")));
    [Fact]
    public async Task GetCapabilities_reads_typed_list()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[{\"id\":{\"value\":\"privacy\"},\"kind\":\"security\",\"state\":\"ready\"}]", System.Text.Encoding.UTF8, "application/json")
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/openconnect/v1/") };

        var capabilities = await new OpenConnectClient(http).GetCapabilitiesAsync();

        Assert.Equal("capabilities", handler.Path);
        var capability = Assert.Single(capabilities);
        Assert.Equal(new CapabilityId("privacy"), capability.Id);
        Assert.Equal(CapabilityKind.Security, capability.Kind);
        Assert.Equal(DetectorState.Ready, capability.State);
    }
    [Fact]
    public async Task GetReadyCapabilitiesReadsLogicalModelAndToolDescriptors()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("/openconnect/v1/capabilities/ready", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"id":{"value":"knowledge.embed"},"kind":"model","state":"ready"},{"id":{"value":"knowledge.index"},"kind":"tool","state":"ready"}]""", System.Text.Encoding.UTF8, "application/json")
            });
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/openconnect/v1/") };
        var capabilities = await new OpenConnectClient(http).GetReadyCapabilitiesAsync();
        Assert.Equal("ready", handler.Path);
        Assert.Equal(new CapabilityId("knowledge.embed"), capabilities[0].Id);
        Assert.Equal(CapabilityKind.Tool, capabilities[1].Kind);
        Assert.Equal(DetectorState.Ready, capabilities[1].State);
    }
    [Fact]
    public async Task Cancellation_is_forwarded()
    {
        var handler = new StubHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OpenConnectClient(http).GetTaskAsync(new TaskId(Ulid.NewUlid()), cts.Token));
    }
    [Fact]
    public async Task SubscribeEvents_requests_finite_snapshot_after_cursor()
    {
        var sessionId = new SessionId(Ulid.NewUlid());
        var handler = new StubHandler((request, _) =>
        {
            Assert.EndsWith($"/sessions/{sessionId.Value}/events", request.RequestUri!.AbsolutePath);
            Assert.Equal("after=17", request.RequestUri.Query.TrimStart('?'));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
            });
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/openconnect/v1/") };

        var events = new List<EventRecord>();
        await foreach (var item in new OpenConnectClient(http).SubscribeEventsAsync(sessionId, 17))
            events.Add(item);

        Assert.Empty(events);
    }

    [Fact]
    public async Task PostClientInteractionResponse_posts_correlated_receipt_to_owned_session_route()
    {
        var sessionId = new SessionId(Ulid.NewUlid());
        var response = new ClientInteractionResponse(Guid.NewGuid().ToString("D"), "action", "{\"outcome\":\"Completed\"}");
        var handler = new StubHandler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith($"/sessions/{sessionId.Value}/liveui/responses", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains(response.CorrelationId, body);
            Assert.Contains("Completed", body);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/openconnect/v1/") };

        await new OpenConnectClient(http).PostClientInteractionResponseAsync(sessionId, response);
    }

    [Fact]
    public async Task PostClientInteractionResponse_accepts_correlated_inference_plan()
    {
        var sessionId = new SessionId(Ulid.NewUlid());
        var response = new ClientInteractionResponse(Guid.NewGuid().ToString("D"), "plan",
            "{\"provider\":\"Browser\",\"plan\":{\"schemaVersion\":\"1.0\",\"actions\":[]},\"outcome\":\"completed\"}");
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith($"/sessions/{sessionId.Value}/liveui/responses", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/openconnect/v1/") };

        await new OpenConnectClient(http).PostClientInteractionResponseAsync(sessionId, response);
    }
    [Fact]
    public async Task SubscribeEvents_rejects_negative_cursor_before_request()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Request should not be sent."));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var client = new OpenConnectClient(http);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in client.SubscribeEventsAsync(new SessionId(Ulid.NewUlid()), -1))
            { }
        });
    }
    [Fact]
    public async Task SubscribeEvents_cancellation_during_http_request_propagates_caller_token()
    {
        using var cts = new CancellationTokenSource();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (_, token) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var enumerator = new OpenConnectClient(http)
            .SubscribeEventsAsync(new SessionId(Ulid.NewUlid()), cancellationToken: cts.Token)
            .GetAsyncEnumerator();

        var moveNext = enumerator.MoveNextAsync().AsTask();
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await moveNext.WaitAsync(TimeSpan.FromSeconds(5)));
        await enumerator.DisposeAsync();
    }
    [Fact]
    public async Task SubscribeEvents_cancellation_after_first_event_stops_snapshot_enumeration()
    {
        var sessionId = new SessionId(Ulid.NewUlid());
        var responseBody = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new EventRecord(sessionId, 1, "agent.started", "first"),
            new EventRecord(sessionId, 2, "agent.completed", "second")
        }, OpenConnect.Abstractions.Serialization.OpenConnectHttpJson.CreateOptions());
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        using var cts = new CancellationTokenSource();
        var enumerator = new OpenConnectClient(http).SubscribeEventsAsync(sessionId, cancellationToken: cts.Token).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, enumerator.Current.Sequence);
        Assert.Equal(sessionId, enumerator.Current.SessionId);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await enumerator.DisposeAsync();
    }
    [Fact]
    public async Task Http_failure_does_not_expose_server_detail()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"code\":\"invalid_request\",\"detail\":\"sensitive provider response\"}")
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };

        var error = await Assert.ThrowsAsync<OpenConnectClientException>(() =>
            new OpenConnectClient(http).GetTaskAsync(new TaskId(Ulid.NewUlid())));

        Assert.Equal("invalid_request", error.ErrorCode);
        Assert.DoesNotContain("sensitive provider response", error.Message);
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"server failure\"")]
    [InlineData("42")]
    [InlineData("{\"code\":42,\"type\":[]}")]
    [InlineData("{not-json")]
    public async Task Http_failure_with_malformed_problem_body_uses_stable_client_exception(string body)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body)
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };

        var error = await Assert.ThrowsAsync<OpenConnectClientException>(() =>
            new OpenConnectClient(http).GetTaskAsync(new TaskId(Ulid.NewUlid())));

        Assert.Equal((int)HttpStatusCode.BadRequest, error.StatusCodeValue);
        Assert.Null(error.ErrorCode);
        Assert.Equal("The OpenConnect service could not complete the request.", error.Message);
    }
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Path = request.RequestUri!.AbsolutePath.Trim('/').Split('/').Last(); return callback(request, cancellationToken); }
    }
}
