using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Serialization;
using OpenConnect.Abstractions.Speech;
using OpenConnect.Core;
using OpenConnect.Core.Configuration;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Privacy;

namespace OpenConnect.Hosting.AspNetCore.Voice;

/// <summary>Gateway-authoritative full-duplex <c>openconnect.voice.v2</c> WebSocket session handler.</summary>
internal static class VoiceDuplexSessionHandler
{
    public const string SubProtocol = "openconnect.voice.v2";

    private const int MaxControlMessageBytes = 256 * 1024;
    private const int SampleRateHz = 24_000;
    private const int BytesPerSample = 2;
    private static readonly JsonSerializerOptions JsonOptions = OpenConnectHttpJson.CreateOptions();

    public static async Task HandleSocketAsync(
        WebSocket browser,
        HttpContext context,
        string sessionId,
        Ulid tenant,
        Ulid principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(context);

        var voiceOptions = context.RequestServices.GetService<IOptions<OpenConnectVoiceOptions>>()?.Value
            ?? new OpenConnectVoiceOptions();
        var coordinator = context.RequestServices.GetService<DuplexVoiceSessionCoordinator>()
            ?? new DuplexVoiceSessionCoordinator(Options.Create(voiceOptions));
        var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("OpenConnect.VoiceDuplex")
            ?? NullLogger.Instance;

        var runner = new DuplexSessionRunner(
            browser,
            context,
            sessionId,
            new TenantId(tenant),
            new PrincipalId(principal),
            new SessionId(Ulid.Parse(sessionId)),
            voiceOptions,
            coordinator,
            logger);

        await runner.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static bool TryCreateTranscriptionV2Uri(string? value, bool localOnly, out Uri? result)
    {
        result = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            return false;
        }

        result = new UriBuilder(endpoint)
        {
            Scheme = endpoint.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = localOnly ? "/internal/v2/realtime/local" : "/internal/v2/realtime"
        }.Uri;
        return true;
    }

    private sealed class DuplexSessionRunner
    {
        private readonly WebSocket _browser;
        private readonly HttpContext _context;
        private readonly string _sessionIdText;
        private readonly TenantId _tenant;
        private readonly PrincipalId _principal;
        private readonly SessionId _sessionId;
        private readonly OpenConnectVoiceOptions _options;
        private readonly DuplexVoiceSessionCoordinator _coordinator;
        private readonly ILogger _logger;
        private readonly Channel<byte[]> _controlChannel;
        private readonly Channel<byte[]> _audioChannel;
        private readonly SemaphoreSlim _writeSignal = new(0);
        private readonly AdaptiveEndpointEvaluator _endpointEvaluator;
        private readonly Queue<(DateTimeOffset Timestamp, int Bytes)> _rollingAudioWindow = new();
        private readonly object _activeStateLock = new();

        private string _voiceConnectionId = Ulid.NewUlid().ToString();
        private string? _clientConnectionId;
        private string? _negotiatedVoice;
        private string? _negotiatedLocale;
        private bool _negotiatedStreamingSpeech;
        private long _outboundControlSeq;
        private long _outboundAudioSeq;
        private uint _ttsStreamId = 1;
        private long _rollingAudioBytes;
        private long _currentUtteranceBytes;
        private bool _sessionOpened;

        // Active capture / STT state
        private string? _activeCaptureId;
        private long _activeCaptureEpoch;
        private long _partialRevision;
        private string _lastPartialText = string.Empty;
        private long _speechStartTimestamp;
        private long _firstPartialRecordedForEpoch;
        private long _silenceStartTimestamp;
        private long _partialStableTimestamp;
        private bool _speechDetectedInCapture;

        // Per-utterance SDK STT pipe (when IRealtimeSpeechToTextClient is primary)
        private Pipe? _sdkUtterancePipe;
        private CancellationTokenSource? _sdkUtteranceCts;
        private Task? _sdkUtteranceTask;

        // Persistent worker v2 STT socket (when Workers:Transcription:Endpoint is used)
        private ClientWebSocket? _workerSttSocket;
        private Task? _workerSttReaderTask;
        private readonly SemaphoreSlim _workerSttSendGate = new(1, 1);

        // Active response / TTS state
        private CancellationTokenSource? _activeResponseCts;
        private Task? _activeResponseTask;
        private Ulid? _activeTaskId;

        public DuplexSessionRunner(
            WebSocket browser,
            HttpContext context,
            string sessionIdText,
            TenantId tenant,
            PrincipalId principal,
            SessionId sessionId,
            OpenConnectVoiceOptions options,
            DuplexVoiceSessionCoordinator coordinator,
            ILogger logger)
        {
            _browser = browser;
            _context = context;
            _sessionIdText = sessionIdText;
            _tenant = tenant;
            _principal = principal;
            _sessionId = sessionId;
            _options = options;
            _coordinator = coordinator;
            _logger = logger;
            _negotiatedStreamingSpeech = options.EnableStreamingSpeechOutput;
            _endpointEvaluator = new AdaptiveEndpointEvaluator(options.ConservativeEndpointing);

            var maxBuffered = Math.Max(16, options.MaxBufferedOutputFrames);
            _controlChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(Math.Max(64, maxBuffered))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            _audioChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(maxBuffered)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _context.RequestAborted);
            var ct = sessionCts.Token;

            var vadDetector = _context.RequestServices.GetService<IVoiceActivityDetector>();
            using var vadStream = vadDetector is not null ? new SileroVoiceActivityAudioStream(vadDetector) : null;
            var writerTask = Task.Run(() => RunWriterLoopAsync(ct), CancellationToken.None);

            var receiveBuffer = new byte[VoiceBinaryFrameCodec.MaxFrameBytes];
            try
            {
                while (_browser.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    ValueWebSocketReceiveResult result;
                    var exceededLimit = false;
                    do
                    {
                        result = await _browser.ReceiveAsync(receiveBuffer.AsMemory(), ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        var maxAllowed = result.MessageType == WebSocketMessageType.Binary
                            ? VoiceBinaryFrameCodec.MaxFrameBytes
                            : MaxControlMessageBytes;
                        if (message.Length + result.Count > maxAllowed)
                        {
                            exceededLimit = true;
                            break;
                        }

                        if (result.Count > 0)
                        {
                            message.Write(receiveBuffer, 0, result.Count);
                        }
                    }
                    while (!result.EndOfMessage);

                    if (exceededLimit)
                    {
                        await SendErrorAndCloseAsync(
                            ErrorCodes.InvalidRequest,
                            "Voice frame exceeds maximum permitted size.",
                            WebSocketCloseStatus.MessageTooBig,
                            ct).ConfigureAwait(false);
                        return;
                    }

                    var payloadLength = (int)message.Length;
                    var payloadBuffer = message.GetBuffer();

                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        var shouldContinue = await HandleBinaryFrameAsync(
                            payloadBuffer.AsMemory(0, payloadLength),
                            vadStream,
                            ct).ConfigureAwait(false);
                        if (!shouldContinue)
                        {
                            return;
                        }

                        continue;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var json = Encoding.UTF8.GetString(payloadBuffer, 0, payloadLength);
                        var shouldContinue = await HandleControlMessageAsync(json, vadStream, ct).ConfigureAwait(false);
                        if (!shouldContinue)
                        {
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Normal cancellation / disconnect.
            }
            catch (WebSocketException)
            {
                // Client disconnected abruptly.
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Duplex voice session ended with {ExceptionType}.", ex.GetType().Name);
            }
            finally
            {
                Array.Clear(receiveBuffer);
                await CancelActiveResponseAsync(cancelAdmittedTask: true).ConfigureAwait(false);
                await CloseSttResourcesAsync().ConfigureAwait(false);

                if (_sessionOpened)
                {
                    _coordinator.FenceConnection(_tenant, _principal, _sessionId, _voiceConnectionId, markReconnecting: false);
                }

                _controlChannel.Writer.TryComplete();
                _audioChannel.Writer.TryComplete();
                await sessionCts.CancelAsync().ConfigureAwait(false);
                _writeSignal.Release();

                try
                {
                    await writerTask.ConfigureAwait(false);
                }
                catch
                {
                    // Ignore writer shutdown races.
                }

                _writeSignal.Dispose();
                _workerSttSendGate.Dispose();
            }
        }

        private async Task<bool> HandleControlMessageAsync(string json, SileroVoiceActivityAudioStream? vadStream, CancellationToken ct)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                await SendErrorAndCloseAsync(ErrorCodes.InvalidRequest, "Malformed JSON control frame.", WebSocketCloseStatus.InvalidPayloadData, ct).ConfigureAwait(false);
                return false;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    await SendErrorAndCloseAsync(ErrorCodes.InvalidRequest, "Control frame must be a JSON object.", WebSocketCloseStatus.InvalidPayloadData, ct).ConfigureAwait(false);
                    return false;
                }

                var type = ReadString(root, "type");
                if (string.IsNullOrWhiteSpace(type))
                {
                    await SendErrorAndCloseAsync(ErrorCodes.InvalidRequest, "Control frame is missing type.", WebSocketCloseStatus.InvalidPayloadData, ct).ConfigureAwait(false);
                    return false;
                }

                switch (type)
                {
                    case "session.open":
                        return await HandleSessionOpenAsync(root, ct).ConfigureAwait(false);

                    case "session.ping":
                        await EnqueueControlAsync(new
                        {
                            version = VoiceBinaryFrameCodec.ProtocolVersion,
                            type = "session.pong",
                            voiceConnectionId = _voiceConnectionId,
                            sequence = ReadInt64(root, "sequence") ?? Interlocked.Increment(ref _outboundControlSeq),
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }, ct).ConfigureAwait(false);
                        return true;

                    case "session.close":
                        await DrainPendingControlMessagesAsync(ct).ConfigureAwait(false);
                        await VoiceRealtimeWebSocketHandler.CloseAsync(_browser, WebSocketCloseStatus.NormalClosure, "session_closed", ct).ConfigureAwait(false);
                        return false;
                }

                if (!_sessionOpened)
                {
                    await EnsureSessionOpenedImplicitlyAsync(ct).ConfigureAwait(false);
                }

                switch (type)
                {
                    case "speech.started":
                        await StartCaptureAsync(ReadString(root, "captureId"), vadStream, ct).ConfigureAwait(false);
                        return true;

                    case "speech.ended":
                        await EndActiveUtteranceAsync(ct).ConfigureAwait(false);
                        return true;

                    case "session.mute":
                    case "session.unmute":
                    {
                        var muted = type == "session.mute";
                        var snap = _coordinator.SetMuted(_tenant, _principal, _sessionId, muted);
                        await EnqueueControlAsync(new
                        {
                            version = VoiceBinaryFrameCodec.ProtocolVersion,
                            type = muted ? "session.muted" : "session.unmuted",
                            voiceConnectionId = _voiceConnectionId,
                            speechEpoch = snap?.SpeechEpoch ?? 1,
                            sequence = Interlocked.Increment(ref _outboundControlSeq)
                        }, ct).ConfigureAwait(false);
                        return true;
                    }

                    case "response.audio.played":
                    case "response.playback.completed":
                    {
                        var responseId = ReadString(root, "responseId") ?? string.Empty;
                        var speechEpoch = ReadInt64(root, "speechEpoch") ?? 0;
                        var playedSamples = ReadInt64(root, "playedSamples") ?? 0;
                        var completed = type == "response.playback.completed" || (ReadBool(root, "completed") ?? false);
                        var receiptResult = _coordinator.RecordPlaybackReceipt(
                            _tenant,
                            _principal,
                            _sessionId,
                            new VoicePlaybackReceipt(responseId, speechEpoch, playedSamples, completed));
                        if (!receiptResult.Accepted && receiptResult.RejectionReason == "stale_speech_epoch")
                        {
                            VoiceDuplexMetrics.RecordStaleEpochRejected("playback_receipt");
                        }

                        return true;
                    }

                    case "response.interrupt":
                        await HandleInterruptAsync(root, ct).ConfigureAwait(false);
                        return true;

                    case "response.audio.underrun":
                        VoiceDuplexMetrics.RecordUnderrun();
                        return true;

                    default:
                        await EnqueueControlAsync(new
                        {
                            version = VoiceBinaryFrameCodec.ProtocolVersion,
                            type = "session.error",
                            voiceConnectionId = _voiceConnectionId,
                            errorCode = ErrorCodes.InvalidRequest,
                            reason = $"Unsupported control event '{type}'."
                        }, ct).ConfigureAwait(false);
                        return true;
                }
            }
        }

        private async Task<bool> HandleSessionOpenAsync(JsonElement root, CancellationToken ct)
        {
            var requestedSessionId = ReadString(root, "sessionId");
            if (!string.IsNullOrWhiteSpace(requestedSessionId)
                && !string.Equals(requestedSessionId, _sessionIdText, StringComparison.Ordinal))
            {
                await SendErrorAndCloseAsync(ErrorCodes.Unauthorized, "Session identifier mismatch.", WebSocketCloseStatus.PolicyViolation, ct).ConfigureAwait(false);
                return false;
            }

            var requestedConnectionId = ReadString(root, "voiceConnectionId");
            if (!string.IsNullOrWhiteSpace(requestedConnectionId) && requestedConnectionId.Length <= 128)
            {
                _voiceConnectionId = requestedConnectionId;
            }

            var clientConnId = ReadString(root, "clientConnectionId");
            if (!string.IsNullOrWhiteSpace(clientConnId))
            {
                _clientConnectionId = clientConnId;
            }

            var inputRate = ReadInt64(root, "inputSampleRateHz") ?? SampleRateHz;
            var outputRate = ReadInt64(root, "outputSampleRateHz") ?? SampleRateHz;
            var channels = ReadInt64(root, "channels") ?? 1;
            if (inputRate != SampleRateHz || outputRate != SampleRateHz || channels != 1)
            {
                await SendErrorAndCloseAsync(ErrorCodes.InvalidRequest, "Only 24 kHz mono PCM16LE audio is supported.", WebSocketCloseStatus.InvalidPayloadData, ct).ConfigureAwait(false);
                return false;
            }

            _negotiatedVoice = ReadString(root, "voice");
            _negotiatedLocale = ReadString(root, "locale");
            var clientStreamingSpeech = ReadBool(root, "enableStreamingSpeechOutput");
            _negotiatedStreamingSpeech = _options.EnableStreamingSpeechOutput && (clientStreamingSpeech ?? true);

            var snapshot = _coordinator.OpenSession(_tenant, _principal, _sessionId, _voiceConnectionId);
            _sessionOpened = true;

            var sttProvider = await EnsureSttReadyAsync(ct).ConfigureAwait(false);
            var synthesizer = _context.RequestServices.GetService<IStreamingSpeechSynthesizer>();

            await EnqueueControlAsync(new
            {
                version = VoiceBinaryFrameCodec.ProtocolVersion,
                type = "session.ready",
                sessionId = _sessionIdText,
                voiceConnectionId = _voiceConnectionId,
                speechEpoch = snapshot.SpeechEpoch,
                sequence = Interlocked.Increment(ref _outboundControlSeq),
                mode = "duplex",
                codec = "pcm16le",
                inputSampleRateHz = SampleRateHz,
                outputSampleRateHz = SampleRateHz,
                channels = 1,
                maxFrameBytes = VoiceBinaryFrameCodec.MaxFrameBytes,
                maxUtteranceAudioBytes = _options.MaxUtteranceAudioBytes,
                maxRollingSessionAudioBytes = _options.MaxRollingSessionAudioBytes,
                rollingSessionWindowSeconds = _options.RollingSessionWindowSeconds,
                streamingSpeechEnabled = _negotiatedStreamingSpeech && _options.EnableSpeechOutput && synthesizer is { IsConfigured: true },
                sttProvider
            }, ct).ConfigureAwait(false);

            return true;
        }

        private async Task EnsureSessionOpenedImplicitlyAsync(CancellationToken ct)
        {
            if (_sessionOpened)
            {
                return;
            }

            _coordinator.OpenSession(_tenant, _principal, _sessionId, _voiceConnectionId);
            _sessionOpened = true;
            await EnsureSttReadyAsync(ct).ConfigureAwait(false);
        }

        private static bool TryDecodeBinaryFrame(ReadOnlyMemory<byte> frame, out VoiceAudioFrameHeader header, out byte[] pcmCopy)
        {
            if (!VoiceBinaryFrameCodec.TryDecode(frame.Span, out header, out var pcmSlice))
            {
                pcmCopy = Array.Empty<byte>();
                return false;
            }

            pcmCopy = pcmSlice.IsEmpty ? Array.Empty<byte>() : pcmSlice.ToArray();
            return true;
        }

        private async Task<bool> HandleBinaryFrameAsync(
            ReadOnlyMemory<byte> frame,
            SileroVoiceActivityAudioStream? vadStream,
            CancellationToken ct)
        {
            if (!TryDecodeBinaryFrame(frame, out var header, out var pcmCopy))
            {
                await SendErrorAndCloseAsync(
                    ErrorCodes.InvalidRequest,
                    "Malformed v2 binary audio frame header or payload.",
                    WebSocketCloseStatus.InvalidPayloadData,
                    ct).ConfigureAwait(false);
                return false;
            }

            if (header.Kind != VoiceAudioStreamKind.MicInput)
            {
                await SendErrorAndCloseAsync(
                    ErrorCodes.InvalidRequest,
                    "Only MicInput binary frames are accepted from clients.",
                    WebSocketCloseStatus.InvalidPayloadData,
                    ct).ConfigureAwait(false);
                return false;
            }

            if (pcmCopy.Length == 0)
            {
                return true;
            }

            if (!_sessionOpened)
            {
                await EnsureSessionOpenedImplicitlyAsync(ct).ConfigureAwait(false);
            }

            var snapshot = _coordinator.GetSnapshot(_tenant, _principal, _sessionId);
            if (snapshot is null)
            {
                await SendErrorAndCloseAsync(ErrorCodes.SessionNotFound, "Voice session not found.", WebSocketCloseStatus.PolicyViolation, ct).ConfigureAwait(false);
                return false;
            }

            if (snapshot.InputState == VoiceInputState.Muted)
            {
                return true;
            }

            if (header.SpeechEpoch > 0 && header.SpeechEpoch != snapshot.SpeechEpoch)
            {
                VoiceDuplexMetrics.RecordStaleEpochRejected("audio_frame");
                return true;
            }

            // Enforce rolling session audio budget.
            if (!TryConsumeRollingAudioBudget(pcmCopy.Length))
            {
                await SendErrorAndCloseAsync(
                    ErrorCodes.RateLimited,
                    "Rolling session audio budget exceeded.",
                    WebSocketCloseStatus.PolicyViolation,
                    ct).ConfigureAwait(false);
                return false;
            }

            // Ensure an active capture exists.
            if (_activeCaptureId is null || _activeCaptureEpoch != snapshot.SpeechEpoch)
            {
                await StartCaptureAsync(null, vadStream, ct).ConfigureAwait(false);
            }

            // Enforce per-utterance byte limit.
            _currentUtteranceBytes += pcmCopy.Length;
            if (_currentUtteranceBytes > _options.MaxUtteranceAudioBytes)
            {
                await SendErrorAndCloseAsync(
                    ErrorCodes.InvalidRequest,
                    "Per-utterance audio limit exceeded.",
                    WebSocketCloseStatus.MessageTooBig,
                    ct).ConfigureAwait(false);
                return false;
            }

            // Run server-side Silero VAD + adaptive endpointing if available.
            if (vadStream is not null)
            {
                await EvaluateVadAndEndpointingAsync(vadStream, pcmCopy, snapshot, ct).ConfigureAwait(false);
            }

            // Forward PCM16 to active STT backend (SDK Cloud Muse primary or Worker v2).
            await ForwardPcmToSttAsync(pcmCopy, ct).ConfigureAwait(false);
            return true;
        }

        private async Task StartCaptureAsync(string? requestedCaptureId, SileroVoiceActivityAudioStream? vadStream, CancellationToken ct)
        {
            var captureId = string.IsNullOrWhiteSpace(requestedCaptureId)
                ? Guid.NewGuid().ToString("D")
                : requestedCaptureId.Trim();

            var captureResult = _coordinator.BeginCapture(_tenant, _principal, _sessionId, captureId, _voiceConnectionId);
            if (!captureResult.Accepted)
            {
                return;
            }

            _activeCaptureId = captureResult.CaptureId;
            _activeCaptureEpoch = captureResult.SpeechEpoch;
            _currentUtteranceBytes = 0;
            _partialRevision = 0;
            _lastPartialText = string.Empty;
            _speechStartTimestamp = Stopwatch.GetTimestamp();
            _firstPartialRecordedForEpoch = 0;
            _silenceStartTimestamp = 0;
            _partialStableTimestamp = 0;
            _speechDetectedInCapture = false;

            if (vadStream is not null)
            {
                await vadStream.ResetAsync(ct).ConfigureAwait(false);
            }

            await StartSttUtteranceAsync(captureResult.CaptureId!, captureResult.SpeechEpoch, ct).ConfigureAwait(false);
        }

        private async Task EndActiveUtteranceAsync(CancellationToken ct)
        {
            if (_activeCaptureId is null)
            {
                return;
            }

            if (_sdkUtterancePipe is not null)
            {
                await _sdkUtterancePipe.Writer.CompleteAsync().ConfigureAwait(false);
                var task = _sdkUtteranceTask;
                _sdkUtterancePipe = null;
                _sdkUtteranceTask = null;
                if (task is not null)
                {
                    try
                    {
                        await task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // Errors are already emitted to client.
                    }
                }

                _sdkUtteranceCts?.Dispose();
                _sdkUtteranceCts = null;
                return;
            }

            if (_workerSttSocket is { State: WebSocketState.Open })
            {
                await SendWorkerSttControlAsync(new
                {
                    type = "endUtterance",
                    captureId = _activeCaptureId,
                    speechEpoch = _activeCaptureEpoch
                }, ct).ConfigureAwait(false);
            }
        }

        private async Task HandleInterruptAsync(JsonElement root, CancellationToken ct)
        {
            var startTs = Stopwatch.GetTimestamp();
            var requestId = ReadString(root, "requestId");
            if (string.IsNullOrWhiteSpace(requestId))
            {
                requestId = Ulid.NewUlid().ToString();
            }

            var responseId = ReadString(root, "responseId");
            var snapshot = _coordinator.GetSnapshot(_tenant, _principal, _sessionId);
            var speechEpoch = ReadInt64(root, "speechEpoch") ?? snapshot?.SpeechEpoch ?? 1;
            var playedSamples = ReadInt64(root, "playedSamples") ?? 0;
            var reason = ReadString(root, "reason") ?? "user_barge_in";

            var result = _coordinator.HandleInterrupt(
                _tenant,
                _principal,
                _sessionId,
                new VoiceInterruptRequest(requestId, responseId, speechEpoch, playedSamples, reason));

            if (!result.Accepted)
            {
                if (result.RejectionReason == "stale_speech_epoch")
                {
                    VoiceDuplexMetrics.RecordStaleEpochRejected("interrupt");
                }

                await EnqueueControlAsync(new
                {
                    version = VoiceBinaryFrameCodec.ProtocolVersion,
                    type = "response.interrupt.rejected",
                    voiceConnectionId = _voiceConnectionId,
                    requestId,
                    responseId = result.CancelledResponseId,
                    speechEpoch = result.NewSpeechEpoch,
                    sequence = Interlocked.Increment(ref _outboundControlSeq),
                    reason = result.RejectionReason
                }, ct).ConfigureAwait(false);
                return;
            }

            if (!result.WasIdempotentReplay)
            {
                await CancelActiveResponseAsync(cancelAdmittedTask: true).ConfigureAwait(false);
                VoiceDuplexMetrics.RecordBargeInStop(Stopwatch.GetElapsedTime(startTs).TotalMilliseconds);
            }

            await EnqueueControlAsync(new
            {
                version = VoiceBinaryFrameCodec.ProtocolVersion,
                type = "response.cancelled",
                voiceConnectionId = _voiceConnectionId,
                requestId,
                responseId = result.CancelledResponseId,
                previousSpeechEpoch = result.PreviousSpeechEpoch,
                speechEpoch = result.NewSpeechEpoch,
                sequence = Interlocked.Increment(ref _outboundControlSeq),
                reason,
                wasIdempotentReplay = result.WasIdempotentReplay,
                reconciliation = result.Reconciliation is null
                    ? null
                    : new
                    {
                        responseId = result.Reconciliation.ResponseId,
                        speechEpoch = result.Reconciliation.SpeechEpoch,
                        transmittedSamples = result.Reconciliation.TransmittedSamples,
                        reportedPlayedSamples = result.Reconciliation.ReportedPlayedSamples,
                        receiptReceived = result.Reconciliation.ReceiptReceived,
                        confirmedSegmentCount = result.Reconciliation.ConfirmedPlayedSegments.Count,
                        hasUncertainSegment = result.Reconciliation.UncertainPartiallyPlayedSegment is not null,
                        unheardSegmentCount = result.Reconciliation.UnheardSegments.Count,
                        confirmedText = result.Reconciliation.ConfirmedText,
                        uncertainText = result.Reconciliation.UncertainText
                    }
            }, ct).ConfigureAwait(false);
        }

        private async Task EvaluateVadAndEndpointingAsync(
            SileroVoiceActivityAudioStream vadStream,
            byte[] pcmCopy,
            DuplexVoiceSessionSnapshot snapshot,
            CancellationToken ct)
        {
            IReadOnlyList<VoiceActivityResult> activities;
            try
            {
                activities = await vadStream.AppendPcm16Async(pcmCopy, SampleRateHz, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var anySpeech = false;
            foreach (var activity in activities)
            {
                if (activity.IsSpeech)
                {
                    anySpeech = true;
                    _speechDetectedInCapture = true;
                    _silenceStartTimestamp = 0;
                }
            }

            if (!anySpeech && _speechDetectedInCapture)
            {
                if (_silenceStartTimestamp == 0)
                {
                    _silenceStartTimestamp = now;
                }

                var silenceMs = (int)Stopwatch.GetElapsedTime(_silenceStartTimestamp, now).TotalMilliseconds;
                var stableMs = _partialStableTimestamp > 0
                    ? (int)Stopwatch.GetElapsedTime(_partialStableTimestamp, now).TotalMilliseconds
                    : 0;
                var isAssistantSpeaking = snapshot.OutputState is VoiceOutputState.Buffering or VoiceOutputState.Playing or VoiceOutputState.Draining;

                var eval = _endpointEvaluator.Evaluate(new AdaptiveEndpointInput(
                    SilenceDurationMs: silenceMs,
                    PartialTranscript: _lastPartialText,
                    TranscriptRevision: _partialRevision,
                    StableDurationMs: stableMs,
                    IsAssistantSpeaking: isAssistantSpeaking,
                    SpeechDetected: false));

                if (eval.Decision == AdaptiveEndpointDecision.BackchannelIgnore)
                {
                    VoiceDuplexMetrics.RecordEndpointDecision(silenceMs, "backchannel_ignore");
                    return;
                }

                if (eval.Decision == AdaptiveEndpointDecision.EndpointReady && !string.IsNullOrWhiteSpace(_lastPartialText))
                {
                    VoiceDuplexMetrics.RecordEndpointDecision(silenceMs, "endpoint_ready");
                    await EndActiveUtteranceAsync(ct).ConfigureAwait(false);
                }
            }
        }

        private async Task<string> EnsureSttReadyAsync(CancellationToken ct)
        {
            var configuration = _context.RequestServices.GetService<IConfiguration>();
            var localOnly = configuration?.GetValue<bool>("Workers:Transcription:RealtimeLocalOnly") ?? false;
            var sdkStt = _context.RequestServices.GetService<IRealtimeSpeechToTextClient>();

            if (!localOnly && sdkStt is { IsConfigured: true })
            {
                return "cloud-muse-sdk";
            }

            var workerToken = configuration?["OPENCONNECT_WORKER_TOKEN"];
            var workerEndpoint = configuration?["Workers:Transcription:Endpoint"];
            if (!string.IsNullOrWhiteSpace(workerToken)
                && TryCreateTranscriptionV2Uri(workerEndpoint, localOnly, out var workerUri))
            {
                if (_workerSttSocket is null)
                {
                    var socket = new ClientWebSocket();
                    socket.Options.SetRequestHeader("Authorization", $"Bearer {workerToken}");
                    try
                    {
                        await socket.ConnectAsync(workerUri!, ct).ConfigureAwait(false);
                        _workerSttSocket = socket;
                        _workerSttReaderTask = Task.Run(() => ReadWorkerSttEventsAsync(socket, ct), CancellationToken.None);
                        return localOnly ? "local-whisper" : "cloud-muse";
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        socket.Dispose();
                        _logger.LogWarning("Duplex transcription worker connect failed with {ExceptionType}.", ex.GetType().Name);
                    }
                }
            }

            return "unconfigured";
        }

        private async Task StartSttUtteranceAsync(string captureId, long speechEpoch, CancellationToken ct)
        {
            var configuration = _context.RequestServices.GetService<IConfiguration>();
            var localOnly = configuration?.GetValue<bool>("Workers:Transcription:RealtimeLocalOnly") ?? false;
            var sdkStt = _context.RequestServices.GetService<IRealtimeSpeechToTextClient>();

            if (!localOnly && sdkStt is { IsConfigured: true })
            {
                await CancelSdkUtteranceAsync().ConfigureAwait(false);

                var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 256 * 1024, resumeWriterThreshold: 128 * 1024));
                var utteranceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _sdkUtterancePipe = pipe;
                _sdkUtteranceCts = utteranceCts;
                _sdkUtteranceTask = Task.Run(
                    () => RunSdkUtteranceAsync(sdkStt, pipe.Reader.AsStream(), captureId, speechEpoch, utteranceCts.Token),
                    CancellationToken.None);
                return;
            }

            if (_workerSttSocket is { State: WebSocketState.Open })
            {
                await SendWorkerSttControlAsync(new
                {
                    type = "startUtterance",
                    captureId,
                    speechEpoch
                }, ct).ConfigureAwait(false);
            }
        }

        private async Task ForwardPcmToSttAsync(byte[] pcmCopy, CancellationToken ct)
        {
            if (_sdkUtterancePipe is not null)
            {
                var flush = await _sdkUtterancePipe.Writer.WriteAsync(pcmCopy, ct).ConfigureAwait(false);
                if (flush.IsCompleted || flush.IsCanceled)
                {
                    _sdkUtterancePipe = null;
                }

                return;
            }

            if (_workerSttSocket is { State: WebSocketState.Open })
            {
                await _workerSttSendGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (_workerSttSocket.State == WebSocketState.Open)
                    {
                        await _workerSttSocket.SendAsync(pcmCopy, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _workerSttSendGate.Release();
                }
            }
        }

        private async Task RunSdkUtteranceAsync(
            IRealtimeSpeechToTextClient sttClient,
            Stream pcmStream,
            string captureId,
            long speechEpoch,
            CancellationToken ct)
        {
            string? accumulatedFinal = null;
            var admitted = false;

            await using (pcmStream.ConfigureAwait(false))
            {
                try
                {
                    var result = await sttClient.TranscribePcmAsync(
                        pcmStream,
                        ct,
                        onPartialTranscript: async (partial, token) =>
                        {
                            if (string.IsNullOrWhiteSpace(partial.Text))
                            {
                                return;
                            }

                            var revision = Interlocked.Increment(ref _partialRevision);
                            await HandlePartialTranscriptAsync(captureId, speechEpoch, revision, partial.Text.Trim(), "cloud-muse-sdk", token).ConfigureAwait(false);
                        },
                        onFinalTranscript: async (turn, token) =>
                        {
                            if (string.IsNullOrWhiteSpace(turn.Text))
                            {
                                return;
                            }

                            accumulatedFinal = MergeTranscriptSegment(accumulatedFinal, turn.Text.Trim());
                            if (!admitted)
                            {
                                admitted = true;
                                await HandleFinalTranscriptAsync(captureId, speechEpoch, accumulatedFinal, token).ConfigureAwait(false);
                            }
                        }).ConfigureAwait(false);

                    if (!admitted && !ct.IsCancellationRequested)
                    {
                        var finalText = string.Join(" ", result.FinalTranscripts.Where(static t => !string.IsNullOrWhiteSpace(t)).Select(static t => t.Trim()));
                        if (!string.IsNullOrWhiteSpace(finalText))
                        {
                            await HandleFinalTranscriptAsync(captureId, speechEpoch, finalText, ct).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Expected on interrupt or disconnect.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Duplex SDK STT utterance failed with {ExceptionType}.", ex.GetType().Name);
                    await EnqueueControlAsync(new
                    {
                        version = VoiceBinaryFrameCodec.ProtocolVersion,
                        type = "session.error",
                        voiceConnectionId = _voiceConnectionId,
                        captureId,
                        speechEpoch,
                        errorCode = ErrorCodes.CapabilityUnavailable,
                        reason = "Voice transcription provider unavailable."
                    }, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        private async Task ReadWorkerSttEventsAsync(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[VoiceBinaryFrameCodec.MaxFrameBytes];
            try
            {
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        if (result.Count > 0)
                        {
                            message.Write(buffer, 0, result.Count);
                        }
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType != WebSocketMessageType.Text || message.Length == 0)
                    {
                        continue;
                    }

                    var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var type = ReadString(root, "type");
                    var captureId = ReadString(root, "captureId") ?? _activeCaptureId ?? string.Empty;
                    var speechEpoch = ReadInt64(root, "speechEpoch") ?? _activeCaptureEpoch;
                    var text = ReadString(root, "text") ?? ReadString(root, "transcript") ?? string.Empty;

                    if (string.Equals(type, "transcript.partial", StringComparison.Ordinal))
                    {
                        var revision = ReadInt64(root, "revision") ?? Interlocked.Increment(ref _partialRevision);
                        await HandlePartialTranscriptAsync(captureId, speechEpoch, revision, text, "worker-v2", ct).ConfigureAwait(false);
                    }
                    else if (string.Equals(type, "transcript.final", StringComparison.Ordinal))
                    {
                        var isEmpty = ReadBool(root, "isEmpty") ?? string.IsNullOrWhiteSpace(text);
                        if (!isEmpty && !string.IsNullOrWhiteSpace(text))
                        {
                            await HandleFinalTranscriptAsync(captureId, speechEpoch, text, ct).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (WebSocketException) { }
            catch (Exception ex)
            {
                _logger.LogWarning("Duplex worker STT reader stopped with {ExceptionType}.", ex.GetType().Name);
            }
            finally
            {
                Array.Clear(buffer);
            }
        }

        private async Task HandlePartialTranscriptAsync(
            string captureId,
            long speechEpoch,
            long revision,
            string text,
            string providerKind,
            CancellationToken ct)
        {
            var partialResult = _coordinator.RecordPartial(_tenant, _principal, _sessionId, captureId, speechEpoch, revision, text);
            if (!partialResult.Accepted)
            {
                if (partialResult.RejectionReason == "stale_speech_epoch")
                {
                    VoiceDuplexMetrics.RecordStaleEpochRejected("partial");
                }

                return;
            }

            if (!string.Equals(_lastPartialText, partialResult.PartialText, StringComparison.Ordinal))
            {
                _lastPartialText = partialResult.PartialText ?? string.Empty;
                _partialStableTimestamp = Stopwatch.GetTimestamp();
            }

            if (_speechStartTimestamp > 0 && Interlocked.Exchange(ref _firstPartialRecordedForEpoch, speechEpoch) != speechEpoch)
            {
                VoiceDuplexMetrics.RecordFirstPartial(Stopwatch.GetElapsedTime(_speechStartTimestamp).TotalMilliseconds, providerKind);
            }

            await EnqueueControlAsync(new
            {
                version = VoiceBinaryFrameCodec.ProtocolVersion,
                type = "transcript.partial",
                voiceConnectionId = _voiceConnectionId,
                captureId,
                speechEpoch = partialResult.SpeechEpoch,
                sequence = Interlocked.Increment(ref _outboundControlSeq),
                revision = partialResult.Revision,
                text = partialResult.PartialText,
                authoritative = false
            }, ct).ConfigureAwait(false);
        }

        private async Task HandleFinalTranscriptAsync(
            string captureId,
            long speechEpoch,
            string finalText,
            CancellationToken ct)
        {
            var admission = _coordinator.TryAdmitFinal(_tenant, _principal, _sessionId, captureId, speechEpoch, finalText);
            if (!admission.Admitted)
            {
                if (admission.RejectionReason == "stale_speech_epoch")
                {
                    VoiceDuplexMetrics.RecordStaleEpochRejected("final");
                }

                return;
            }

            _activeCaptureId = null;
            _currentUtteranceBytes = 0;

            await EnqueueControlAsync(new
            {
                version = VoiceBinaryFrameCodec.ProtocolVersion,
                type = "transcript.final",
                voiceConnectionId = _voiceConnectionId,
                captureId,
                speechEpoch = admission.SpeechEpoch,
                sequence = Interlocked.Increment(ref _outboundControlSeq),
                text = admission.Transcript,
                authoritative = true
            }, ct).ConfigureAwait(false);

            var admittedTask = await AdmitDurableTurnAsync(admission.IdempotencyKey!, admission.Transcript!, ct).ConfigureAwait(false);
            if (admittedTask is null)
            {
                await EnqueueControlAsync(new
                {
                    version = VoiceBinaryFrameCodec.ProtocolVersion,
                    type = "response.error",
                    voiceConnectionId = _voiceConnectionId,
                    captureId,
                    speechEpoch = admission.SpeechEpoch,
                    sequence = Interlocked.Increment(ref _outboundControlSeq),
                    errorCode = ErrorCodes.CapabilityUnavailable,
                    reason = "Voice task could not be admitted."
                }, ct).ConfigureAwait(false);
                return;
            }

            StartResponseStreaming(admittedTask.Id, admission.SpeechEpoch, ct);
        }

        private async Task<TaskRecord?> AdmitDurableTurnAsync(string idempotencyKey, string transcript, CancellationToken ct)
        {
            var dispatcher = _context.RequestServices.GetService<IHostTaskExecutionDispatcher>();
            if (dispatcher is null
                || _context.RequestServices.GetService<IHostTaskExecutionAuthorizer>() is null
                || _context.RequestServices.GetService<IHostTaskExecutor>() is null)
            {
                return null;
            }

            var origin = _clientConnectionId is null ? HostTaskOrigin.NonInteractive : HostTaskOrigin.MiniComposer;
            using var presence = _clientConnectionId is null
                ? null
                : _context.RequestServices.GetService<InMemorySessionStore>()?.TryClaimSoleConnection(_sessionId);
            if (_clientConnectionId is not null
                && (presence is null || !string.Equals(presence.ConnectionId, _clientConnectionId, StringComparison.Ordinal)))
            {
                return null;
            }

            var request = new TaskRequest(idempotencyKey, transcript, _sessionIdText)
            {
                BoundConnectionId = presence?.ConnectionId
            };
            var fingerprint = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{(int)origin}\n{request.ClientTaskId}\n{transcript}\n{_sessionIdText}")));
            var result = await dispatcher.AcceptAsync(
                request,
                idempotencyKey,
                fingerprint,
                _tenant,
                _principal,
                ct,
                origin).ConfigureAwait(false);

            return result.Status == ExecutionAdmissionStatus.Accepted ? result.Task : null;
        }

        private void StartResponseStreaming(TaskId taskId, long speechEpoch, CancellationToken sessionCt)
        {
            var responseId = taskId.Value.ToString();
            var startResult = _coordinator.StartResponse(_tenant, _principal, _sessionId, responseId, speechEpoch);
            if (!startResult.Accepted)
            {
                return;
            }

            var responseCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
            lock (_activeStateLock)
            {
                _activeResponseCts?.Cancel();
                _activeResponseCts?.Dispose();
                _activeResponseCts = responseCts;
                _activeTaskId = taskId.Value;
                _activeResponseTask = Task.Run(
                    () => StreamAdmittedTaskResponseAsync(taskId, responseId, speechEpoch, responseCts.Token),
                    CancellationToken.None);
            }
        }

        private async Task StreamAdmittedTaskResponseAsync(
            TaskId taskId,
            string responseId,
            long speechEpoch,
            CancellationToken ct)
        {
            var turnStartTs = Stopwatch.GetTimestamp();
            var firstPhraseRecorded = false;
            var firstTtsChunkRecorded = false;

            var sessions = _context.RequestServices.GetService<InMemorySessionStore>();
            var synthesizer = _context.RequestServices.GetService<IStreamingSpeechSynthesizer>();
            var egressGate = _context.RequestServices.GetService<IEgressGate>();
            var canSynthesize = _options.EnableSpeechOutput
                && _negotiatedStreamingSpeech
                && synthesizer is { IsConfigured: true };

            var formatter = new SpeechPhraseFormatter(new SpeechPhraseFormatterOptions(
                Math.Max(1, _options.MinPhraseWords),
                Math.Max(Math.Max(1, _options.MinPhraseWords), _options.MaxPhraseWords)));

            var streamId = Interlocked.Increment(ref _ttsStreamId);
            long sampleOffset = 0;
            var sawDelta = false;
            var taskIdText = taskId.Value.ToString();

            await EnqueueControlAsync(new
            {
                version = VoiceBinaryFrameCodec.ProtocolVersion,
                type = "response.started",
                voiceConnectionId = _voiceConnectionId,
                responseId,
                speechEpoch,
                sequence = Interlocked.Increment(ref _outboundControlSeq)
            }, ct).ConfigureAwait(false);

            if (sessions is null)
            {
                _coordinator.CompleteResponseGeneration(_tenant, _principal, _sessionId, responseId, speechEpoch);
                return;
            }

            long cursor = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var events = sessions.GetEvents(_sessionId.Value, cursor);
                    var terminal = false;

                    foreach (var ev in events)
                    {
                        cursor = ev.Sequence;
                        ct.ThrowIfCancellationRequested();

                        var matchesTask = string.Equals(ev.TaskId, taskIdText, StringComparison.Ordinal)
                            || EventDataContainsTaskId(ev.Data, taskIdText);
                        if (!matchesTask)
                        {
                            continue;
                        }

                        if (string.Equals(ev.Type, "message.delta", StringComparison.Ordinal))
                        {
                            sawDelta = true;
                            await EnqueueControlAsync(new
                            {
                                version = VoiceBinaryFrameCodec.ProtocolVersion,
                                type = "response.text.delta",
                                voiceConnectionId = _voiceConnectionId,
                                responseId,
                                speechEpoch,
                                sequence = Interlocked.Increment(ref _outboundControlSeq),
                                text = ev.Data
                            }, ct).ConfigureAwait(false);

                            if (canSynthesize)
                            {
                                var segments = formatter.AppendDelta(ev.Data);
                                foreach (var segment in segments)
                                {
                                    if (!firstPhraseRecorded)
                                    {
                                        firstPhraseRecorded = true;
                                        VoiceDuplexMetrics.RecordFirstSpeakablePhrase(Stopwatch.GetElapsedTime(turnStartTs).TotalMilliseconds);
                                    }

                                    sampleOffset = await SynthesizeAndEmitSegmentAsync(
                                        synthesizer!,
                                        egressGate,
                                        responseId,
                                        speechEpoch,
                                        streamId,
                                        segment,
                                        sampleOffset,
                                        turnStartTs,
                                        () =>
                                        {
                                            if (!firstTtsChunkRecorded)
                                            {
                                                firstTtsChunkRecorded = true;
                                                VoiceDuplexMetrics.RecordFirstTtsChunk(Stopwatch.GetElapsedTime(turnStartTs).TotalMilliseconds);
                                            }
                                        },
                                        ct).ConfigureAwait(false);
                                }
                            }
                        }
                        else if (string.Equals(ev.Type, "message", StringComparison.Ordinal))
                        {
                            await EnqueueControlAsync(new
                            {
                                version = VoiceBinaryFrameCodec.ProtocolVersion,
                                type = "response.text.final",
                                voiceConnectionId = _voiceConnectionId,
                                responseId,
                                speechEpoch,
                                sequence = Interlocked.Increment(ref _outboundControlSeq),
                                text = ev.Data,
                                authoritative = true
                            }, ct).ConfigureAwait(false);

                            if (canSynthesize)
                            {
                                if (!sawDelta)
                                {
                                    var segments = formatter.AppendDelta(ev.Data, forceFlush: true);
                                    foreach (var segment in segments)
                                    {
                                        if (!firstPhraseRecorded)
                                        {
                                            firstPhraseRecorded = true;
                                            VoiceDuplexMetrics.RecordFirstSpeakablePhrase(Stopwatch.GetElapsedTime(turnStartTs).TotalMilliseconds);
                                        }

                                        sampleOffset = await SynthesizeAndEmitSegmentAsync(
                                            synthesizer!,
                                            egressGate,
                                            responseId,
                                            speechEpoch,
                                            streamId,
                                            segment,
                                            sampleOffset,
                                            turnStartTs,
                                            () =>
                                            {
                                                if (!firstTtsChunkRecorded)
                                                {
                                                    firstTtsChunkRecorded = true;
                                                    VoiceDuplexMetrics.RecordFirstTtsChunk(Stopwatch.GetElapsedTime(turnStartTs).TotalMilliseconds);
                                                }
                                            },
                                            ct).ConfigureAwait(false);
                                    }
                                }
                                else
                                {
                                    var tail = formatter.Flush();
                                    if (tail is not null)
                                    {
                                        if (!firstPhraseRecorded)
                                        {
                                            firstPhraseRecorded = true;
                                            VoiceDuplexMetrics.RecordFirstSpeakablePhrase(Stopwatch.GetElapsedTime(turnStartTs).TotalMilliseconds);
                                        }

                                        sampleOffset = await SynthesizeAndEmitSegmentAsync(
                                            synthesizer!,
                                            egressGate,
                                            responseId,
                                            speechEpoch,
                                            streamId,
                                            tail,
                                            sampleOffset,
                                            turnStartTs,
                                            () =>
                                            {
                                                if (!firstTtsChunkRecorded)
                                                {
                                                    firstTtsChunkRecorded = true;
                                                    VoiceDuplexMetrics.RecordFirstTtsChunk(Stopwatch.GetElapsedTime(turnStartTs).TotalMilliseconds);
                                                }
                                            },
                                            ct).ConfigureAwait(false);
                                    }
                                }
                            }
                        }
                        else if (string.Equals(ev.Type, "task.completed", StringComparison.Ordinal))
                        {
                            if (canSynthesize)
                            {
                                var tail = formatter.Flush();
                                if (tail is not null)
                                {
                                    sampleOffset = await SynthesizeAndEmitSegmentAsync(
                                        synthesizer!,
                                        egressGate,
                                        responseId,
                                        speechEpoch,
                                        streamId,
                                        tail,
                                        sampleOffset,
                                        turnStartTs,
                                        () =>
                                        {
                                            if (!firstTtsChunkRecorded)
                                            {
                                                firstTtsChunkRecorded = true;
                                                VoiceDuplexMetrics.RecordFirstTtsChunk(Stopwatch.GetElapsedTime(turnStartTs).TotalMilliseconds);
                                            }
                                        },
                                        ct).ConfigureAwait(false);
                                }
                            }

                            _coordinator.CompleteResponseGeneration(_tenant, _principal, _sessionId, responseId, speechEpoch);
                            await EnqueueControlAsync(new
                            {
                                version = VoiceBinaryFrameCodec.ProtocolVersion,
                                type = "response.completed",
                                voiceConnectionId = _voiceConnectionId,
                                responseId,
                                speechEpoch,
                                sequence = Interlocked.Increment(ref _outboundControlSeq),
                                sampleCount = sampleOffset
                            }, ct).ConfigureAwait(false);
                            terminal = true;
                            break;
                        }
                        else if (string.Equals(ev.Type, "task.failed", StringComparison.Ordinal))
                        {
                            await EnqueueControlAsync(new
                            {
                                version = VoiceBinaryFrameCodec.ProtocolVersion,
                                type = "response.error",
                                voiceConnectionId = _voiceConnectionId,
                                responseId,
                                speechEpoch,
                                sequence = Interlocked.Increment(ref _outboundControlSeq),
                                errorCode = ErrorCodes.CapabilityUnavailable,
                                reason = "Agent execution failed."
                            }, ct).ConfigureAwait(false);
                            terminal = true;
                            break;
                        }
                        else if (string.Equals(ev.Type, "task.cancelled", StringComparison.Ordinal))
                        {
                            terminal = true;
                            break;
                        }
                    }

                    if (terminal)
                    {
                        break;
                    }

                    await Task.Delay(25, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Expected on barge-in interrupt or disconnect.
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Duplex response stream failed with {ExceptionType}.", ex.GetType().Name);
                try
                {
                    await EnqueueControlAsync(new
                    {
                        version = VoiceBinaryFrameCodec.ProtocolVersion,
                        type = "response.error",
                        voiceConnectionId = _voiceConnectionId,
                        responseId,
                        speechEpoch,
                        sequence = Interlocked.Increment(ref _outboundControlSeq),
                        errorCode = ErrorCodes.CapabilityUnavailable,
                        reason = "Voice response synthesis failed."
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore transport failure while reporting error.
                }
            }
        }

        private async Task<long> SynthesizeAndEmitSegmentAsync(
            IStreamingSpeechSynthesizer synthesizer,
            IEgressGate? egressGate,
            string responseId,
            long speechEpoch,
            uint streamId,
            SpeakablePhraseSegment segment,
            long currentSampleOffset,
            long turnStartTs,
            Action onFirstChunk,
            CancellationToken ct)
        {
            var spokenText = segment.SpokenText;
            if (string.IsNullOrWhiteSpace(spokenText))
            {
                return currentSampleOffset;
            }

            // Screen complete speakable segment fail-closed before synthesis when privacy gate is registered.
            if (egressGate is not null)
            {
                var egressContext = new EgressContext(
                    _tenant,
                    _principal,
                    _sessionId,
                    EgressDestination.Output);
                var decision = await egressGate.EvaluateAsync(spokenText, egressContext, ct).ConfigureAwait(false);
                if (decision is null
                    || !decision.Allowed
                    || decision.Action is SecurityPolicyAction.Block or SecurityPolicyAction.Review
                    || string.IsNullOrWhiteSpace(decision.SanitizedContent))
                {
                    return currentSampleOffset;
                }

                spokenText = decision.SanitizedContent;
            }

            var segmentId = $"seg-{segment.SegmentIndex}";
            var segmentStartSample = currentSampleOffset;
            long segmentSamples = 0;

            var pcmStream = synthesizer is KokoroStreamingSpeechSynthesizer kokoro
                ? kokoro.SynthesizePcmStreamAsync(
                    spokenText,
                    _negotiatedVoice,
                    _negotiatedLocale,
                    speed: 0.95f,
                    responseId,
                    segmentId,
                    speechEpoch,
                    ct)
                : synthesizer.SynthesizePcmStreamAsync(
                    spokenText,
                    _negotiatedVoice,
                    _negotiatedLocale,
                    speed: 0.95f,
                    ct);

            await foreach (var chunk in pcmStream.WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (chunk.IsEmpty || (chunk.Length & 1) != 0)
                {
                    continue;
                }

                var snapshot = _coordinator.GetSnapshot(_tenant, _principal, _sessionId);
                if (snapshot is null || snapshot.SpeechEpoch != speechEpoch)
                {
                    return currentSampleOffset;
                }

                onFirstChunk();

                var offset = 0;
                while (offset < chunk.Length)
                {
                    var sliceLen = Math.Min(VoiceBinaryFrameCodec.MaxPayloadBytes, chunk.Length - offset);
                    sliceLen &= ~1; // Ensure even PCM16 byte count
                    if (sliceLen <= 0)
                    {
                        break;
                    }

                    var frameSeq = Interlocked.Increment(ref _outboundAudioSeq) - 1;
                    var header = new VoiceAudioFrameHeader(
                        VoiceAudioStreamKind.TtsOutput,
                        streamId,
                        speechEpoch,
                        frameSeq,
                        currentSampleOffset);

                    var frameBytes = new byte[VoiceBinaryFrameCodec.HeaderSize + sliceLen];
                    if (VoiceBinaryFrameCodec.TryEncode(header, chunk.Span.Slice(offset, sliceLen), frameBytes, out _))
                    {
                        if (!_audioChannel.Writer.TryWrite(frameBytes))
                        {
                            VoiceDuplexMetrics.RecordOverload("output_queue");
                            throw new InvalidOperationException("Voice output buffer overloaded.");
                        }

                        _writeSignal.Release();
                    }

                    var samples = sliceLen / BytesPerSample;
                    currentSampleOffset += samples;
                    segmentSamples += samples;
                    offset += sliceLen;
                }
            }

            if (segmentSamples > 0)
            {
                var metadata = new VoiceSegmentMetadata(
                    segmentId,
                    segment.SegmentIndex,
                    spokenText,
                    segment.SourceText,
                    segmentStartSample,
                    segmentSamples);
                _coordinator.RegisterAudioSegment(_tenant, _principal, _sessionId, responseId, speechEpoch, metadata);

                await EnqueueControlAsync(new
                {
                    version = VoiceBinaryFrameCodec.ProtocolVersion,
                    type = "response.audio.segment",
                    voiceConnectionId = _voiceConnectionId,
                    responseId,
                    segmentId,
                    speechEpoch,
                    sequence = Interlocked.Increment(ref _outboundControlSeq),
                    sampleOffset = segmentStartSample,
                    sampleCount = segmentSamples,
                    text = spokenText
                }, ct).ConfigureAwait(false);
            }

            return currentSampleOffset;
        }

        private async Task CancelActiveResponseAsync(bool cancelAdmittedTask)
        {
            CancellationTokenSource? cts;
            Task? responseTask;
            Ulid? taskId;

            lock (_activeStateLock)
            {
                cts = _activeResponseCts;
                responseTask = _activeResponseTask;
                taskId = _activeTaskId;
                _activeResponseCts = null;
                _activeResponseTask = null;
                _activeTaskId = null;
            }

            if (cts is not null)
            {
                try
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                }
                catch { }
            }

            if (cancelAdmittedTask && taskId is not null)
            {
                var dispatcher = _context.RequestServices.GetService<IHostTaskExecutionDispatcher>();
                if (dispatcher is not null)
                {
                    try
                    {
                        await dispatcher.CancelAsync(taskId.Value, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { }
                }
            }

            if (responseTask is not null)
            {
                try
                {
                    await responseTask.ConfigureAwait(false);
                }
                catch { }
            }

            cts?.Dispose();
        }

        private async Task CancelSdkUtteranceAsync()
        {
            var cts = _sdkUtteranceCts;
            var pipe = _sdkUtterancePipe;
            var task = _sdkUtteranceTask;
            _sdkUtteranceCts = null;
            _sdkUtterancePipe = null;
            _sdkUtteranceTask = null;

            if (cts is not null)
            {
                try { await cts.CancelAsync().ConfigureAwait(false); } catch { }
            }

            if (pipe is not null)
            {
                try { await pipe.Writer.CompleteAsync().ConfigureAwait(false); } catch { }
            }

            if (task is not null)
            {
                try { await task.ConfigureAwait(false); } catch { }
            }

            cts?.Dispose();
        }

        private async Task CloseSttResourcesAsync()
        {
            await CancelSdkUtteranceAsync().ConfigureAwait(false);

            var socket = _workerSttSocket;
            var reader = _workerSttReaderTask;
            _workerSttSocket = null;
            _workerSttReaderTask = null;

            if (socket is not null)
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        await SendWorkerSttControlAsync(new { type = "closeSession" }, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch { }

                try
                {
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "session_closed", closeCts.Token).ConfigureAwait(false);
                    }
                }
                catch { }

                socket.Dispose();
            }

            if (reader is not null)
            {
                try { await reader.ConfigureAwait(false); } catch { }
            }
        }

        private async Task SendWorkerSttControlAsync(object payload, CancellationToken ct)
        {
            if (_workerSttSocket is not { State: WebSocketState.Open })
            {
                return;
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            await _workerSttSendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_workerSttSocket.State == WebSocketState.Open)
                {
                    await _workerSttSocket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _workerSttSendGate.Release();
            }
        }

        private bool TryConsumeRollingAudioBudget(int byteCount)
        {
            var now = DateTimeOffset.UtcNow;
            var window = TimeSpan.FromSeconds(Math.Max(1, _options.RollingSessionWindowSeconds));
            var cutoff = now - window;

            while (_rollingAudioWindow.Count > 0 && _rollingAudioWindow.Peek().Timestamp < cutoff)
            {
                var expired = _rollingAudioWindow.Dequeue();
                _rollingAudioBytes = Math.Max(0, _rollingAudioBytes - expired.Bytes);
            }

            if (_rollingAudioBytes + byteCount > _options.MaxRollingSessionAudioBytes)
            {
                return false;
            }

            _rollingAudioWindow.Enqueue((now, byteCount));
            _rollingAudioBytes += byteCount;
            return true;
        }

        private async Task DrainPendingControlMessagesAsync(CancellationToken ct)
        {
            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            drainCts.CancelAfter(TimeSpan.FromMilliseconds(250));
            try
            {
                while (_controlChannel.Reader.Count > 0 && _browser.State == WebSocketState.Open && !drainCts.IsCancellationRequested)
                {
                    await Task.Delay(5, drainCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Best-effort drain on graceful close.
            }
        }

        private async ValueTask EnqueueControlAsync<T>(T payload, CancellationToken ct)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            await _controlChannel.Writer.WriteAsync(bytes, ct).ConfigureAwait(false);
            _writeSignal.Release();
        }

        private async Task SendErrorAndCloseAsync(string errorCode, string reason, WebSocketCloseStatus status, CancellationToken ct)
        {
            try
            {
                if (_browser.State == WebSocketState.Open)
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        version = VoiceBinaryFrameCodec.ProtocolVersion,
                        type = "session.error",
                        voiceConnectionId = _voiceConnectionId,
                        errorCode,
                        reason
                    }, JsonOptions);
                    await _browser.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
                }
            }
            catch { }

            await VoiceRealtimeWebSocketHandler.CloseAsync(_browser, status, errorCode, ct).ConfigureAwait(false);
        }

        private async Task RunWriterLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _writeSignal.WaitAsync(ct).ConfigureAwait(false);

                    // Always drain all control frames before sending any audio frame.
                    while (_controlChannel.Reader.TryRead(out var controlBytes))
                    {
                        if (_browser.State != WebSocketState.Open)
                        {
                            return;
                        }

                        await _browser.SendAsync(controlBytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
                    }

                    if (_audioChannel.Reader.TryRead(out var audioBytes))
                    {
                        if (_browser.State != WebSocketState.Open)
                        {
                            return;
                        }

                        await _browser.SendAsync(audioBytes, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (WebSocketException) { }
        }

        private static bool EventDataContainsTaskId(string? data, string taskId)
        {
            if (string.IsNullOrWhiteSpace(data))
            {
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(data);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("taskId", out var prop)
                    && prop.ValueKind == JsonValueKind.String
                    && string.Equals(prop.GetString(), taskId, StringComparison.Ordinal);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string MergeTranscriptSegment(string? accumulated, string segment)
        {
            if (string.IsNullOrWhiteSpace(accumulated))
            {
                return segment;
            }

            if (segment.StartsWith(accumulated, StringComparison.OrdinalIgnoreCase))
            {
                return segment;
            }

            if (accumulated.EndsWith(segment, StringComparison.OrdinalIgnoreCase))
            {
                return accumulated;
            }

            return $"{accumulated} {segment}";
        }

        private static string? ReadString(JsonElement root, string name) =>
            root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

        private static long? ReadInt64(JsonElement root, string name) =>
            root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var val) ? val : null;

        private static bool? ReadBool(JsonElement root, string name) =>
            root.TryGetProperty(name, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False ? prop.GetBoolean() : null;
    }
}
