using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Serialization;
using OpenConnect.Abstractions.Speech;

namespace OpenConnect.Client;

/// <summary>Full-duplex <c>openconnect.voice.v2</c> WebSocket session with control-prioritized serialized writing and 32-byte binary framing.</summary>
public sealed class RealtimeVoiceSession : IRealtimeVoiceSession
{
    public const string SubProtocol = "openconnect.voice.v2";
    private const int MaxControlMessageBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = OpenConnectHttpJson.CreateOptions();

    private readonly ClientWebSocket _socket;
    private readonly Channel<byte[]> _controlQueue;
    private readonly Channel<byte[]> _audioQueue;
    private readonly SemaphoreSlim _writeSignal = new(0);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _writerTask;
    private readonly uint _micStreamId = 1;

    private string _voiceConnectionId;
    private long _observedSpeechEpoch = 1;
    private long _controlSequence;
    private long _audioSequence;
    private long _audioSampleOffset;
    private int _disposed;

    internal RealtimeVoiceSession(
        ClientWebSocket socket,
        string sessionId,
        string voiceConnectionId,
        int maxBufferedAudioFrames = 256)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        SessionId = sessionId;
        _voiceConnectionId = voiceConnectionId;

        _controlQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        _audioQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(Math.Max(16, maxBufferedAudioFrames))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        _writerTask = Task.Run(RunWriterLoopAsync);
    }

    /// <inheritdoc />
    public string SessionId { get; }

    /// <inheritdoc />
    public string VoiceConnectionId => Volatile.Read(ref _voiceConnectionId);

    /// <inheritdoc />
    public async ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (pcm.IsEmpty)
        {
            return;
        }

        if ((pcm.Length & 1) != 0)
        {
            throw new ArgumentException("PCM16 audio payload must have an even byte length.", nameof(pcm));
        }

        if (pcm.Length > VoiceBinaryFrameCodec.MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pcm),
                $"PCM payload exceeds the maximum frame payload size of {VoiceBinaryFrameCodec.MaxPayloadBytes} bytes.");
        }

        var seq = Interlocked.Increment(ref _audioSequence) - 1;
        var sampleCount = pcm.Length / 2;
        var sampleOffset = Interlocked.Add(ref _audioSampleOffset, sampleCount) - sampleCount;
        var epoch = Volatile.Read(ref _observedSpeechEpoch);

        var header = new VoiceAudioFrameHeader(
            VoiceAudioStreamKind.MicInput,
            _micStreamId,
            epoch,
            seq,
            sampleOffset);

        var frame = new byte[VoiceBinaryFrameCodec.HeaderSize + pcm.Length];
        if (!VoiceBinaryFrameCodec.TryEncode(header, pcm.Span, frame, out _))
        {
            throw new InvalidOperationException("Failed to encode binary voice audio frame.");
        }

        await _audioQueue.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        _writeSignal.Release();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<VoiceSessionEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var token = linked.Token;

        var buffer = new byte[VoiceBinaryFrameCodec.MaxFrameBytes];
        while (_socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    yield break;
                }

                var maxAllowed = result.MessageType == WebSocketMessageType.Binary
                    ? VoiceBinaryFrameCodec.MaxFrameBytes
                    : MaxControlMessageBytes;
                if (message.Length + result.Count > maxAllowed)
                {
                    throw new WebSocketException("Incoming voice frame exceeds the maximum permitted size.");
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var payloadLength = (int)message.Length;
            var payloadBuffer = message.GetBuffer();
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                if (TryDecodeBinaryEvent(payloadBuffer, payloadLength, VoiceConnectionId, out var audioEvent) && audioEvent is not null)
                {
                    yield return audioEvent;
                }

                continue;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            var json = Encoding.UTF8.GetString(payloadBuffer, 0, payloadLength);
            var controlEvent = ParseControlEvent(json);
            if (controlEvent is not null)
            {
                if (!string.IsNullOrWhiteSpace(controlEvent.VoiceConnectionId))
                {
                    Volatile.Write(ref _voiceConnectionId, controlEvent.VoiceConnectionId);
                }

                if (controlEvent.SpeechEpoch is > 0)
                {
                    Volatile.Write(ref _observedSpeechEpoch, controlEvent.SpeechEpoch.Value);
                }

                yield return controlEvent;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask InterruptAsync(VoiceInterruptRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new ArgumentException("A non-empty RequestId is required.", nameof(request));
        }

        var seq = Interlocked.Increment(ref _controlSequence);
        var payload = new
        {
            version = VoiceBinaryFrameCodec.ProtocolVersion,
            type = "response.interrupt",
            voiceConnectionId = VoiceConnectionId,
            requestId = request.RequestId,
            responseId = request.ResponseId,
            speechEpoch = request.SpeechEpoch > 0 ? request.SpeechEpoch : Volatile.Read(ref _observedSpeechEpoch),
            sequence = seq,
            playedSamples = request.PlayedSamples,
            reason = request.Reason
        };

        return EnqueueControlAsync(payload, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask AcknowledgePlaybackAsync(VoicePlaybackReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var seq = Interlocked.Increment(ref _controlSequence);
        var payload = new
        {
            version = VoiceBinaryFrameCodec.ProtocolVersion,
            type = receipt.Completed ? "response.playback.completed" : "response.audio.played",
            voiceConnectionId = VoiceConnectionId,
            responseId = receipt.ResponseId,
            speechEpoch = receipt.SpeechEpoch,
            sequence = seq,
            playedSamples = receipt.PlayedSamples,
            completed = receipt.Completed
        };

        return EnqueueControlAsync(payload, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask EndUtteranceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var seq = Interlocked.Increment(ref _controlSequence);
        var payload = new
        {
            version = VoiceBinaryFrameCodec.ProtocolVersion,
            type = "speech.ended",
            voiceConnectionId = VoiceConnectionId,
            speechEpoch = Volatile.Read(ref _observedSpeechEpoch),
            sequence = seq
        };

        return EnqueueControlAsync(payload, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var seq = Interlocked.Increment(ref _controlSequence);
        var payload = new
        {
            version = VoiceBinaryFrameCodec.ProtocolVersion,
            type = muted ? "session.mute" : "session.unmute",
            voiceConnectionId = VoiceConnectionId,
            speechEpoch = Volatile.Read(ref _observedSpeechEpoch),
            sequence = seq
        };

        return EnqueueControlAsync(payload, cancellationToken);
    }

    internal ValueTask SendSessionOpenAsync(VoiceDuplexSessionOptions options, CancellationToken cancellationToken)
    {
        var seq = Interlocked.Increment(ref _controlSequence);
        var payload = new
        {
            version = VoiceBinaryFrameCodec.ProtocolVersion,
            type = "session.open",
            sessionId = SessionId,
            voiceConnectionId = VoiceConnectionId,
            sequence = seq,
            mode = options.Mode,
            codec = options.Codec,
            inputSampleRateHz = options.InputSampleRateHz,
            outputSampleRateHz = options.OutputSampleRateHz,
            channels = options.Channels,
            voice = options.Voice,
            locale = options.Locale,
            enableStreamingSpeechOutput = options.EnableStreamingSpeechOutput,
            enableSemanticTurnDetection = options.EnableSemanticTurnDetection
        };

        return EnqueueControlAsync(payload, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                var closePayload = new
                {
                    version = VoiceBinaryFrameCodec.ProtocolVersion,
                    type = "session.close",
                    voiceConnectionId = VoiceConnectionId,
                    sequence = Interlocked.Increment(ref _controlSequence)
                };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(closePayload, JsonOptions);
                _controlQueue.Writer.TryWrite(bytes);
                _writeSignal.Release();
            }
        }
        catch
        {
            // Best-effort close notification.
        }

        _controlQueue.Writer.TryComplete();
        _audioQueue.Writer.TryComplete();
        await _disposeCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch
        {
            // Writer cancellation is expected during disposal.
        }

        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "session_disposed", closeCts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore socket close races during disposal.
        }

        _socket.Dispose();
        _writeSignal.Dispose();
        _disposeCts.Dispose();
    }

    private async ValueTask EnqueueControlAsync<T>(T payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await _controlQueue.Writer.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        _writeSignal.Release();
    }

    private async Task RunWriterLoopAsync()
    {
        var token = _disposeCts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _writeSignal.WaitAsync(token).ConfigureAwait(false);

                // Always drain control messages before audio frames to prioritize interrupts and receipts.
                while (_controlQueue.Reader.TryRead(out var controlBytes))
                {
                    if (_socket.State != WebSocketState.Open)
                    {
                        return;
                    }

                    await _socket.SendAsync(controlBytes, WebSocketMessageType.Text, endOfMessage: true, token).ConfigureAwait(false);
                }

                if (_audioQueue.Reader.TryRead(out var audioBytes))
                {
                    if (_socket.State != WebSocketState.Open)
                    {
                        return;
                    }

                    await _socket.SendAsync(audioBytes, WebSocketMessageType.Binary, endOfMessage: true, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disposal.
        }
        catch (WebSocketException)
        {
            // Transport closed.
        }
    }

    private static bool TryDecodeBinaryEvent(
        byte[] buffer,
        int length,
        string voiceConnectionId,
        out VoiceSessionEvent? sessionEvent)
    {
        sessionEvent = null;
        if (!VoiceBinaryFrameCodec.TryDecode(buffer.AsSpan(0, length), out var header, out var pcmSlice))
        {
            return false;
        }

        var pcmCopy = pcmSlice.ToArray();
        sessionEvent = new VoiceSessionEvent(
            Version: VoiceBinaryFrameCodec.ProtocolVersion,
            Type: "response.audio.chunk",
            VoiceConnectionId: voiceConnectionId,
            SpeechEpoch: header.SpeechEpoch,
            Sequence: header.Sequence,
            SampleOffset: header.SampleOffset,
            SampleCount: pcmCopy.Length / 2)
        {
            AudioHeader = header,
            AudioPayload = pcmCopy
        };
        return true;
    }

    private static VoiceSessionEvent? ParseControlEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var type = root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                ? typeProp.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(type))
            {
                return null;
            }

            var version = root.TryGetProperty("version", out var verProp) && verProp.ValueKind == JsonValueKind.Number && verProp.TryGetInt32(out var v)
                ? v
                : VoiceBinaryFrameCodec.ProtocolVersion;

            return new VoiceSessionEvent(
                Version: version,
                Type: type,
                VoiceConnectionId: ReadString(root, "voiceConnectionId"),
                CaptureId: ReadString(root, "captureId"),
                ResponseId: ReadString(root, "responseId"),
                SegmentId: ReadString(root, "segmentId"),
                SpeechEpoch: ReadInt64(root, "speechEpoch"),
                Sequence: ReadInt64(root, "sequence"),
                Revision: ReadInt64(root, "revision"),
                Text: ReadString(root, "text") ?? ReadString(root, "transcript"),
                Authoritative: ReadBool(root, "authoritative"),
                SampleOffset: ReadInt64(root, "sampleOffset"),
                SampleCount: ReadInt64(root, "sampleCount"),
                Reason: ReadString(root, "reason"),
                ErrorCode: ReadString(root, "errorCode") ?? ReadString(root, "code"),
                PayloadJson: json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

    private static long? ReadInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var val) ? val : null;

    private static bool? ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False ? prop.GetBoolean() : null;
}

public partial class OpenConnectClient : IRealtimeVoiceClient
{
    /// <inheritdoc />
    public async Task<IRealtimeVoiceSession> ConnectVoiceSessionAsync(
        SessionId sessionId,
        VoiceDuplexSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (sessionId.Value == default)
        {
            throw new ArgumentException("A valid session ID is required.", nameof(sessionId));
        }

        var effectiveOptions = options ?? new VoiceDuplexSessionOptions();
        var baseAddress = _options?.WebSocketAddress ?? _httpClient.BaseAddress
            ?? throw new InvalidOperationException("The OpenConnect client requires an HTTP or WebSocket base address for realtime voice.");

        var builder = new UriBuilder(baseAddress)
        {
            Scheme = baseAddress.Scheme switch
            {
                "https" => "wss",
                "http" => "ws",
                _ => baseAddress.Scheme
            },
            Path = baseAddress.AbsolutePath.TrimEnd('/') + $"/voice/realtime/{sessionId.Value}",
            Query = string.Empty
        };

        var socket = new ClientWebSocket();
        try
        {
            socket.Options.AddSubProtocol(RealtimeVoiceSession.SubProtocol);
            if (_options?.WebSocketHeaders is not null)
            {
                foreach (var header in _options.WebSocketHeaders)
                {
                    socket.Options.SetRequestHeader(header.Key, header.Value);
                }
            }

            foreach (var header in _httpClient.DefaultRequestHeaders)
            {
                if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                socket.Options.SetRequestHeader(header.Key, string.Join(", ", header.Value));
            }

            await socket.ConnectAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
            var voiceConnectionId = Ulid.NewUlid().ToString();
            var session = new RealtimeVoiceSession(socket, sessionId.Value.ToString(), voiceConnectionId);
            await session.SendSessionOpenAsync(effectiveOptions, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
