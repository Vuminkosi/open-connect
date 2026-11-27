using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
using OpenConnect.Privacy;
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

    [Fact]
    public async Task LongResponseEmitsFirstAudioBeforeAgentTaskCompletes_LV05()
    {
        var (tenant, principal, sessions, session) = CreateSession();
        var options = new OpenConnectVoiceOptions
        {
            EnableContinuousConversation = true,
            EnableStreamingSpeechOutput = true,
            MinPhraseWords = 3,
            MaxPhraseWords = 12
        };
        var coordinator = new DuplexVoiceSessionCoordinator(Options.Create(options));
        var stt = new MultiTurnFakeSttClient(["tell me a long story"]);
        var tts = new FakeStreamingSpeechSynthesizer();
        var allowAgentCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentCompleted = false;
        var dispatcher = new CustomActionDispatcher((req, record) =>
        {
            var sessionUlid = Ulid.Parse(req.SessionId!);
            _ = Task.Run(async () =>
            {
                await sessions.AppendTaskEventAsync(sessionUlid, record.Id, "message.delta", "First sentence is ready to speak now. ", CancellationToken.None).ConfigureAwait(false);
                await allowAgentCompletion.Task.ConfigureAwait(false);
                await sessions.AppendTaskEventAsync(sessionUlid, record.Id, "message.delta", "Second sentence completes the long response.", CancellationToken.None).ConfigureAwait(false);
                await sessions.AppendTaskEventAsync(sessionUlid, record.Id, "message", "First sentence is ready to speak now. Second sentence completes the long response.", CancellationToken.None).ConfigureAwait(false);
                agentCompleted = true;
                await sessions.AppendEventAsync(sessionUlid, "task.completed", JsonSerializer.Serialize(new { taskId = record.Id.Value.ToString(), state = "completed" }), CancellationToken.None).ConfigureAwait(false);
            }, CancellationToken.None);
        });
        var services = BuildDuplexServices(sessions, options, coordinator, stt, tts, dispatcher);

        var socket = new InteractiveDuplexSocket();
        var httpContext = CreateAuthenticatedContext(services, tenant, principal, new FakeWebSocketFeature(["openconnect.voice.v2"], socket));
        var handlerTask = VoiceDuplexSessionHandler.HandleSocketAsync(socket, httpContext, session.Id.Value.ToString(), tenant.Value, principal.Value, CancellationToken.None);

        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "session.open", sessionId = session.Id.Value.ToString() }));
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"session.ready\"", StringComparison.Ordinal));

        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "speech.started", captureId = "cap-lv05" }));
        socket.EnqueueBinary(EncodeMicFrame(1, 0, 0, 960));
        socket.EnqueueText("""{"version":2,"type":"speech.ended"}""");

        // First audio frame MUST arrive while the agent task is still blocked before completion.
        await socket.WaitForBinaryCountAsync(1);
        Assert.False(agentCompleted);
        Assert.False(allowAgentCompletion.Task.IsCompleted);

        // Now allow agent task to complete and verify ordered completion.
        allowAgentCompletion.TrySetResult();
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"response.completed\"", StringComparison.Ordinal));
        socket.EnqueueText("""{"version":2,"type":"session.close"}""");
        await handlerTask;

        AssertMonotonicTtsFrames(socket.SentBinary);
    }

    private static void AssertMonotonicTtsFrames(IEnumerable<byte[]> frames)
    {
        long lastSeq = -1;
        long lastSampleOffset = -1;
        foreach (var bin in frames)
        {
            Assert.True(VoiceBinaryFrameCodec.TryDecode(bin, out var header, out var payload));
            Assert.Equal(VoiceAudioStreamKind.TtsOutput, header.Kind);
            Assert.True(header.Sequence > lastSeq);
            Assert.True(header.SampleOffset > lastSampleOffset);
            lastSeq = header.Sequence;
            lastSampleOffset = header.SampleOffset;
            Assert.True(payload.Length > 0 && payload.Length % 2 == 0);
        }
    }

    [Fact]
    public async Task InterruptBeforeFirstAudioChunkEmitsZeroObsoletePlayback_LV07()
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
        var stt = new MultiTurnFakeSttClient(["start slow answer"]);
        var synthesisEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSynthesis = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tts = new GatedStreamingSpeechSynthesizer(synthesisEntered, releaseSynthesis);
        var dispatcher = new FakeDispatcher(sessions, autoRespond: true);
        var services = BuildDuplexServices(sessions, options, coordinator, stt, tts, dispatcher);

        var socket = new InteractiveDuplexSocket();
        var httpContext = CreateAuthenticatedContext(services, tenant, principal, new FakeWebSocketFeature(["openconnect.voice.v2"], socket));
        var handlerTask = VoiceDuplexSessionHandler.HandleSocketAsync(socket, httpContext, session.Id.Value.ToString(), tenant.Value, principal.Value, CancellationToken.None);

        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "session.open", sessionId = session.Id.Value.ToString() }));
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"session.ready\"", StringComparison.Ordinal));

        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "speech.started", captureId = "cap-lv07" }));
        socket.EnqueueBinary(EncodeMicFrame(1, 0, 0, 960));
        socket.EnqueueText("""{"version":2,"type":"speech.ended"}""");

        // Wait until TTS has been invoked but before any audio chunk is yielded.
        await synthesisEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(socket.SentBinary);

        socket.EnqueueText(JsonSerializer.Serialize(new
        {
            version = 2,
            type = "response.interrupt",
            requestId = "int-lv07",
            speechEpoch = 1,
            playedSamples = 0,
            reason = "user_barge_in"
        }));

        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"response.cancelled\"", StringComparison.Ordinal));
        releaseSynthesis.TrySetResult();
        await Task.Delay(80);
        socket.EnqueueText("""{"version":2,"type":"session.close"}""");
        await handlerTask;

        Assert.Empty(socket.SentBinary);
        Assert.DoesNotContain(socket.SentText, t => t.Contains("\"type\":\"response.audio.segment\"", StringComparison.Ordinal));
        Assert.NotEmpty(dispatcher.CancelledTasks);
    }

    [Fact]
    public async Task HesitationIncompleteSentenceDoesNotPrematurelyAdmitTurnAtGateway_LV08()
    {
        var (tenant, principal, sessions, session) = CreateSession();
        var options = new OpenConnectVoiceOptions { EnableContinuousConversation = true };
        var coordinator = new DuplexVoiceSessionCoordinator(Options.Create(options));
        var partialSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stt = new HesitationStreamingFakeSttClient("Can you show me the... umm...", partialSent);
        var vad = new ScriptedVadDetector(speechFrameCount: 1);
        var dispatcher = new FakeDispatcher(sessions);
        var services = BuildDuplexServices(sessions, options, coordinator, stt, null, dispatcher, s => s.AddSingleton<IVoiceActivityDetector>(vad));

        var socket = new InteractiveDuplexSocket();
        var httpContext = CreateAuthenticatedContext(services, tenant, principal, new FakeWebSocketFeature(["openconnect.voice.v2"], socket));
        var handlerTask = VoiceDuplexSessionHandler.HandleSocketAsync(socket, httpContext, session.Id.Value.ToString(), tenant.Value, principal.Value, CancellationToken.None);

        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "session.open", sessionId = session.Id.Value.ToString() }));
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"session.ready\"", StringComparison.Ordinal));

        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "speech.started", captureId = "cap-lv08" }));
        // 1600 bytes = 800 samples at 24 kHz -> >512 samples at 16 kHz, triggers 1 Silero VAD frame (IsSpeech = true).
        socket.EnqueueBinary(EncodeMicFrame(1, 0, 0, 1600));
        await partialSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"transcript.partial\"", StringComparison.Ordinal));

        // Feed silence frames across ~550ms (> normal 450ms threshold, < 1400ms hesitation threshold).
        socket.EnqueueBinary(EncodeMicFrame(1, 1, 800, 1600));
        await Task.Delay(300);
        socket.EnqueueBinary(EncodeMicFrame(1, 2, 1600, 1600));
        await Task.Delay(250);
        socket.EnqueueBinary(EncodeMicFrame(1, 3, 2400, 1600));
        await Task.Delay(100);

        // Hesitation MUST NOT have prematurely triggered EndActiveUtteranceAsync / turn admission.
        Assert.Empty(dispatcher.Admissions);

        socket.EnqueueText("""{"version":2,"type":"session.close"}""");
        await handlerTask;
        Assert.Empty(dispatcher.Admissions);
    }

    [Fact]
    public async Task WorkerTimeoutAndOverloadCancelsSafelyAndRecoversOnNextTurn_LV16()
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
        var stt = new MultiTurnFakeSttClient(["first turn times out", "second turn succeeds"]);
        var tts = new FaultingThenRecoveringSynthesizer(new TimeoutException("Kokoro worker timed out."));
        var dispatcher = new FakeDispatcher(sessions, autoRespond: true);
        var services = BuildDuplexServices(sessions, options, coordinator, stt, tts, dispatcher);

        var socket = new InteractiveDuplexSocket();
        var httpContext = CreateAuthenticatedContext(services, tenant, principal, new FakeWebSocketFeature(["openconnect.voice.v2"], socket));
        var handlerTask = VoiceDuplexSessionHandler.HandleSocketAsync(socket, httpContext, session.Id.Value.ToString(), tenant.Value, principal.Value, CancellationToken.None);

        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "session.open", sessionId = session.Id.Value.ToString() }));
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"session.ready\"", StringComparison.Ordinal));

        // Turn 1: worker times out -> safe recoverable response.error emitted, socket remains open.
        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "speech.started", captureId = "cap-t1" }));
        socket.EnqueueBinary(EncodeMicFrame(1, 0, 0, 960));
        socket.EnqueueText("""{"version":2,"type":"speech.ended"}""");
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"response.error\"", StringComparison.Ordinal) && t.Contains("\"recoverable\":true", StringComparison.Ordinal));

        // Turn 2: session recovers cleanly and streams valid audio.
        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "speech.started", captureId = "cap-t2" }));
        socket.EnqueueBinary(EncodeMicFrame(1, 1, 480, 960));
        socket.EnqueueText("""{"version":2,"type":"speech.ended"}""");
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"response.completed\"", StringComparison.Ordinal));
        await socket.WaitForBinaryCountAsync(1);

        socket.EnqueueText("""{"version":2,"type":"session.close"}""");
        await handlerTask;

        Assert.Equal(2, dispatcher.Admissions.Count);
        Assert.NotEmpty(socket.SentBinary);
    }

    [Fact]
    public async Task PiiSplitAcrossDeltasIsScreenedAndRedactedBeforeTts_LV18()
    {
        var (tenant, principal, sessions, session) = CreateSession();
        var options = new OpenConnectVoiceOptions
        {
            EnableContinuousConversation = true,
            EnableStreamingSpeechOutput = true,
            MinPhraseWords = 3,
            MaxPhraseWords = 15
        };
        var coordinator = new DuplexVoiceSessionCoordinator(Options.Create(options));
        var stt = new MultiTurnFakeSttClient(["what is my account info"]);
        var tts = new RecordingStreamingSpeechSynthesizer();
        var egressGate = new RegexPiiEgressGate(blockOnMatch: false);
        var dispatcher = new CustomActionDispatcher((req, record) =>
        {
            var sessionUlid = Ulid.Parse(req.SessionId!);
            _ = Task.Run(async () =>
            {
                // SSN is split right across two separate text deltas.
                await sessions.AppendTaskEventAsync(sessionUlid, record.Id, "message.delta", "Your SSN is 123-45-", CancellationToken.None).ConfigureAwait(false);
                await sessions.AppendTaskEventAsync(sessionUlid, record.Id, "message.delta", "6789 on file. ", CancellationToken.None).ConfigureAwait(false);
                await sessions.AppendTaskEventAsync(sessionUlid, record.Id, "message", "Your SSN is 123-45-6789 on file.", CancellationToken.None).ConfigureAwait(false);
                await sessions.AppendEventAsync(sessionUlid, "task.completed", JsonSerializer.Serialize(new { taskId = record.Id.Value.ToString(), state = "completed" }), CancellationToken.None).ConfigureAwait(false);
            }, CancellationToken.None);
        });
        var services = BuildDuplexServices(sessions, options, coordinator, stt, tts, dispatcher, s => s.AddSingleton<IEgressGate>(egressGate));

        var socket = new InteractiveDuplexSocket();
        var httpContext = CreateAuthenticatedContext(services, tenant, principal, new FakeWebSocketFeature(["openconnect.voice.v2"], socket));
        var handlerTask = VoiceDuplexSessionHandler.HandleSocketAsync(socket, httpContext, session.Id.Value.ToString(), tenant.Value, principal.Value, CancellationToken.None);

        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "session.open", sessionId = session.Id.Value.ToString() }));
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"session.ready\"", StringComparison.Ordinal));

        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "speech.started", captureId = "cap-pii-delta" }));
        socket.EnqueueBinary(EncodeMicFrame(1, 0, 0, 960));
        socket.EnqueueText("""{"version":2,"type":"speech.ended"}""");
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"response.completed\"", StringComparison.Ordinal));

        socket.EnqueueText("""{"version":2,"type":"session.close"}""");
        await handlerTask;

        Assert.NotEmpty(tts.ReceivedTexts);
        Assert.All(tts.ReceivedTexts, text =>
        {
            Assert.DoesNotContain("123-45-6789", text, StringComparison.Ordinal);
            Assert.DoesNotContain("123-45-", text, StringComparison.Ordinal);
        });
        Assert.Contains(tts.ReceivedTexts, text => text.Contains("[REDACTED]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PiiDeniedAcrossSegmentBoundaryCancelsPendingAudioAndBlocksTts_LV18()
    {
        var (tenant, principal, sessions, session) = CreateSession();
        var options = new OpenConnectVoiceOptions
        {
            EnableContinuousConversation = true,
            EnableStreamingSpeechOutput = true,
            MinPhraseWords = 2,
            MaxPhraseWords = 5
        };
        var coordinator = new DuplexVoiceSessionCoordinator(Options.Create(options));
        var stt = new MultiTurnFakeSttClient(["share contact"]);
        var tts = new RecordingStreamingSpeechSynthesizer();
        var egressGate = new RegexPiiEgressGate(blockOnMatch: true);
        var dispatcher = new CustomActionDispatcher((req, record) =>
        {
            var sessionUlid = Ulid.Parse(req.SessionId!);
            _ = Task.Run(async () =>
            {
                // First segment ends with "123 45.", second segment starts with "6789" -> combined SSN across segment boundary!
                await sessions.AppendTaskEventAsync(sessionUlid, record.Id, "message.delta", "Code is 123 45. ", CancellationToken.None).ConfigureAwait(false);
                await sessions.AppendTaskEventAsync(sessionUlid, record.Id, "message.delta", "6789 is the rest. ", CancellationToken.None).ConfigureAwait(false);
                await sessions.AppendTaskEventAsync(sessionUlid, record.Id, "message.delta", "This third segment must never be spoken.", CancellationToken.None).ConfigureAwait(false);
                await sessions.AppendEventAsync(sessionUlid, "task.completed", JsonSerializer.Serialize(new { taskId = record.Id.Value.ToString(), state = "completed" }), CancellationToken.None).ConfigureAwait(false);
            }, CancellationToken.None);
        });
        var services = BuildDuplexServices(sessions, options, coordinator, stt, tts, dispatcher, s => s.AddSingleton<IEgressGate>(egressGate));

        var socket = new InteractiveDuplexSocket();
        var httpContext = CreateAuthenticatedContext(services, tenant, principal, new FakeWebSocketFeature(["openconnect.voice.v2"], socket));
        var handlerTask = VoiceDuplexSessionHandler.HandleSocketAsync(socket, httpContext, session.Id.Value.ToString(), tenant.Value, principal.Value, CancellationToken.None);

        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "session.open", sessionId = session.Id.Value.ToString() }));
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"session.ready\"", StringComparison.Ordinal));

        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "speech.started", captureId = "cap-pii-seg" }));
        socket.EnqueueBinary(EncodeMicFrame(1, 0, 0, 960));
        socket.EnqueueText("""{"version":2,"type":"speech.ended"}""");
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"response.error\"", StringComparison.Ordinal) && t.Contains(ErrorCodes.PrivacyBlocked, StringComparison.Ordinal));

        socket.EnqueueText("""{"version":2,"type":"session.close"}""");
        await handlerTask;

        Assert.DoesNotContain(tts.ReceivedTexts, text => text.Contains("6789", StringComparison.Ordinal));
        Assert.DoesNotContain(tts.ReceivedTexts, text => text.Contains("third segment", StringComparison.Ordinal));
        Assert.Contains(socket.SentText, t => t.Contains("\"type\":\"response.error\"", StringComparison.Ordinal) && t.Contains(ErrorCodes.PrivacyBlocked, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("config-local-only")]
    [InlineData("privacy-local-only")]
    [InlineData("privacy-strict-local")]
    public async Task LocalOnlyAndStrictLocalPrivacyNeverInvokeExternalCloudStt_LV19(string mode)
    {
        var (tenant, principal, sessions, session) = CreateSession();
        var options = new OpenConnectVoiceOptions { EnableContinuousConversation = true };
        var coordinator = new DuplexVoiceSessionCoordinator(Options.Create(options));
        var cloudStt = new TrackingCloudSttClient();
        var dispatcher = new FakeDispatcher(sessions);
        var services = BuildDuplexServices(sessions, options, coordinator, cloudStt, null, dispatcher, s =>
        {
            if (mode == "config-local-only")
            {
                var cfg = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["Workers:Transcription:RealtimeLocalOnly"] = "true" })
                    .Build();
                s.AddSingleton<IConfiguration>(cfg);
            }
            else if (mode == "privacy-local-only")
            {
                s.AddSingleton<IOptions<OpenConnect.Privacy.PrivacyOptions>>(Options.Create(new OpenConnect.Privacy.PrivacyOptions
                {
                    Enabled = true,
                    Mode = FirewallMode.LocalOnly
                }));
            }
            else if (mode == "privacy-strict-local")
            {
                var coreOpts = new OpenConnectOptions();
                coreOpts.Privacy.Enabled = true;
                coreOpts.Privacy.Mode = FirewallMode.StrictLocal;
                s.AddSingleton<IOptions<OpenConnectOptions>>(Options.Create(coreOpts));
            }
        });

        var socket = new ScriptedDuplexSocket(
        [
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = 2, type = "session.open", sessionId = session.Id.Value.ToString() }))),
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = 2, type = "speech.started", captureId = "cap-lv19" }))),
            (WebSocketMessageType.Binary, EncodeMicFrame(1, 0, 0, 960)),
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes("""{"version":2,"type":"speech.ended"}""")),
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes("""{"version":2,"type":"session.close"}"""))
        ], delayBeforeCloseMs: 60);

        var httpContext = CreateAuthenticatedContext(services, tenant, principal, new FakeWebSocketFeature(["openconnect.voice.v2"], socket));
        await VoiceDuplexSessionHandler.HandleSocketAsync(socket, httpContext, session.Id.Value.ToString(), tenant.Value, principal.Value, CancellationToken.None);

        Assert.Equal(0, cloudStt.InvocationCount);
        Assert.Contains(socket.SentText, t => t.Contains("\"type\":\"session.ready\"", StringComparison.Ordinal) && t.Contains("\"sttProvider\":\"unconfigured\"", StringComparison.Ordinal));
        Assert.DoesNotContain(socket.SentText, t => t.Contains("cloud-muse-sdk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfirmationRequiredAndToolEventsForwardedOntoV2SocketWithoutSpeakingToolPayloads_LV21()
    {
        var (tenant, principal, sessions, session) = CreateSession();
        var options = new OpenConnectVoiceOptions
        {
            EnableContinuousConversation = true,
            EnableStreamingSpeechOutput = true,
            MinPhraseWords = 2,
            MaxPhraseWords = 12
        };
        var coordinator = new DuplexVoiceSessionCoordinator(Options.Create(options));
        var stt = new MultiTurnFakeSttClient(["delete completed tasks"]);
        var tts = new RecordingStreamingSpeechSynthesizer();
        var dispatcher = new CustomActionDispatcher((req, record) =>
        {
            var sessionUlid = Ulid.Parse(req.SessionId!);
            _ = Task.Run(async () =>
            {
                await sessions.AppendTaskEventAsync(sessionUlid, record.Id, "tool.started", JsonSerializer.Serialize(new { toolName = "todo.delete", callId = "call-1", status = "running", secretArgs = "DO_NOT_SPEAK_ARGS" }), CancellationToken.None).ConfigureAwait(false);
                await sessions.AppendTaskEventAsync(sessionUlid, record.Id, "confirmation.required", JsonSerializer.Serialize(new { toolName = "todo.delete", actionId = "act-99", summary = "Confirm deleting 3 completed tasks." }), CancellationToken.None).ConfigureAwait(false);
                await sessions.AppendTaskEventAsync(sessionUlid, record.Id, "tool.completed", JsonSerializer.Serialize(new { toolName = "todo.delete", callId = "call-1", status = "completed", rawResult = "INTERNAL_DB_ROW_JSON" }), CancellationToken.None).ConfigureAwait(false);
                await sessions.AppendTaskEventAsync(sessionUlid, record.Id, "message", "Done deleting your completed tasks.", CancellationToken.None).ConfigureAwait(false);
                await sessions.AppendEventAsync(sessionUlid, "task.completed", JsonSerializer.Serialize(new { taskId = record.Id.Value.ToString(), state = "completed" }), CancellationToken.None).ConfigureAwait(false);
            }, CancellationToken.None);
        });
        var services = BuildDuplexServices(sessions, options, coordinator, stt, tts, dispatcher);

        var socket = new InteractiveDuplexSocket();
        var httpContext = CreateAuthenticatedContext(services, tenant, principal, new FakeWebSocketFeature(["openconnect.voice.v2"], socket));
        var handlerTask = VoiceDuplexSessionHandler.HandleSocketAsync(socket, httpContext, session.Id.Value.ToString(), tenant.Value, principal.Value, CancellationToken.None);

        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "session.open", sessionId = session.Id.Value.ToString() }));
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"session.ready\"", StringComparison.Ordinal));

        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "speech.started", captureId = "cap-lv21" }));
        socket.EnqueueBinary(EncodeMicFrame(1, 0, 0, 960));
        socket.EnqueueText("""{"version":2,"type":"speech.ended"}""");
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"response.completed\"", StringComparison.Ordinal));

        socket.EnqueueText("""{"version":2,"type":"session.close"}""");
        await handlerTask;

        Assert.Contains(socket.SentText, t => t.Contains("\"type\":\"tool.started\"", StringComparison.Ordinal) && t.Contains("\"toolName\":\"todo.delete\"", StringComparison.Ordinal));
        Assert.Contains(socket.SentText, t => t.Contains("\"type\":\"confirmation.required\"", StringComparison.Ordinal) && t.Contains("\"actionId\":\"act-99\"", StringComparison.Ordinal));
        Assert.Contains(socket.SentText, t => t.Contains("\"type\":\"tool.completed\"", StringComparison.Ordinal) && t.Contains("\"callId\":\"call-1\"", StringComparison.Ordinal));
        // Raw tool arguments / results must never leak to control frames or TTS.
        Assert.DoesNotContain(socket.SentText, t => t.Contains("DO_NOT_SPEAK_ARGS", StringComparison.Ordinal) || t.Contains("INTERNAL_DB_ROW_JSON", StringComparison.Ordinal));
        Assert.All(tts.ReceivedTexts, text =>
        {
            Assert.DoesNotContain("DO_NOT_SPEAK_ARGS", text, StringComparison.Ordinal);
            Assert.DoesNotContain("INTERNAL_DB_ROW_JSON", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task InterruptAfterCommittedToolPreservesToolOutcomeWithoutReversal_LV22()
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
        var stt = new MultiTurnFakeSttClient(["create high priority task"]);
        var tts = new FakeStreamingSpeechSynthesizer();
        var committedToolCount = 0;
        var reversedToolCount = 0;
        var dispatcher = new CustomActionDispatcher((req, record) =>
        {
            var sessionUlid = Ulid.Parse(req.SessionId!);
            _ = Task.Run(async () =>
            {
                Interlocked.Increment(ref committedToolCount);
                await sessions.AppendTaskEventAsync(sessionUlid, record.Id, "tool.completed", JsonSerializer.Serialize(new { toolName = "todo.create", callId = "call-commit-1", status = "committed" }), CancellationToken.None).ConfigureAwait(false);
                await sessions.AppendTaskEventAsync(sessionUlid, record.Id, "message.delta", "I created your high priority task right away. ", CancellationToken.None).ConfigureAwait(false);
                // Keep task open simulating long narration after tool commit.
                await Task.Delay(5000, CancellationToken.None).ConfigureAwait(false);
            }, CancellationToken.None);
        });
        var services = BuildDuplexServices(sessions, options, coordinator, stt, tts, dispatcher);

        var socket = new InteractiveDuplexSocket();
        var httpContext = CreateAuthenticatedContext(services, tenant, principal, new FakeWebSocketFeature(["openconnect.voice.v2"], socket));
        var handlerTask = VoiceDuplexSessionHandler.HandleSocketAsync(socket, httpContext, session.Id.Value.ToString(), tenant.Value, principal.Value, CancellationToken.None);

        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "session.open", sessionId = session.Id.Value.ToString() }));
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"session.ready\"", StringComparison.Ordinal));

        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "speech.started", captureId = "cap-lv22" }));
        socket.EnqueueBinary(EncodeMicFrame(1, 0, 0, 960));
        socket.EnqueueText("""{"version":2,"type":"speech.ended"}""");

        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"tool.completed\"", StringComparison.Ordinal));
        await socket.WaitForBinaryCountAsync(1);

        // User interrupts narration after tool already committed.
        socket.EnqueueText(JsonSerializer.Serialize(new
        {
            version = 2,
            type = "response.interrupt",
            requestId = "int-lv22",
            speechEpoch = 1,
            playedSamples = 480,
            reason = "user_barge_in"
        }));
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"response.cancelled\"", StringComparison.Ordinal));

        socket.EnqueueText("""{"version":2,"type":"session.close"}""");
        await handlerTask;

        Assert.Equal(1, committedToolCount);
        Assert.Equal(0, reversedToolCount);
        var persistedEvents = sessions.GetEvents(session.Id.Value, 0);
        Assert.Contains(persistedEvents, e => e.Type == "tool.completed" && e.Data.Contains("call-commit-1", StringComparison.Ordinal));
        Assert.DoesNotContain(persistedEvents, e => e.Type.Contains("rollback", StringComparison.OrdinalIgnoreCase) || e.Type.Contains("reversed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InterruptPersistsDurableDeliveryReconciliationEventForFutureTurns_LV23()
    {
        var (tenant, principal, sessions, session) = CreateSession();
        var options = new OpenConnectVoiceOptions { EnableContinuousConversation = true };
        var coordinator = new DuplexVoiceSessionCoordinator(Options.Create(options));
        var services = BuildDuplexServices(sessions, options, coordinator, null, null, new FakeDispatcher(sessions));

        var connId = Ulid.NewUlid().ToString();
        coordinator.OpenSession(tenant, principal, session.Id, connId);
        coordinator.StartResponse(tenant, principal, session.Id, "resp-lv23", speechEpoch: 1);
        coordinator.RegisterAudioSegment(tenant, principal, session.Id, "resp-lv23", 1, new VoiceSegmentMetadata("seg-0", 0, "First sentence.", "First sentence.", 0, 24000));
        coordinator.RegisterAudioSegment(tenant, principal, session.Id, "resp-lv23", 1, new VoiceSegmentMetadata("seg-1", 1, "Second sentence here.", "Second sentence here.", 24000, 24000));
        coordinator.RegisterAudioSegment(tenant, principal, session.Id, "resp-lv23", 1, new VoiceSegmentMetadata("seg-2", 2, "Third sentence.", "Third sentence.", 48000, 24000));

        var socket = new ScriptedDuplexSocket(
        [
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = 2, type = "session.open", sessionId = session.Id.Value.ToString(), voiceConnectionId = connId }))),
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                version = 2,
                type = "response.interrupt",
                requestId = "int-lv23",
                responseId = "resp-lv23",
                speechEpoch = 1,
                playedSamples = 36000,
                reason = "user_barge_in"
            }))),
            (WebSocketMessageType.Text, Encoding.UTF8.GetBytes("""{"version":2,"type":"session.close"}"""))
        ]);

        var httpContext = CreateAuthenticatedContext(services, tenant, principal, new FakeWebSocketFeature(["openconnect.voice.v2"], socket));
        await VoiceDuplexSessionHandler.HandleSocketAsync(socket, httpContext, session.Id.Value.ToString(), tenant.Value, principal.Value, CancellationToken.None);

        var persisted = Assert.Single(sessions.GetEvents(session.Id.Value, 0), e => e.Type == "voice.delivery.reconciled");
        using var doc = JsonDocument.Parse(persisted.Data);
        Assert.Equal("resp-lv23", doc.RootElement.GetProperty("responseId").GetString());
        Assert.Equal("First sentence.", doc.RootElement.GetProperty("confirmedText").GetString());
        Assert.Equal("Second sentence here.", doc.RootElement.GetProperty("uncertainText").GetString());
        Assert.Equal("seg-0", doc.RootElement.GetProperty("confirmedSegments")[0].GetString());
        Assert.Equal("seg-1", doc.RootElement.GetProperty("uncertainSegment").GetString());
        Assert.Equal("seg-2", doc.RootElement.GetProperty("unheardSegments")[0].GetString());
    }

    [Fact]
    public async Task TurnDurableStreamingNeverEmitsHiddenReasoningOrToolPayloadsAsSpeakableText_LV24()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal);
        var taskId = new TaskId(Ulid.NewUlid());
        var task = new HostTaskExecution(taskId, tenant, principal, session.Id, "Summarize status", "voice:cap-lv24:1");

        var updates = new[]
        {
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("HIDDEN_CHAIN_OF_THOUGHT")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-1", "internal_lookup", new Dictionary<string, object?> { ["secret"] = "TOP_SECRET_ARG" })]),
            new ChatResponseUpdate(ChatRole.Tool, [new FunctionResultContent("call-1", "RAW_TOOL_RESULT_JSON"), new TextContent("RAW_TOOL_TEXT_NEVER_SPEAK")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Your project is "), new TextReasoningContent("MORE_HIDDEN_REASONING")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("on track.")])
        };

        var factory = new StreamingExecutionFactory(new MixedContentStreamingChatClient(updates));
        var executor = new MafHostTaskExecutor(factory, sessions);

        await executor.ExecuteAsync(task, CancellationToken.None);

        var deltas = sessions.GetEvents(session.Id.Value, 0)
            .Where(e => e.Type == "message.delta")
            .Select(e => e.Data)
            .ToArray();

        Assert.Equal(["Your project is ", "on track."], deltas);
        var joined = string.Concat(deltas);
        Assert.DoesNotContain("HIDDEN_CHAIN_OF_THOUGHT", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("MORE_HIDDEN_REASONING", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP_SECRET_ARG", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW_TOOL_RESULT_JSON", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW_TOOL_TEXT_NEVER_SPEAK", joined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowNetworkBoundedOutputQueueEnforcesOverloadWithoutUnboundedBuffering_LV26()
    {
        var (tenant, principal, sessions, session) = CreateSession();
        var options = new OpenConnectVoiceOptions
        {
            EnableContinuousConversation = true,
            EnableStreamingSpeechOutput = true,
            MaxBufferedOutputFrames = 16,
            MinPhraseWords = 2,
            MaxPhraseWords = 10
        };
        var coordinator = new DuplexVoiceSessionCoordinator(Options.Create(options));
        var stt = new MultiTurnFakeSttClient(["stream lots of audio"]);
        var tts = new BurstStreamingSpeechSynthesizer(chunkCount: 40);
        var dispatcher = new FakeDispatcher(sessions, autoRespond: true);
        var services = BuildDuplexServices(sessions, options, coordinator, stt, tts, dispatcher);

        var unblockSlowSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new InteractiveDuplexSocket(blockFirstBinarySendOn: unblockSlowSend);
        var httpContext = CreateAuthenticatedContext(services, tenant, principal, new FakeWebSocketFeature(["openconnect.voice.v2"], socket));
        var handlerTask = VoiceDuplexSessionHandler.HandleSocketAsync(socket, httpContext, session.Id.Value.ToString(), tenant.Value, principal.Value, CancellationToken.None);

        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "session.open", sessionId = session.Id.Value.ToString() }));
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"session.ready\"", StringComparison.Ordinal));

        socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "speech.started", captureId = "cap-lv26" }));
        socket.EnqueueBinary(EncodeMicFrame(1, 0, 0, 960));
        socket.EnqueueText("""{"version":2,"type":"speech.ended"}""");

        // Wait until bounded queue overflows and handler rejects cleanly, then unblock writer so error frame flushes.
        await tts.OverloadObservedOrFinished.WaitAsync(TimeSpan.FromSeconds(5));
        unblockSlowSend.TrySetResult();
        await socket.WaitForTextAsync(t => t.Contains("\"type\":\"response.error\"", StringComparison.Ordinal) && t.Contains(ErrorCodes.RateLimited, StringComparison.Ordinal));

        socket.EnqueueText("""{"version":2,"type":"session.close"}""");
        await handlerTask;

        Assert.True(socket.SentBinary.Count < 40, $"Expected bounded output queue to stop before 40 frames, got {socket.SentBinary.Count}.");
        Assert.Contains(socket.SentText, t => t.Contains("\"type\":\"response.error\"", StringComparison.Ordinal) && t.Contains(ErrorCodes.RateLimited, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepeatedSessionsAndConcurrentTenantsHaveZeroStateLeakage_LV29()
    {
        var options = new OpenConnectVoiceOptions
        {
            EnableContinuousConversation = true,
            EnableStreamingSpeechOutput = true,
            MinPhraseWords = 2,
            MaxPhraseWords = 10
        };
        var sharedCoordinator = new DuplexVoiceSessionCoordinator(Options.Create(options));
        var sharedSessions = new InMemorySessionStore();

        const int tenantCount = 4;
        const int roundsPerTenant = 3;

        var tenantTasks = Enumerable.Range(0, tenantCount).Select(async tenantIdx =>
        {
            var tenant = new TenantId(Ulid.NewUlid());
            var principal = new PrincipalId(Ulid.NewUlid());
            var session = sharedSessions.Create(tenant, principal);
            var tenantTag = $"tenant-{tenantIdx}-secret";

            for (var round = 0; round < roundsPerTenant; round++)
            {
                var turnText = $"{tenantTag} round {round}";
                var stt = new MultiTurnFakeSttClient([turnText]);
                var tts = new FakeStreamingSpeechSynthesizer();
                var dispatcher = new FakeDispatcher(sharedSessions, autoRespond: true);
                var services = BuildDuplexServices(sharedSessions, options, sharedCoordinator, stt, tts, dispatcher);

                var connId = $"vc-t{tenantIdx}-r{round}-{Ulid.NewUlid()}";
                var socket = new InteractiveDuplexSocket();
                var httpContext = CreateAuthenticatedContext(services, tenant, principal, new FakeWebSocketFeature(["openconnect.voice.v2"], socket));
                var handlerTask = VoiceDuplexSessionHandler.HandleSocketAsync(socket, httpContext, session.Id.Value.ToString(), tenant.Value, principal.Value, CancellationToken.None);

                socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "session.open", sessionId = session.Id.Value.ToString(), voiceConnectionId = connId }));
                await socket.WaitForTextAsync(t => t.Contains("\"type\":\"session.ready\"", StringComparison.Ordinal));

                var epoch = sharedCoordinator.GetSnapshot(tenant, principal, session.Id)!.SpeechEpoch;
                socket.EnqueueText(JsonSerializer.Serialize(new { version = 2, type = "speech.started", captureId = $"cap-t{tenantIdx}-r{round}" }));
                socket.EnqueueBinary(EncodeMicFrame(epoch, round, round * 480, 960));
                socket.EnqueueText("""{"version":2,"type":"speech.ended"}""");

                await socket.WaitForTextAsync(t => t.Contains("\"type\":\"response.completed\"", StringComparison.Ordinal));
                socket.EnqueueText("""{"version":2,"type":"session.close"}""");
                await handlerTask;

                // Verify this socket saw ONLY its own tenant's content and zero leakage from other tenants.
                Assert.Contains(socket.SentText, t => t.Contains(turnText, StringComparison.Ordinal));
                for (var other = 0; other < tenantCount; other++)
                {
                    if (other == tenantIdx)
                        continue;
                    var otherTag = $"tenant-{other}-secret";
                    Assert.DoesNotContain(socket.SentText, t => t.Contains(otherTag, StringComparison.Ordinal));
                }
            }

            // Verify session event store has zero cross-tenant leakage.
            var sessionEvents = sharedSessions.GetEvents(session.Id.Value, 0);
            for (var other = 0; other < tenantCount; other++)
            {
                if (other == tenantIdx)
                    continue;
                var otherTag = $"tenant-{other}-secret";
                Assert.DoesNotContain(sessionEvents, e => e.Data.Contains(otherTag, StringComparison.Ordinal));
            }
        });

        await Task.WhenAll(tenantTasks);
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
        IHostTaskExecutionDispatcher dispatcher,
        Action<ServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(sessions);
        services.AddSingleton<IOptions<OpenConnectVoiceOptions>>(Options.Create(options));
        services.AddSingleton(coordinator);
        services.AddSingleton(dispatcher);
        services.AddSingleton<IHostTaskExecutor, NoOpExecutor>();
        services.AddSingleton<IHostTaskExecutionAuthorizer, NoOpAuthorizer>();
        if (stt is not null)
            services.AddSingleton(stt);
        if (tts is not null)
            services.AddSingleton(tts);
        configureServices?.Invoke(services);
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
            foreach (var item in list)
                _incoming.Enqueue(item);
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
            while (await pcm24kMono16Bit.ReadAsync(buf, cancellationToken).ConfigureAwait(false) > 0)
            { }

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
            while (await pcm24kMono16Bit.ReadAsync(buf, cancellationToken).ConfigureAwait(false) > 0)
            { }

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

    private sealed class InteractiveDuplexSocket : WebSocket
    {
        private readonly System.Threading.Channels.Channel<(WebSocketMessageType Type, byte[] Payload)> _incoming =
            System.Threading.Channels.Channel.CreateUnbounded<(WebSocketMessageType, byte[])>();
        private readonly TaskCompletionSource? _blockFirstBinarySendOn;
        private int _binarySendBlockedOnce;
        private WebSocketState _state = WebSocketState.Open;

        public InteractiveDuplexSocket(TaskCompletionSource? blockFirstBinarySendOn = null)
        {
            _blockFirstBinarySendOn = blockFirstBinarySendOn;
        }

        public ConcurrentQueue<string> SentText { get; } = new();
        public ConcurrentQueue<byte[]> SentBinary { get; } = new();
        public WebSocketCloseStatus? ClosedStatus { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => ClosedStatus;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => "openconnect.voice.v2";

        public void EnqueueText(string json) => _incoming.Writer.TryWrite((WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json)));
        public void EnqueueBinary(byte[] frame) => _incoming.Writer.TryWrite((WebSocketMessageType.Binary, frame));

        public async Task WaitForTextAsync(Func<string, bool> predicate, int timeoutMs = 5000)
        {
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (SentText.Any(predicate))
                {
                    return;
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            throw new TimeoutException($"Timed out waiting for expected WebSocket text frame. Received: {string.Join(" | ", SentText)}");
        }

        public async Task WaitForBinaryCountAsync(int minCount, int timeoutMs = 5000)
        {
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (SentBinary.Count >= minCount)
                {
                    return;
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            throw new TimeoutException($"Timed out waiting for {minCount} binary frames (saw {SentBinary.Count}).");
        }

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            _incoming.Writer.TryComplete();
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            ClosedStatus = closeStatus;
            _state = WebSocketState.Closed;
            _incoming.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _incoming.Writer.TryComplete();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (await _incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                && _incoming.Reader.TryRead(out var item))
            {
                Buffer.BlockCopy(item.Payload, 0, buffer.Array!, buffer.Offset, item.Payload.Length);
                return new WebSocketReceiveResult(item.Payload.Length, item.Type, true);
            }

            _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "eof");
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (messageType == WebSocketMessageType.Text)
            {
                SentText.Enqueue(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
                return;
            }

            if (messageType == WebSocketMessageType.Binary)
            {
                if (_blockFirstBinarySendOn is not null && Interlocked.Exchange(ref _binarySendBlockedOnce, 1) == 0)
                {
                    await _blockFirstBinarySendOn.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                var copy = new byte[buffer.Count];
                Buffer.BlockCopy(buffer.Array!, buffer.Offset, copy, 0, buffer.Count);
                SentBinary.Enqueue(copy);
            }
        }
    }

    private sealed class CustomActionDispatcher(Action<TaskRequest, TaskRecord> onAccepted) : IHostTaskExecutionDispatcher
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
            onAccepted(request, record);
            return Task.FromResult(new AdmissionResult(ExecutionAdmissionStatus.Accepted, record));
        }

        public Task<TaskRecord?> CancelAsync(Ulid taskId, CancellationToken cancellationToken)
        {
            CancelledTasks.Add(taskId);
            return Task.FromResult<TaskRecord?>(null);
        }
    }

    private sealed class GatedStreamingSpeechSynthesizer(
        TaskCompletionSource entered,
        TaskCompletionSource release) : IStreamingSpeechSynthesizer
    {
        public bool IsConfigured => true;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizePcmStreamAsync(
            string text,
            string? voice = null,
            string? locale = null,
            float speed = 1.0f,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new byte[960];
        }
    }

    private sealed class HesitationStreamingFakeSttClient(string hesitationPartial, TaskCompletionSource partialSent) : IRealtimeSpeechToTextClient
    {
        public bool IsConfigured => true;

        public async Task<RealtimeTranscriptionResult> TranscribePcmAsync(
            Stream pcm24kMono16Bit,
            CancellationToken cancellationToken = default,
            Func<RealtimePartialTranscript, CancellationToken, ValueTask>? onPartialTranscript = null,
            Func<RealtimeTranscriptTurn, CancellationToken, ValueTask>? onFinalTranscript = null)
        {
            var buf = new byte[1600];
            var sent = false;
            while (await pcm24kMono16Bit.ReadAsync(buf, cancellationToken).ConfigureAwait(false) > 0)
            {
                if (!sent)
                {
                    sent = true;
                    if (onPartialTranscript is not null)
                    {
                        await onPartialTranscript(new RealtimePartialTranscript(1, hesitationPartial), cancellationToken).ConfigureAwait(false);
                    }

                    partialSent.TrySetResult();
                }
            }

            if (onFinalTranscript is not null)
            {
                await onFinalTranscript(new RealtimeTranscriptTurn(1, hesitationPartial), cancellationToken).ConfigureAwait(false);
            }

            return new RealtimeTranscriptionResult("sdk-hesitation", [new RealtimeTranscriptTurn(1, hesitationPartial)]);
        }
    }

    private sealed class ScriptedVadDetector(int speechFrameCount) : IVoiceActivityDetector
    {
        public IVoiceActivityDetectionSession CreateSession() => new Session(speechFrameCount);

        private sealed class Session(int speechFrames) : IVoiceActivityDetectionSession
        {
            private int _frame;
            public ValueTask<VoiceActivityResult> AnalyzeAsync(VoiceActivityFrame frame, CancellationToken cancellationToken = default)
            {
                var isSpeech = Interlocked.Increment(ref _frame) <= speechFrames;
                return ValueTask.FromResult(new VoiceActivityResult(
                    frame.Sequence,
                    isSpeech ? 0.95f : 0.02f,
                    isSpeech,
                    false,
                    0,
                    frame.Samples.Length));
            }

            public void Reset()
            {
                Interlocked.Exchange(ref _frame, 0);
            }

            public void Dispose() { }
        }
    }

    private sealed class FaultingThenRecoveringSynthesizer(Exception firstCallException) : IStreamingSpeechSynthesizer
    {
        private int _calls;
        public bool IsConfigured => true;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizePcmStreamAsync(
            string text,
            string? voice = null,
            string? locale = null,
            float speed = 1.0f,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                await Task.Yield();
                throw firstCallException;
            }

            await Task.Yield();
            yield return new byte[960];
        }
    }

    private sealed class RecordingStreamingSpeechSynthesizer : IStreamingSpeechSynthesizer
    {
        public ConcurrentQueue<string> ReceivedTexts { get; } = new();
        public bool IsConfigured => true;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizePcmStreamAsync(
            string text,
            string? voice = null,
            string? locale = null,
            float speed = 1.0f,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedTexts.Enqueue(text);
            await Task.Yield();
            yield return new byte[960];
        }
    }

    private sealed class RegexPiiEgressGate(bool blockOnMatch) : IEgressGate
    {
        private static readonly Regex SsnRegex = new(@"\b\d{3}[-\s]\d{2}[-\s]\d{4}\b|\b\d{3}\s\d{2}\.\s*\d{4}\b", RegexOptions.Compiled);

        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            var match = SsnRegex.Match(content);
            if (!match.Success)
            {
                return ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "clean"));
            }

            var spans = new[] { new EntitySpan(match.Index, match.Length, "SSN") };
            if (blockOnMatch)
            {
                return ValueTask.FromResult(new EgressDecision(false, null, spans, SecurityPolicyAction.Block, "pii_detected"));
            }

            var redacted = SsnRegex.Replace(content, "[REDACTED]");
            return ValueTask.FromResult(new EgressDecision(true, redacted, spans, SecurityPolicyAction.Redact, "pii_redacted"));
        }
    }

    private sealed class TrackingCloudSttClient : IRealtimeSpeechToTextClient
    {
        private int _invocations;
        public int InvocationCount => Volatile.Read(ref _invocations);
        public bool IsConfigured => true;

        public Task<RealtimeTranscriptionResult> TranscribePcmAsync(
            Stream pcm24kMono16Bit,
            CancellationToken cancellationToken = default,
            Func<RealtimePartialTranscript, CancellationToken, ValueTask>? onPartialTranscript = null,
            Func<RealtimeTranscriptTurn, CancellationToken, ValueTask>? onFinalTranscript = null)
        {
            Interlocked.Increment(ref _invocations);
            return Task.FromResult(new RealtimeTranscriptionResult("cloud", []));
        }
    }

    private sealed class MixedContentStreamingChatClient(ChatResponseUpdate[] updates) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("mixed-content-test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Your project is on track.")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class BurstStreamingSpeechSynthesizer(int chunkCount) : IStreamingSpeechSynthesizer
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task OverloadObservedOrFinished => _done.Task;
        public bool IsConfigured => true;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizePcmStreamAsync(
            string text,
            string? voice = null,
            string? locale = null,
            float speed = 1.0f,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                for (var i = 0; i < chunkCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                    yield return new byte[960];
                }
            }
            finally
            {
                _done.TrySetResult();
            }
        }
    }
}
