using System.Buffers.Binary;
using System.Text.Json.Serialization;

namespace OpenConnect.Abstractions.Speech;

/// <summary>Transport connection lifecycle state for a duplex realtime voice session.</summary>
[JsonConverter(typeof(KebabCaseStringEnumConverter<VoiceConnectionState>))]
public enum VoiceConnectionState
{
    Opening = 0,
    Ready = 1,
    Reconnecting = 2,
    Closing = 3,
    Closed = 4,
    Faulted = 5
}

/// <summary>Concurrent microphone capture and endpointing state for a duplex voice session.</summary>
[JsonConverter(typeof(KebabCaseStringEnumConverter<VoiceInputState>))]
public enum VoiceInputState
{
    Listening = 0,
    SpeechDetected = 1,
    EndpointPending = 2,
    Finalizing = 3,
    Muted = 4
}

/// <summary>Concurrent agent reasoning and tool execution state for a duplex voice session.</summary>
[JsonConverter(typeof(KebabCaseStringEnumConverter<VoiceReasoningState>))]
public enum VoiceReasoningState
{
    Idle = 0,
    Generating = 1,
    AwaitingTool = 2,
    AwaitingConfirmation = 3,
    Cancelling = 4,
    Completed = 5,
    Failed = 6
}

/// <summary>Concurrent speech synthesis and playback output state for a duplex voice session.</summary>
[JsonConverter(typeof(KebabCaseStringEnumConverter<VoiceOutputState>))]
public enum VoiceOutputState
{
    Idle = 0,
    Buffering = 1,
    Playing = 2,
    Draining = 3,
    Interrupted = 4,
    Failed = 5
}

/// <summary>Audio stream direction/kind encoded in a binary v2 voice audio frame header.</summary>
public enum VoiceAudioStreamKind : byte
{
    MicInput = 1,
    TtsOutput = 2
}

/// <summary>Fixed 32-byte little-endian binary frame header for <c>openconnect.voice.v2</c> PCM16 audio frames.</summary>
public readonly record struct VoiceAudioFrameHeader(
    VoiceAudioStreamKind Kind,
    uint StreamId,
    long SpeechEpoch,
    long Sequence,
    long SampleOffset);

/// <summary>Encodes and decodes validated 32-byte little-endian <c>openconnect.voice.v2</c> binary audio frames.</summary>
public static class VoiceBinaryFrameCodec
{
    /// <summary>First magic byte (<c>'O'</c> / <c>0x4F</c>).</summary>
    public const byte MagicByte0 = 0x4F;

    /// <summary>Second magic byte (<c>'C'</c> / <c>0x43</c>).</summary>
    public const byte MagicByte1 = 0x43;

    /// <summary>Binary framing protocol version.</summary>
    public const byte ProtocolVersion = 2;

    /// <summary>Fixed header length in bytes.</summary>
    public const int HeaderSize = 32;

    /// <summary>Maximum allowed total binary frame size in bytes (96 KB).</summary>
    public const int MaxFrameBytes = 96 * 1024;

    /// <summary>Maximum allowed PCM16 payload size in bytes within a single frame.</summary>
    public const int MaxPayloadBytes = MaxFrameBytes - HeaderSize;

    /// <summary>Validates and encodes a binary audio frame into <paramref name="destination"/>.</summary>
    public static bool TryEncode(
        VoiceAudioFrameHeader header,
        ReadOnlySpan<byte> pcmPayload,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;

        if (!IsValidKind(header.Kind)
            || header.SpeechEpoch < 0
            || header.Sequence < 0
            || header.SampleOffset < 0)
        {
            return false;
        }

        if ((pcmPayload.Length & 1) != 0)
        {
            return false;
        }

        if (pcmPayload.Length > MaxPayloadBytes)
        {
            return false;
        }

        var totalBytes = HeaderSize + pcmPayload.Length;
        if (destination.Length < totalBytes)
        {
            return false;
        }

        destination[0] = MagicByte0;
        destination[1] = MagicByte1;
        destination[2] = ProtocolVersion;
        destination[3] = (byte)header.Kind;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4, 4), header.StreamId);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(8, 8), header.SpeechEpoch);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(16, 8), header.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(24, 8), header.SampleOffset);

        if (!pcmPayload.IsEmpty)
        {
            pcmPayload.CopyTo(destination.Slice(HeaderSize));
        }

        bytesWritten = totalBytes;
        return true;
    }

    /// <summary>Validates and decodes a binary audio frame into its header and PCM16 payload slice.</summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> frame,
        out VoiceAudioFrameHeader header,
        out ReadOnlySpan<byte> pcmPayload)
    {
        header = default;
        pcmPayload = default;

        if (frame.Length < HeaderSize || frame.Length > MaxFrameBytes)
        {
            return false;
        }

        if (frame[0] != MagicByte0 || frame[1] != MagicByte1 || frame[2] != ProtocolVersion)
        {
            return false;
        }

        var kind = (VoiceAudioStreamKind)frame[3];
        if (!IsValidKind(kind))
        {
            return false;
        }

        var payloadLength = frame.Length - HeaderSize;
        if ((payloadLength & 1) != 0)
        {
            return false;
        }

        var streamId = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(4, 4));
        var speechEpoch = BinaryPrimitives.ReadInt64LittleEndian(frame.Slice(8, 8));
        var sequence = BinaryPrimitives.ReadInt64LittleEndian(frame.Slice(16, 8));
        var sampleOffset = BinaryPrimitives.ReadInt64LittleEndian(frame.Slice(24, 8));

        if (speechEpoch < 0 || sequence < 0 || sampleOffset < 0)
        {
            return false;
        }

        header = new VoiceAudioFrameHeader(kind, streamId, speechEpoch, sequence, sampleOffset);
        pcmPayload = frame.Slice(HeaderSize);
        return true;
    }

    private static bool IsValidKind(VoiceAudioStreamKind kind) =>
        kind is VoiceAudioStreamKind.MicInput or VoiceAudioStreamKind.TtsOutput;
}

/// <summary>Structured control or decoded audio event emitted on a <c>openconnect.voice.v2</c> realtime session.</summary>
public sealed record VoiceSessionEvent(
    int Version,
    string Type,
    string? VoiceConnectionId = null,
    string? CaptureId = null,
    string? ResponseId = null,
    string? SegmentId = null,
    long? SpeechEpoch = null,
    long? Sequence = null,
    long? Revision = null,
    string? Text = null,
    bool? Authoritative = null,
    long? SampleOffset = null,
    long? SampleCount = null,
    string? Reason = null,
    string? ErrorCode = null,
    string? PayloadJson = null)
{
    /// <summary>Optional decoded PCM16 audio payload for binary audio events.</summary>
    [JsonIgnore]
    public ReadOnlyMemory<byte> AudioPayload { get; init; }

    /// <summary>Optional decoded 32-byte audio frame header for binary audio events.</summary>
    [JsonIgnore]
    public VoiceAudioFrameHeader? AudioHeader { get; init; }
}

/// <summary>Client request to interrupt an active assistant response with last rendered playback cursor.</summary>
public sealed record VoiceInterruptRequest(
    string RequestId,
    string? ResponseId = null,
    long SpeechEpoch = 0,
    long PlayedSamples = 0,
    string? Reason = null);

/// <summary>Advisory client playback progress receipt bounded by samples actually transmitted.</summary>
public sealed record VoicePlaybackReceipt(
    string ResponseId,
    long SpeechEpoch,
    long PlayedSamples,
    bool Completed = false);

/// <summary>Maps an ordered speakable phrase segment to its source text and sample range within an assistant response.</summary>
public sealed record VoiceSegmentMetadata(
    string SegmentId,
    int SegmentIndex,
    string SpokenText,
    string SourceText,
    long StartSampleOffset,
    long SampleCount)
{
    /// <summary>Exclusive end sample offset of this segment.</summary>
    public long EndSampleOffset => StartSampleOffset + SampleCount;
}

/// <summary>Reconciliation of confirmed-played, uncertain partially played, and unheard segments after interruption or disconnect.</summary>
public sealed record VoiceDeliveryReconciliation(
    string ResponseId,
    long SpeechEpoch,
    long TransmittedSamples,
    long ReportedPlayedSamples,
    bool ReceiptReceived,
    IReadOnlyList<VoiceSegmentMetadata> ConfirmedPlayedSegments,
    VoiceSegmentMetadata? UncertainPartiallyPlayedSegment,
    IReadOnlyList<VoiceSegmentMetadata> UnheardSegments,
    string ConfirmedText,
    string? UncertainText);

/// <summary>Negotiated options sent when opening a <c>openconnect.voice.v2</c> duplex voice session.</summary>
public sealed record VoiceDuplexSessionOptions(
    string Mode = "duplex",
    string Codec = "pcm16le",
    int InputSampleRateHz = 24_000,
    int OutputSampleRateHz = 24_000,
    int Channels = 1,
    string? Voice = null,
    string? Locale = null,
    bool EnableStreamingSpeechOutput = true,
    bool EnableSemanticTurnDetection = false);

/// <summary>Full-duplex realtime voice session over <c>openconnect.voice.v2</c>.</summary>
public interface IRealtimeVoiceSession : IAsyncDisposable
{
    /// <summary>Authorized durable conversation/session identifier.</summary>
    string SessionId { get; }

    /// <summary>Unique transport generation identifier for this connection.</summary>
    string VoiceConnectionId { get; }

    /// <summary>Streams a 24 kHz mono 16-bit PCM microphone audio frame to the gateway.</summary>
    ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default);

    /// <summary>Reads ordered control events and decoded TTS audio events from the gateway.</summary>
    IAsyncEnumerable<VoiceSessionEvent> ReadEventsAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests gateway-authoritative interruption of the active response.</summary>
    ValueTask InterruptAsync(VoiceInterruptRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reports advisory cumulative rendered playback samples for the active response.</summary>
    ValueTask AcknowledgePlaybackAsync(VoicePlaybackReceipt receipt, CancellationToken cancellationToken = default);

    /// <summary>Signals explicit end-of-utterance for the current capture.</summary>
    ValueTask EndUtteranceAsync(CancellationToken cancellationToken = default);

    /// <summary>Mutes or unmutes microphone transmission on this voice connection.</summary>
    ValueTask SetMutedAsync(bool muted, CancellationToken cancellationToken = default);
}

/// <summary>Client factory for opening authenticated <c>openconnect.voice.v2</c> duplex voice sessions.</summary>
public interface IRealtimeVoiceClient
{
    /// <summary>Connects a new full-duplex realtime voice session for an existing authorized session.</summary>
    Task<IRealtimeVoiceSession> ConnectVoiceSessionAsync(
        SessionId sessionId,
        VoiceDuplexSessionOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Streams validated PCM16 speech synthesis chunks incrementally with cancellation support.</summary>
public interface IStreamingSpeechSynthesizer
{
    /// <summary>Gets a value indicating whether streaming speech synthesis is configured and available.</summary>
    bool IsConfigured { get; }

    /// <summary>Synthesizes approved speakable text into an ordered stream of validated 24 kHz mono PCM16LE audio chunks.</summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizePcmStreamAsync(
        string text,
        string? voice = null,
        string? locale = null,
        float speed = 1.0f,
        CancellationToken cancellationToken = default);
}

/// <summary>Stateful streaming speech-to-text session accepting incremental PCM16 frames and emitting partial/final transcripts.</summary>
public interface IStreamingSpeechToTextSession : IAsyncDisposable
{
    /// <summary>Provider-assigned transcription session identifier, if known.</summary>
    string? ProviderSessionId { get; }

    /// <summary>Appends a 24 kHz mono 16-bit PCM audio frame to the active recognition stream.</summary>
    ValueTask SendPcmAsync(ReadOnlyMemory<byte> pcm24kMono16Bit, CancellationToken cancellationToken = default);

    /// <summary>Signals the end of the current spoken utterance.</summary>
    ValueTask EndUtteranceAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads non-authoritative partial transcript updates as speech progresses.</summary>
    IAsyncEnumerable<RealtimePartialTranscript> ReadPartialsAsync(CancellationToken cancellationToken = default);

    /// <summary>Completes the streaming session and returns finalized transcript turns.</summary>
    Task<RealtimeTranscriptionResult> CompleteAsync(CancellationToken cancellationToken = default);
}
