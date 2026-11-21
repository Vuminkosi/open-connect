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
        public List<(TaskRequest Request, string Key)> Admissions { get; } = [];
        public Task<AdmissionResult> AcceptAsync(TaskRequest request, string key, string fingerprint, TenantId tenant,
            PrincipalId principal, CancellationToken cancellationToken, HostTaskOrigin origin = HostTaskOrigin.NonInteractive)
        {
            Admissions.Add((request, key));
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
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
