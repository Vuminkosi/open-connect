using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class VoiceRealtimeTranscriptTests
{
    [Theory]
    [InlineData(true, "ws://worker:8080/internal/v1/realtime/local")]
    [InlineData(false, "ws://worker:8080/internal/v1/realtime")]
    public void WorkerRouteHonorsLocalOnlySetting(bool localOnly, string expected)
    {
        Assert.True(VoiceRealtimeWebSocketHandler.TryCreateWorkerUri("http://worker:8080", localOnly, out var uri));
        Assert.Equal(expected, uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("file:///tmp/worker")]
    [InlineData("https://user:password@worker/")]
    [InlineData("https://worker/?secret=value")]
    [InlineData("https://worker/#fragment")]
    public void WorkerRouteRejectsInvalidEndpoint(string endpoint)
    {
        Assert.False(VoiceRealtimeWebSocketHandler.TryCreateWorkerUri(endpoint, true, out _));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"sessionId\":1}")]
    [InlineData("{\"sessionId\":\"session\",\"captureId\":\"00000000-0000-0000-0000-000000000001\",\"speechEpoch\":1,\"sampleRate\":\"24000\",\"channels\":1,\"bitsPerSample\":16}")]
    public void MalformedVoiceHandshakeIsRejected(string json)
    {
        Assert.False(VoiceRealtimeWebSocketHandler.TryValidateHandshake(json, "session", out _, out _, out _));
    }

    [Fact]
    public void VoiceHandshakePreservesConnectionIdentity()
    {
        var connection = Ulid.NewUlid().ToString();
        var json = System.Text.Json.JsonSerializer.Serialize(new { sessionId = "session", captureId = Guid.NewGuid(),
            speechEpoch = 1, clientConnectionId = connection, sampleRate = 24000, channels = 1, bitsPerSample = 16 });
        Assert.True(VoiceRealtimeWebSocketHandler.TryValidateHandshake(json, "session", out _, out _, out var actual));
        Assert.Equal(connection, actual);
    }

    [Fact]
    public async Task CompletionAdmitsOnlyFinalTextWithoutApplicationSpecificRewriting()
    {
        var dispatcher = await RelayAsync(
            "{\"text\":\"A long unconfirmed partial transcript\"}",
            "{\"text\":\"toto waybridge\",\"final\":true}",
            "{\"type\":\"speechComplete\"}");

        var admission = Assert.Single(dispatcher.Admissions);
        Assert.Equal("toto waybridge", admission.Request.Message);
        Assert.Equal("voice:capture:1", admission.Request.ClientTaskId);
        Assert.Equal(admission.Request.ClientTaskId, admission.Key);
    }

    [Fact]
    public async Task SocketCloseAdmitsFinalTextOnce()
    {
        var dispatcher = await RelayAsync("{\"text\":\"show privacy\",\"final\":true}");
        Assert.Equal("show privacy", Assert.Single(dispatcher.Admissions).Request.Message);
    }

    [Fact]
    public async Task ProviderErrorDoesNotAdmitAnUnfinishedTurn()
    {
        var dispatcher = await RelayAsync(
            "{\"text\":\"show privacy\",\"final\":true}",
            "{\"type\":\"error\"}");
        Assert.Empty(dispatcher.Admissions);
    }

    [Theory]
    [InlineData("matching")]
    [InlineData("wrong")]
    [InlineData("ambiguous")]
    [InlineData("disconnected")]
    public async Task InteractiveVoiceRequiresTheSoleBoundConnection(string scenario)
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal);
        using var connection = sessions.RegisterBoundConnection(session)!;
        using var other = scenario == "ambiguous" ? sessions.RegisterBoundConnection(session) : null;
        if (scenario == "disconnected") connection.Dispose();
        var requested = scenario == "wrong" ? Ulid.NewUlid().ToString() : connection.ConnectionId;
        var dispatcher = new RecordingDispatcher();
        using var services = new ServiceCollection()
            .AddSingleton(sessions)
            .AddSingleton<IHostTaskExecutionDispatcher>(dispatcher)
            .AddSingleton<IHostTaskExecutor, Executor>()
            .AddSingleton<IHostTaskExecutionAuthorizer, Authorizer>()
            .BuildServiceProvider();
        using var worker = new ScriptedSocket(["{\"type\":\"speechComplete\",\"text\":\"show privacy\"}"]);
        using var browser = new ScriptedSocket([]);
        await VoiceRealtimeWebSocketHandler.RelayWorkerToBrowserAsync(worker, browser,
            new DefaultHttpContext { RequestServices = services }, session.Id.Value.ToString(), "capture", 1,
            tenant.Value, principal.Value, CancellationToken.None, NullLogger.Instance, requested);
        if (scenario == "matching")
        {
            var admission = Assert.Single(dispatcher.Admissions);
            Assert.Equal(HostTaskOrigin.MiniComposer, admission.Origin);
            Assert.Equal(connection.ConnectionId, admission.Request.BoundConnectionId);
        }
        else
        {
            Assert.Empty(dispatcher.Admissions);
            Assert.Contains(browser.Messages, message => message.Contains("\"type\":\"error\"", StringComparison.Ordinal));
        }
    }

    private static async Task<RecordingDispatcher> RelayAsync(params string[] events)
    {
        var dispatcher = new RecordingDispatcher();
        using var services = new ServiceCollection()
            .AddSingleton<IHostTaskExecutionDispatcher>(dispatcher)
            .AddSingleton<IHostTaskExecutor, Executor>()
            .AddSingleton<IHostTaskExecutionAuthorizer, Authorizer>()
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var worker = new ScriptedSocket(events);
        using var browser = new ScriptedSocket([]);
        await VoiceRealtimeWebSocketHandler.RelayWorkerToBrowserAsync(worker, browser, context,
            "session", "capture", 1, Ulid.NewUlid(), Ulid.NewUlid(), CancellationToken.None, NullLogger.Instance);
        return dispatcher;
    }

    private sealed class RecordingDispatcher : IHostTaskExecutionDispatcher
    {
        public List<(TaskRequest Request, string Key, HostTaskOrigin Origin)> Admissions { get; } = [];
        public Task<AdmissionResult> AcceptAsync(TaskRequest request, string key, string fingerprint, TenantId tenant,
            PrincipalId principal, CancellationToken cancellationToken, HostTaskOrigin origin = HostTaskOrigin.NonInteractive)
        {
            Admissions.Add((request, key, origin));
            return Task.FromResult(new AdmissionResult(ExecutionAdmissionStatus.Accepted, null));
        }
        public Task<TaskRecord?> CancelAsync(Ulid taskId, CancellationToken cancellationToken) => Task.FromResult<TaskRecord?>(null);
    }

    private sealed class Executor : IHostTaskExecutor
    {
        public Task ExecuteAsync(HostTaskExecution task, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Authorizer : IHostTaskExecutionAuthorizer
    {
        public Task AuthorizeAsync(HostTaskExecution task, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ScriptedSocket(IEnumerable<string> events) : WebSocket
    {
        private readonly Queue<string> _events = new(events);
        public List<string> Messages { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (!_events.TryDequeue(out var message))
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            var bytes = Encoding.UTF8.GetBytes(message);
            bytes.CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Messages.Add(Encoding.UTF8.GetString(buffer));
            return Task.CompletedTask;
        }
    }
}
