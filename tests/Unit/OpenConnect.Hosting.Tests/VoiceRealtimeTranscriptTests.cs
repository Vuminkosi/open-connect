using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Abstractions.Speech;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Hosting.AspNetCore.Voice;
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

    [Fact]
    public async Task ClosingAnAlreadyDisconnectedBrowserDoesNotThrow()
    {
        using var browser = new ScriptedSocket([]) { ThrowOnClose = true };
        await VoiceRealtimeWebSocketHandler.CloseAsync(browser, WebSocketCloseStatus.EndpointUnavailable,
            "transcription_unavailable", CancellationToken.None);
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

    [Fact]
    public async Task SdkSpeechToTextPathStreamsPcmEmitsSessionAndAdmitsFinalTranscript()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal);
        using var connection = sessions.RegisterBoundConnection(session)!;
        var captureId = Guid.NewGuid().ToString();
        var handshake = System.Text.Json.JsonSerializer.Serialize(new
        {
            sessionId = session.Id.Value.ToString(),
            captureId,
            speechEpoch = 3,
            clientConnectionId = connection.ConnectionId,
            sampleRate = 24000,
            channels = 1,
            bitsPerSample = 16
        });

        var dispatcher = new RecordingDispatcher();
        using var services = new ServiceCollection()
            .AddSingleton(sessions)
            .AddSingleton<IHostTaskExecutionDispatcher>(dispatcher)
            .AddSingleton<IHostTaskExecutor, Executor>()
            .AddSingleton<IHostTaskExecutionAuthorizer, Authorizer>()
            .BuildServiceProvider();

        var sttClient = new FakeRealtimeSpeechToTextClient(
            partialText: "open privacy",
            finalText: "open privacy firewall");

        using var browser = new ScriptedFrameSocket(
        [
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes(handshake)),
            (WebSocketMessageType.Binary, new byte[64]),
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes("""{"type":"endStream"}"""))
        ]);

        await VoiceRealtimeWebSocketHandler.HandleSdkSpeechToTextAsync(
            browser,
            sttClient,
            new DefaultHttpContext { RequestServices = services },
            session.Id.Value.ToString(),
            tenant.Value,
            principal.Value,
            CancellationToken.None);

        Assert.Equal(64, sttClient.ReceivedPcmBytes);
        Assert.Contains(browser.Messages, m => m.Contains("\"type\":\"session\"", StringComparison.Ordinal) && m.Contains("\"source\":\"sdk-stt\"", StringComparison.Ordinal));
        Assert.Contains(browser.Messages, m => m.Contains("\"type\":\"transcript-partial\"", StringComparison.Ordinal) && m.Contains("open privacy", StringComparison.Ordinal));
        Assert.Contains(browser.Messages, m => m.Contains("\"type\":\"transcript-final\"", StringComparison.Ordinal) && m.Contains("open privacy firewall", StringComparison.Ordinal));

        var admission = Assert.Single(dispatcher.Admissions);
        Assert.Equal("open privacy firewall", admission.Request.Message);
        Assert.Equal($"voice:{captureId}:3", admission.Key);
        Assert.Equal(connection.ConnectionId, admission.Request.BoundConnectionId);
        Assert.Equal(HostTaskOrigin.MiniComposer, admission.Origin);
    }

    [Fact]
    public async Task SdkSpeechToTextPathRejectsInvalidHandshakeWithoutCallingClient()
    {
        var sttClient = new FakeRealtimeSpeechToTextClient("ignored", "ignored");
        using var browser = new ScriptedFrameSocket(
        [
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes("""{"sessionId":"wrong"}"""))
        ]);

        await VoiceRealtimeWebSocketHandler.HandleSdkSpeechToTextAsync(
            browser,
            sttClient,
            new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() },
            Ulid.NewUlid().ToString(),
            Ulid.NewUlid(),
            Ulid.NewUlid(),
            CancellationToken.None);

        Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, browser.ClosedStatus);
        Assert.Equal("invalid_handshake", browser.ClosedReason);
        Assert.False(sttClient.WasCalled);
    }

    [Fact]
    public void HostingAddOpenConnectSpeechToTextBindsIConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenConnect:ASR_WSS"] = "wss://host.example.test/realtime",
                ["OpenConnect:ApiKey"] = "host-key"
            })
            .Build();

        using var services = new ServiceCollection()
            .AddOpenConnectSpeechToText(config)
            .BuildServiceProvider();

        Assert.True(services.GetRequiredService<IRealtimeSpeechToTextClient>().IsConfigured);
        Assert.IsType<SileroOnnxVoiceActivityDetector>(services.GetRequiredService<IVoiceActivityDetector>());
    }

    [Fact]
    public async Task SileroOnnxVoiceActivityDetectorEvaluatesSilenceAndResetsOnSequenceGap()
    {
        using var detector = new SileroOnnxVoiceActivityDetector();
        using var session = detector.CreateSession();
        var silence = new float[512];

        var first = await session.AnalyzeAsync(new VoiceActivityFrame(0, 16_000, silence));
        Assert.Equal(0, first.Sequence);
        Assert.InRange(first.SpeechProbability, 0f, 1f);
        Assert.False(first.IsSpeech);
        Assert.False(first.StateWasReset);

        var afterGap = await session.AnalyzeAsync(new VoiceActivityFrame(5, 16_000, silence));
        Assert.Equal(5, afterGap.Sequence);
        Assert.True(afterGap.StateWasReset);
        Assert.False(afterGap.IsSpeech);
    }

    [Fact]
    public async Task SileroVoiceActivityAudioStreamResamples24kPcm16AndEvaluatesFrames()
    {
        using var detector = new SileroOnnxVoiceActivityDetector();
        using var stream = new SileroVoiceActivityAudioStream(detector);

        // 1,600 samples at 24 kHz = 3,200 bytes -> ~1,066 samples at 16 kHz -> 2 Silero 512-sample frames.
        var silent24kPcm16 = new byte[3200];
        var results = await stream.AppendPcm16Async(silent24kPcm16, 24_000);

        Assert.Equal(2, results.Count);
        Assert.Equal(0, results[0].Sequence);
        Assert.Equal(1, results[1].Sequence);
        Assert.All(results, r =>
        {
            Assert.False(r.IsSpeech);
            Assert.InRange(r.SpeechProbability, 0f, 1f);
            Assert.True(r.AudioStartSample >= 0);
            Assert.True(r.AudioEndSample > r.AudioStartSample);
        });
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
        public bool ThrowOnClose { get; init; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => ThrowOnClose ? Task.FromException(new WebSocketException()) : Task.CompletedTask;
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

    private sealed class ScriptedFrameSocket(IEnumerable<(WebSocketMessageType Type, byte[] Payload)> frames) : WebSocket
    {
        private readonly Queue<(WebSocketMessageType Type, byte[] Payload)> _frames = new(frames);
        public List<string> Messages { get; } = [];
        public WebSocketCloseStatus? ClosedStatus { get; private set; }
        public string? ClosedReason { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => ClosedStatus;
        public override string? CloseStatusDescription => ClosedReason;
        public override WebSocketState State => ClosedStatus.HasValue ? WebSocketState.Closed : WebSocketState.Open;
        public override string? SubProtocol => "openconnect.voice.v1";
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            ClosedStatus = closeStatus;
            ClosedReason = statusDescription;
            return Task.CompletedTask;
        }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (!_frames.TryDequeue(out var frame))
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            frame.Payload.CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(frame.Payload.Length, frame.Type, true));
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Messages.Add(Encoding.UTF8.GetString(buffer));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRealtimeSpeechToTextClient(string partialText, string finalText) : IRealtimeSpeechToTextClient
    {
        public bool IsConfigured => true;
        public bool WasCalled { get; private set; }
        public long ReceivedPcmBytes { get; private set; }

        public async Task<RealtimeTranscriptionResult> TranscribePcmAsync(
            Stream pcm24kMono16Bit,
            CancellationToken cancellationToken = default,
            Func<RealtimePartialTranscript, CancellationToken, ValueTask>? onPartialTranscript = null,
            Func<RealtimeTranscriptTurn, CancellationToken, ValueTask>? onFinalTranscript = null)
        {
            WasCalled = true;
            var buffer = new byte[1024];
            int read;
            while ((read = await pcm24kMono16Bit.ReadAsync(buffer, cancellationToken)) > 0)
            {
                ReceivedPcmBytes += read;
            }

            if (onPartialTranscript is not null)
                await onPartialTranscript(new RealtimePartialTranscript(0, partialText), cancellationToken);

            var turn = new RealtimeTranscriptTurn(0, finalText);
            if (onFinalTranscript is not null)
                await onFinalTranscript(turn, cancellationToken);

            return new RealtimeTranscriptionResult("sdk-session", [turn]);
        }
    }
}
