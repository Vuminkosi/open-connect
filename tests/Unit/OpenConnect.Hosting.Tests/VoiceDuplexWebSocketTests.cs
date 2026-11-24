using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Abstractions.Speech;
using OpenConnect.Agents.Execution;
using OpenConnect.Core;
using OpenConnect.Core.Configuration;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Hosting.AspNetCore.Voice;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class VoiceDuplexWebSocketTests
{
    [Theory]
    [InlineData(false, "ws://worker:8080/internal/v2/realtime")]
    [InlineData(true, "ws://worker:8080/internal/v2/realtime/local")]
    public void TranscriptionV2UriRoutesCloudPrimaryAndLocalFallback(bool localOnly, string expected)
    {
        Assert.True(VoiceDuplexSessionHandler.TryCreateTranscriptionV2Uri("http://worker:8080", localOnly, out var uri));
        Assert.Equal(expected, uri!.AbsoluteUri);
    }

    [Fact]
    public void KokoroStreamUriBuildsExpectedWebSocketRoute()
    {
        Assert.True(KokoroStreamingSpeechSynthesizer.TryCreateStreamUri("https://kokoro.internal:8443", out var uri));
        Assert.Equal("wss://kokoro.internal:8443/internal/v2/speech/stream", uri!.AbsoluteUri);
        Assert.False(KokoroStreamingSpeechSynthesizer.TryCreateStreamUri("file:///tmp/kokoro", out _));
    }

    [Fact]
    public async Task V2SubprotocolRejectedWhenContinuousConversationDisabled()
    {
        var (tenant, principal, sessions, session) = CreateSession();
        var services = new ServiceCollection()
            .AddSingleton(sessions)
            .AddSingleton<IOptions<OpenConnectVoiceOptions>>(Options.Create(new OpenConnectVoiceOptions
            {
                EnableContinuousConversation = false
            }))
            .BuildServiceProvider();

        var wsFeature = new FakeWebSocketFeature(["openconnect.voice.v2"], new ScriptedDuplexSocket([]));
        var httpContext = CreateAuthenticatedContext(services, tenant, principal, wsFeature);

        await VoiceRealtimeWebSocketHandler.HandleAsync(httpContext, session.Id.Value.ToString(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        Assert.Null(wsFeature.AcceptedSubProtocol);
    }

    [Fact]
    public async Task V2SubprotocolAcceptedWhenContinuousConversationEnabledAndEmitsSessionReady()
    {
        var (tenant, principal, sessions, session) = CreateSession();
        var options = new OpenConnectVoiceOptions
        {
            EnableContinuousConversation = true,
            EnableStreamingSpeechOutput = true
        };
        var coordinator = new DuplexVoiceSessionCoordinator(Options.Create(options));
        var stt = new MultiTurnFakeSttClient(["hello"]);
        var tts = new FakeStreamingSpeechSynthesizer();
        var services = BuildDuplexServices(sessions, options, coordinator, stt, tts, new FakeDispatcher(sessions));

        var openMsg = JsonSerializer.Serialize(new
        {
            version = 2,
            type = "session.open",
            sessionId = session.Id.Value.ToString(),
            voiceConnectionId = Ulid.NewUlid().ToString(),
            inputSampleRateHz = 24000,
            outputSampleRateHz = 24000,
            channels = 1
        });
        var closeMsg = """{"version":2,"type":"session.close"}""";
        var socket = new ScriptedDuplexSocket(
        [
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes(openMsg)),
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes(closeMsg))
        ]);
        var wsFeature = new FakeWebSocketFeature(["openconnect.voice.v2"], socket);
        var httpContext = CreateAuthenticatedContext(services, tenant, principal, wsFeature);

        await VoiceRealtimeWebSocketHandler.HandleAsync(httpContext, session.Id.Value.ToString(), CancellationToken.None);

        Assert.Equal("openconnect.voice.v2", wsFeature.AcceptedSubProtocol);
        Assert.Contains(socket.SentText, text => text.Contains("\"type\":\"session.ready\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task V1SubprotocolRemainsIntactWhenContinuousConversationDisabled()
    {
        var (tenant, principal, sessions, session) = CreateSession();
        var options = new OpenConnectVoiceOptions { EnableContinuousConversation = false };
        var stt = new MultiTurnFakeSttClient(["legacy v1 turn"]);
        var dispatcher = new FakeDispatcher(sessions);
        var services = BuildDuplexServices(sessions, options, new DuplexVoiceSessionCoordinator(Options.Create(options)), stt, null, dispatcher);

        var captureId = Guid.NewGuid().ToString();
        var handshake = JsonSerializer.Serialize(new
        {
            sessionId = session.Id.Value.ToString(),
            captureId,
            speechEpoch = 1,
            sampleRate = 24000,
            channels = 1,
            bitsPerSample = 16
        });
        var socket = new ScriptedDuplexSocket(
        [
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes(handshake)),
            (WebSocketMessageType.Binary, new byte[64]),
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes("""{"type":"endStream"}"""))
        ]);
        var wsFeature = new FakeWebSocketFeature(["openconnect.voice.v2", "openconnect.voice.v1"], socket);
        var httpContext = CreateAuthenticatedContext(services, tenant, principal, wsFeature);

        await VoiceRealtimeWebSocketHandler.HandleAsync(httpContext, session.Id.Value.ToString(), CancellationToken.None);

        Assert.Equal("openconnect.voice.v1", wsFeature.AcceptedSubProtocol);
        var admission = Assert.Single(dispatcher.Admissions);
        Assert.Equal("legacy v1 turn", admission.Request.Message);
        Assert.Equal($"voice:{captureId}:1", admission.Key);
    }

    [Fact]
    public async Task MultiTurnPersistentSessionCompletesFiveTurnsWithoutReconnect()
    {
        var (tenant, principal, sessions, session) = CreateSession();
        var options = new OpenConnectVoiceOptions
        {
            EnableContinuousConversation = true,
            EnableStreamingSpeechOutput = true,
            MinPhraseWords = 2,
            MaxPhraseWords = 10
        };
        var coordinator = new DuplexVoiceSessionCoordinator(Options.Create(options));
        var stt = new MultiTurnFakeSttClient(["turn one", "turn two", "turn three", "turn four", "turn five"]);
        var tts = new FakeStreamingSpeechSynthesizer();
        var dispatcher = new FakeDispatcher(sessions, autoRespond: true);
        var services = BuildDuplexServices(sessions, options, coordinator, stt, tts, dispatcher);

        var frames = new List<(WebSocketMessageType, byte[])>
        {
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                version = 2,
                type = "session.open",
                sessionId = session.Id.Value.ToString(),
                voiceConnectionId = Ulid.NewUlid().ToString()
            })))
        };

        for (var i = 0; i < 5; i++)
        {
            var captureId = $"cap-{i + 1}";
            frames.Add((WebSocketMessageType.Text, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                version = 2,
                type = "speech.started",
                captureId
            }))));
            frames.Add((WebSocketMessageType.Binary, EncodeMicFrame(speechEpoch: 1, sequence: i, sampleOffset: i * 480, pcmBytes: 960)));
            frames.Add((WebSocketMessageType.Text, Encoding.UTF8.GetBytes("""{"version":2,"type":"speech.ended"}""")));
        }

        // Allow response streaming tasks to flush before closing.
        var socket = new ScriptedDuplexSocket(frames, delayBeforeCloseMs: 250);
        var httpContext = CreateAuthenticatedContext(services, tenant, principal, new FakeWebSocketFeature(["openconnect.voice.v2"], socket));

        await VoiceDuplexSessionHandler.HandleSocketAsync(socket, httpContext, session.Id.Value.ToString(), tenant.Value, principal.Value, CancellationToken.None);

        Assert.Equal(5, dispatcher.Admissions.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal($"voice:cap-{i + 1}:1", dispatcher.Admissions[i].Key);
        }

        Assert.True(socket.SentText.Count(t => t.Contains("\"type\":\"transcript.partial\"", StringComparison.Ordinal)) >= 5);
        Assert.Equal(5, socket.SentText.Count(t => t.Contains("\"type\":\"transcript.final\"", StringComparison.Ordinal)));
        Assert.True(socket.SentBinary.Count > 0);

        // Verify every outbound binary frame decodes cleanly as TtsOutput.
        foreach (var bin in socket.SentBinary)
        {
            AssertValidTtsFrame(bin);
        }
    }

    [Fact]
    public async Task PartialRevisionsAreMonotonicAndDuplicateFinalsAdmitOnlyOnce()
    {
        var (tenant, principal, sessions, session) = CreateSession();
        var options = new OpenConnectVoiceOptions { EnableContinuousConversation = true };
        var coordinator = new DuplexVoiceSessionCoordinator(Options.Create(options));
        var stt = new DuplicateFinalFakeSttClient("first draft", "final answer");
        var dispatcher = new FakeDispatcher(sessions);
        var services = BuildDuplexServices(sessions, options, coordinator, stt, null, dispatcher);

        var captureId = "cap-dup";
        var socket = new ScriptedDuplexSocket(
        [
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = 2, type = "session.open", sessionId = session.Id.Value.ToString() }))),
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = 2, type = "speech.started", captureId }))),
            (WebSocketMessageType.Binary, EncodeMicFrame(1, 0, 0, 960)),
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes("""{"version":2,"type":"speech.ended"}""")),
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes("""{"version":2,"type":"session.close"}"""))
        ], delayBeforeCloseMs: 100);

        var httpContext = CreateAuthenticatedContext(services, tenant, principal, new FakeWebSocketFeature(["openconnect.voice.v2"], socket));
        await VoiceDuplexSessionHandler.HandleSocketAsync(socket, httpContext, session.Id.Value.ToString(), tenant.Value, principal.Value, CancellationToken.None);

        var admission = Assert.Single(dispatcher.Admissions);
        Assert.Equal("final answer", admission.Request.Message);
        Assert.Equal("voice:cap-dup:1", admission.Key);
        Assert.Contains(socket.SentText, t => t.Contains("\"type\":\"transcript.partial\"", StringComparison.Ordinal) && t.Contains("\"revision\":1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("bad-magic")]
    [InlineData("odd-bytes")]
    [InlineData("oversized")]
    public async Task MalformedOrOversizedBinaryFramesAreRejectedCleanly(string scenario)
    {
        var (tenant, principal, sessions, session) = CreateSession();
        var options = new OpenConnectVoiceOptions { EnableContinuousConversation = true };
        var coordinator = new DuplexVoiceSessionCoordinator(Options.Create(options));
        var services = BuildDuplexServices(sessions, options, coordinator, null, null, new FakeDispatcher(sessions));

        byte[] badFrame = scenario switch
        {
            "bad-magic" => new byte[64], // all zeros, invalid magic
            "odd-bytes" => BuildOddPayloadFrame(),
            "oversized" => new byte[VoiceBinaryFrameCodec.MaxFrameBytes + 2],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var socket = new ScriptedDuplexSocket(
        [
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = 2, type = "session.open", sessionId = session.Id.Value.ToString() }))),
            (WebSocketMessageType.Binary, badFrame)
        ]);
        var httpContext = CreateAuthenticatedContext(services, tenant, principal, new FakeWebSocketFeature(["openconnect.voice.v2"], socket));

        await VoiceDuplexSessionHandler.HandleSocketAsync(socket, httpContext, session.Id.Value.ToString(), tenant.Value, principal.Value, CancellationToken.None);

        Assert.Contains(socket.SentText, t => t.Contains("\"type\":\"session.error\"", StringComparison.Ordinal) && t.Contains("invalid_request", StringComparison.Ordinal));
        var expectedClose = scenario == "oversized" ? WebSocketCloseStatus.MessageTooBig : WebSocketCloseStatus.InvalidPayloadData;
        Assert.Equal(expectedClose, socket.ClosedStatus);
    }

    [Fact]
    public async Task InterruptAdvancesEpochReconcilesDeliveryAndCancelsActiveTask()
    {
        var (tenant, principal, sessions, session) = CreateSession();
        var options = new OpenConnectVoiceOptions
        {
            EnableContinuousConversation = true,
            EnableStreamingSpeechOutput = true
        };
        var coordinator = new DuplexVoiceSessionCoordinator(Options.Create(options));
        var dispatcher = new FakeDispatcher(sessions);
        var services = BuildDuplexServices(sessions, options, coordinator, null, null, dispatcher);

        var connId = Ulid.NewUlid().ToString();
        coordinator.OpenSession(tenant, principal, session.Id, connId);
        coordinator.StartResponse(tenant, principal, session.Id, "resp-1", speechEpoch: 1);
        coordinator.RegisterAudioSegment(tenant, principal, session.Id, "resp-1", 1, new VoiceSegmentMetadata("seg-0", 0, "First sentence.", "First sentence.", 0, 24000));
        coordinator.RegisterAudioSegment(tenant, principal, session.Id, "resp-1", 1, new VoiceSegmentMetadata("seg-1", 1, "Second sentence.", "Second sentence.", 24000, 24000));

        var interruptReq = JsonSerializer.Serialize(new
        {
            version = 2,
            type = "response.interrupt",
            requestId = "int-1",
            responseId = "resp-1",
            speechEpoch = 1,
            playedSamples = 24000,
            reason = "user_barge_in"
        });
        var socket = new ScriptedDuplexSocket(
        [
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = 2, type = "session.open", sessionId = session.Id.Value.ToString(), voiceConnectionId = connId }))),
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes(interruptReq)),
            // Duplicate requestId must replay idempotently without advancing epoch again.
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes(interruptReq)),
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes("""{"version":2,"type":"session.close"}"""))
        ]);

        var httpContext = CreateAuthenticatedContext(services, tenant, principal, new FakeWebSocketFeature(["openconnect.voice.v2"], socket));
        await VoiceDuplexSessionHandler.HandleSocketAsync(socket, httpContext, session.Id.Value.ToString(), tenant.Value, principal.Value, CancellationToken.None);

        var cancelledMessages = socket.SentText.Where(t => t.Contains("\"type\":\"response.cancelled\"", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, cancelledMessages.Count);
        Assert.Contains("\"speechEpoch\":2", cancelledMessages[0], StringComparison.Ordinal);
        Assert.Contains("\"confirmedSegmentCount\":1", cancelledMessages[0], StringComparison.Ordinal);
        Assert.Contains("\"unheardSegmentCount\":1", cancelledMessages[0], StringComparison.Ordinal);
        Assert.Contains("\"wasIdempotentReplay\":true", cancelledMessages[1], StringComparison.Ordinal);

        var snap = coordinator.GetSnapshot(tenant, principal, session.Id);
        Assert.NotNull(snap);
        Assert.Equal(2, snap!.SpeechEpoch);
    }

    [Fact]
    public async Task CrossTenantConnectionIsDeniedBeforeAccept()
    {
        var (ownerTenant, ownerPrincipal, sessions, session) = CreateSession();
        var otherTenant = new TenantId(Ulid.NewUlid());
        var services = new ServiceCollection()
            .AddSingleton(sessions)
            .AddSingleton<IOptions<OpenConnectVoiceOptions>>(Options.Create(new OpenConnectVoiceOptions { EnableContinuousConversation = true }))
            .BuildServiceProvider();

        var wsFeature = new FakeWebSocketFeature(["openconnect.voice.v2"], new ScriptedDuplexSocket([]));
        var httpContext = CreateAuthenticatedContext(services, otherTenant, ownerPrincipal, wsFeature);

        await VoiceRealtimeWebSocketHandler.HandleAsync(httpContext, session.Id.Value.ToString(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, httpContext.Response.StatusCode);
        Assert.Null(wsFeature.AcceptedSubProtocol);
        Assert.NotEqual(ownerTenant, otherTenant);
    }

    [Fact]
    public async Task DisconnectFencesConnectionAndAdvancesEpochWhenResponseActive()
    {
        var (tenant, principal, sessions, session) = CreateSession();
        var options = new OpenConnectVoiceOptions { EnableContinuousConversation = true };
        var coordinator = new DuplexVoiceSessionCoordinator(Options.Create(options));
        var services = BuildDuplexServices(sessions, options, coordinator, null, null, new FakeDispatcher(sessions));

        var connId = Ulid.NewUlid().ToString();
        coordinator.OpenSession(tenant, principal, session.Id, connId);
        coordinator.StartResponse(tenant, principal, session.Id, "resp-active", speechEpoch: 1);
        coordinator.RegisterAudioSegment(tenant, principal, session.Id, "resp-active", 1, new VoiceSegmentMetadata("seg-0", 0, "Playing audio.", "Playing audio.", 0, 12000));

        // Socket opens then immediately disconnects (Close).
        var socket = new ScriptedDuplexSocket(
        [
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = 2, type = "session.open", sessionId = session.Id.Value.ToString(), voiceConnectionId = connId })))
        ]);

        var httpContext = CreateAuthenticatedContext(services, tenant, principal, new FakeWebSocketFeature(["openconnect.voice.v2"], socket));
        await VoiceDuplexSessionHandler.HandleSocketAsync(socket, httpContext, session.Id.Value.ToString(), tenant.Value, principal.Value, CancellationToken.None);

        var snap = coordinator.GetSnapshot(tenant, principal, session.Id);
        Assert.NotNull(snap);
        Assert.Equal(VoiceConnectionState.Closed, snap!.ConnectionState);
        Assert.Equal(2, snap.SpeechEpoch);
        Assert.Null(snap.ActiveResponseId);
    }

    [Fact]
    public async Task MafHostTaskExecutorStreamsOrderedMessageDeltasForVoiceTasks()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal);
        var taskId = new TaskId(Ulid.NewUlid());
        var task = new HostTaskExecution(taskId, tenant, principal, session.Id, "Hello voice", "voice:cap-1:1");

        var streamingClient = new StreamingDeltaChatClient(["Hello ", "world, ", "ready to help."]);
        var factory = new StreamingExecutionFactory(streamingClient);
        var executor = new MafHostTaskExecutor(factory, sessions);

        await executor.ExecuteAsync(task, CancellationToken.None);

        var events = sessions.GetEvents(session.Id.Value, 0);
        var deltas = events.Where(e => e.Type == "message.delta").Select(e => e.Data).ToArray();
        Assert.Equal(["Hello ", "world, ", "ready to help."], deltas);
        var finalMsg = Assert.Single(events, e => e.Type == "message");
        Assert.Equal("Hello world, ready to help.", finalMsg.Data);
    }

    private static (TenantId Tenant, PrincipalId Principal, InMemorySessionStore Sessions, OpenConnect.Hosting.AspNetCore.SessionRecord Session) CreateSession()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal);
        return (tenant, principal, sessions, session);
    }

    private static ServiceProvider BuildDuplexServices(
        InMemorySessionStore sessions,
        OpenConnectVoiceOptions options,
        DuplexVoiceSessionCoordinator coordinator,
        IRealtimeSpeechToTextClient? stt,
        IStreamingSpeechSynthesizer? tts,
        IHostTaskExecutionDispatcher dispatcher)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(sessions);
        services.AddSingleton<IOptions<OpenConnectVoiceOptions>>(Options.Create(options));
        services.AddSingleton(coordinator);
        services.AddSingleton(dispatcher);
        services.AddSingleton<IHostTaskExecutor, NoOpExecutor>();
        services.AddSingleton<IHostTaskExecutionAuthorizer, NoOpAuthorizer>();
        if (stt is not null) services.AddSingleton(stt);
        if (tts is not null) services.AddSingleton(tts);
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext CreateAuthenticatedContext(
        IServiceProvider services,
        TenantId tenant,
        PrincipalId principal,
        FakeWebSocketFeature wsFeature)
    {
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("tenant_id", tenant.Value.ToString()),
            new Claim(ClaimTypes.NameIdentifier, principal.Value.ToString())
        ], "test"));
        ctx.Features.Set<IHttpWebSocketFeature>(wsFeature);
        if (wsFeature.RequestedProtocols.Count > 0)
        {
            ctx.Request.Headers.SecWebSocketProtocol = string.Join(", ", wsFeature.RequestedProtocols);
        }
        return ctx;
    }

    private static void AssertValidTtsFrame(byte[] frame)
    {
        Assert.True(VoiceBinaryFrameCodec.TryDecode(frame, out var header, out var payload));
        Assert.Equal(VoiceAudioStreamKind.TtsOutput, header.Kind);
        Assert.Equal(0, payload.Length % 2);
    }

    private static byte[] EncodeMicFrame(long speechEpoch, long sequence, long sampleOffset, int pcmBytes)
    {
        var pcm = new byte[pcmBytes];
        var frame = new byte[VoiceBinaryFrameCodec.HeaderSize + pcmBytes];
        var header = new VoiceAudioFrameHeader(VoiceAudioStreamKind.MicInput, 1, speechEpoch, sequence, sampleOffset);
        Assert.True(VoiceBinaryFrameCodec.TryEncode(header, pcm, frame, out _));
        return frame;
    }

    private static byte[] BuildOddPayloadFrame()
    {
        var valid = EncodeMicFrame(1, 0, 0, 4);
        var odd = new byte[valid.Length + 1];
        Buffer.BlockCopy(valid, 0, odd, 0, valid.Length);
        return odd;
    }

    private sealed class FakeWebSocketFeature(IReadOnlyList<string> requestedProtocols, WebSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public string? AcceptedSubProtocol { get; private set; }
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            AcceptedSubProtocol = context.SubProtocol;
            return Task.FromResult(socket);
        }
        public IReadOnlyList<string> RequestedProtocols { get; } = requestedProtocols;
    }

    private sealed class ScriptedDuplexSocket : WebSocket
    {
        private readonly Queue<(WebSocketMessageType Type, byte[] Payload)> _incoming;
        private readonly int _delayBeforeCloseMs;
        private WebSocketState _state = WebSocketState.Open;

        public ScriptedDuplexSocket(IEnumerable<(WebSocketMessageType, byte[])> incoming, int delayBeforeCloseMs = 0)
        {
            _incoming = new Queue<(WebSocketMessageType, byte[])>(incoming);
            _delayBeforeCloseMs = delayBeforeCloseMs;
        }

        public ConcurrentQueue<string> SentText { get; } = new();
        public ConcurrentQueue<byte[]> SentBinary { get; } = new();
        public WebSocketCloseStatus? ClosedStatus { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => ClosedStatus;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => "openconnect.voice.v2";

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            ClosedStatus = closeStatus;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() => _state = WebSocketState.Closed;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_incoming.Count == 0)
            {
                if (_delayBeforeCloseMs > 0)
                {
                    await Task.Delay(_delayBeforeCloseMs, cancellationToken).ConfigureAwait(false);
                }

                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "eof");
            }

            var (type, payload) = _incoming.Dequeue();
            var count = Math.Min(buffer.Count, payload.Length);
            if (payload.Length <= buffer.Count)
            {
                Buffer.BlockCopy(payload, 0, buffer.Array!, buffer.Offset, payload.Length);
                return new WebSocketReceiveResult(payload.Length, type, true);
            }

            // Fragment oversized payload across multiple ReceiveAsync calls so handler enforces max size cleanly.
            Buffer.BlockCopy(payload, 0, buffer.Array!, buffer.Offset, count);
            var remaining = new byte[payload.Length - count];
            Buffer.BlockCopy(payload, count, remaining, 0, remaining.Length);
            var list = _incoming.ToList();
            list.Insert(0, (type, remaining));
            _incoming.Clear();
            foreach (var item in list) _incoming.Enqueue(item);
            return new WebSocketReceiveResult(count, type, false);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (messageType == WebSocketMessageType.Text)
            {
                SentText.Enqueue(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            }
            else if (messageType == WebSocketMessageType.Binary)
            {
                var copy = new byte[buffer.Count];
                Buffer.BlockCopy(buffer.Array!, buffer.Offset, copy, 0, buffer.Count);
                SentBinary.Enqueue(copy);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class MultiTurnFakeSttClient(string[] transcripts) : IRealtimeSpeechToTextClient
    {
        private int _index;
        public bool IsConfigured => true;

        public async Task<RealtimeTranscriptionResult> TranscribePcmAsync(
            Stream pcm24kMono16Bit,
            CancellationToken cancellationToken = default,
            Func<RealtimePartialTranscript, CancellationToken, ValueTask>? onPartialTranscript = null,
            Func<RealtimeTranscriptTurn, CancellationToken, ValueTask>? onFinalTranscript = null)
        {
            var buf = new byte[1024];
            while (await pcm24kMono16Bit.ReadAsync(buf, cancellationToken).ConfigureAwait(false) > 0) { }

            var idx = Math.Min(Interlocked.Increment(ref _index) - 1, transcripts.Length - 1);
            var text = transcripts[idx];
            if (onPartialTranscript is not null)
            {
                await onPartialTranscript(new RealtimePartialTranscript(idx + 1, text), cancellationToken).ConfigureAwait(false);
            }

            if (onFinalTranscript is not null)
            {
                await onFinalTranscript(new RealtimeTranscriptTurn(idx + 1, text), cancellationToken).ConfigureAwait(false);
            }

            return new RealtimeTranscriptionResult("sdk-session", [new RealtimeTranscriptTurn(idx + 1, text)]);
        }
    }

    private sealed class DuplicateFinalFakeSttClient(string partial, string final) : IRealtimeSpeechToTextClient
    {
        public bool IsConfigured => true;

        public async Task<RealtimeTranscriptionResult> TranscribePcmAsync(
            Stream pcm24kMono16Bit,
            CancellationToken cancellationToken = default,
            Func<RealtimePartialTranscript, CancellationToken, ValueTask>? onPartialTranscript = null,
            Func<RealtimeTranscriptTurn, CancellationToken, ValueTask>? onFinalTranscript = null)
        {
            var buf = new byte[1024];
            while (await pcm24kMono16Bit.ReadAsync(buf, cancellationToken).ConfigureAwait(false) > 0) { }

            if (onPartialTranscript is not null)
            {
                await onPartialTranscript(new RealtimePartialTranscript(1, partial), cancellationToken).ConfigureAwait(false);
            }

            if (onFinalTranscript is not null)
            {
                await onFinalTranscript(new RealtimeTranscriptTurn(1, final), cancellationToken).ConfigureAwait(false);
                await onFinalTranscript(new RealtimeTranscriptTurn(1, final), cancellationToken).ConfigureAwait(false);
            }

            return new RealtimeTranscriptionResult("sdk-session", [new RealtimeTranscriptTurn(1, final)]);
        }
    }

    private sealed class FakeStreamingSpeechSynthesizer : IStreamingSpeechSynthesizer
    {
        public bool IsConfigured => true;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizePcmStreamAsync(
            string text,
            string? voice = null,
            string? locale = null,
            float speed = 1.0f,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new byte[960];
        }
    }

    private sealed class FakeDispatcher(InMemorySessionStore sessions, bool autoRespond = false) : IHostTaskExecutionDispatcher
    {
        public List<(TaskRequest Request, string Key)> Admissions { get; } = [];
        public List<Ulid> CancelledTasks { get; } = [];

        public Task<AdmissionResult> AcceptAsync(
            TaskRequest request,
            string key,
            string fingerprint,
            TenantId tenant,
            PrincipalId principal,
            CancellationToken cancellationToken,
            HostTaskOrigin origin = HostTaskOrigin.NonInteractive)
        {
            Admissions.Add((request, key));
            var record = new TaskRecord(new TaskId(Ulid.NewUlid()), tenant, principal, AgentRunState.Accepted);
            if (autoRespond && request.SessionId is not null)
            {
                var sessionUlid = Ulid.Parse(request.SessionId);
                _ = Task.Run(async () =>
                {
                    await sessions.AppendTaskEventAsync(sessionUlid, record.Id, "message.delta", $"Reply for {request.Message}. ", CancellationToken.None).ConfigureAwait(false);
                    await sessions.AppendTaskEventAsync(sessionUlid, record.Id, "message", $"Reply for {request.Message}.", CancellationToken.None).ConfigureAwait(false);
                    await sessions.AppendEventAsync(sessionUlid, "task.completed", JsonSerializer.Serialize(new { taskId = record.Id.Value.ToString(), state = "completed" }), CancellationToken.None).ConfigureAwait(false);
                }, CancellationToken.None);
            }

            return Task.FromResult(new AdmissionResult(ExecutionAdmissionStatus.Accepted, record));
        }

        public Task<TaskRecord?> CancelAsync(Ulid taskId, CancellationToken cancellationToken)
        {
            CancelledTasks.Add(taskId);
            return Task.FromResult<TaskRecord?>(null);
        }
    }

    private sealed class NoOpExecutor : IHostTaskExecutor
    {
        public Task ExecuteAsync(HostTaskExecution task, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpAuthorizer : IHostTaskExecutionAuthorizer
    {
        public Task AuthorizeAsync(HostTaskExecution task, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StreamingDeltaChatClient(string[] chunks) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("streaming-delta-test");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(chunks))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StreamingExecutionFactory(IChatClient client) : IHostTaskAgentExecutionFactory
    {
        public Task<HostTaskAgentExecution> CreateAsync(HostTaskExecution task, CancellationToken cancellationToken)
        {
            var binding = new NativeAgentSessionBinding(task.SessionId, task.TenantId, task.PrincipalId, "lead", "r1", "maf-test", "s1", "test");
            var context = new AgentExecutionContext
            {
                TenantScope = task.TenantId.Value.ToString(),
                PrincipalOrWorkloadScope = task.PrincipalId.Value.ToString(),
                IdentityKind = AgentIdentityMode.ActiveUser,
                SessionId = task.SessionId,
                TaskId = task.TaskId,
                AgentExecutionId = new ExecutionId(Ulid.NewUlid()),
                Depth = 0,
                CatalogRevision = 1,
                PolicyRevision = 1,
                AuthRevision = 1,
                Deadline = DateTimeOffset.UtcNow.AddMinutes(2),
                RemainingBudgets = ImmutableDictionary<string, long>.Empty,
                CancellationToken = cancellationToken
            };
            var agentFactory = new MafRealAgentFactory(new SingleAgentRegistry(), client);
            var runner = new MafConversationRunner(agentFactory, new AgentId("lead"));
            var persistence = new NativeAgentSessionPersistence(new InMemoryNativeStore(), new PassthroughProtector());
            var persistenceCtx = new NativeSessionTurnPersistenceContext(persistence, binding, new AlwaysGrantTurnGate());
            return Task.FromResult(new HostTaskAgentExecution(runner, context, (_, _) => ValueTask.FromResult(false), persistenceCtx));
        }

        private sealed class SingleAgentRegistry : IAgentDefinitionRegistry
        {
            private static readonly AgentDefinition Def = new(new AgentId("lead"), "Lead");
            public AgentDefinition? Get(AgentId id) => Def;
            public IReadOnlyList<AgentDefinition> GetAll() => [Def];
            public void Register(AgentDefinition value) => throw new NotSupportedException();
        }

        private sealed class InMemoryNativeStore : INativeAgentSessionStore
        {
            private NativeAgentSessionRecord? _record;
            public Task<NativeAgentSessionRecord?> GetAsync(NativeAgentSessionBinding binding, CancellationToken ct = default) => Task.FromResult(_record);
            public Task<NativeAgentSessionSaveOutcome> SaveAsync(NativeAgentSessionRecord record, long expectedVersion, CancellationToken ct = default)
            {
                _record = record;
                return Task.FromResult(NativeAgentSessionSaveOutcome.Saved);
            }
        }

        private sealed class PassthroughProtector : INativeAgentSessionProtector
        {
            public Task<byte[]> ProtectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) => Task.FromResult(plaintext.ToArray());
            public Task<byte[]> UnprotectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> ciphertext, CancellationToken ct = default) => Task.FromResult(ciphertext.ToArray());
        }

        private sealed class AlwaysGrantTurnGate : INativeSessionTurnGate
        {
            public Task<INativeSessionTurnOwnership?> TryAcquireAsync(NativeAgentSessionBinding binding, AgentExecutionContext context, CancellationToken ct = default) =>
                Task.FromResult<INativeSessionTurnOwnership?>(new DummyOwnership());
        }

        private sealed class DummyOwnership : INativeSessionTurnOwnership
        {
            public CancellationToken OwnershipLost => default;
            public Task ValidateOwnershipAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
