namespace OpenConnect.Core.Speech;

/// <summary>Configuration options for OpenConnect realtime and upload speech-to-text clients.</summary>
public sealed class SpeechToTextOptions
{
    /// <summary>Default maximum audio payload size in bytes (32 MB).</summary>
    public const long DefaultMaxAudioBytes = 32 * 1024 * 1024;

    /// <summary>Default realtime WebSocket handshake timeout in seconds.</summary>
    public const int DefaultHandshakeTimeoutSeconds = 10;

    /// <summary>Realtime WebSocket transcription endpoint (<c>wss://</c> or loopback <c>ws://</c>).</summary>
    public string? RealtimeWebSocketUrl { get; set; }

    /// <summary>Whole-recording HTTP(S) transcription upload endpoint (<c>https://</c> or loopback <c>http://</c>).</summary>
    public string? UploadUrl { get; set; }

    /// <summary>Provider API key used for Bearer authentication. Never logged or included in exceptions.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Transcription model identifier.</summary>
    public string Model { get; set; } = "muse-voice-transcribe-1.0";

    /// <summary>Provider transcription mode (for example <c>ENDPOINTING</c>, <c>PUSH_TO_TALK</c>, or <c>DIARIZATION</c>).</summary>
    public string Mode { get; set; } = "ENDPOINTING";

    /// <summary>BCP-47 or provider language code used for upload transcription.</summary>
    public string Language { get; set; } = "en";

    /// <summary>PCM audio encoding identifier sent in the realtime WebSocket handshake.</summary>
    public string AudioEncoding { get; set; } = "PCM_24KHZ";

    /// <summary>Maximum allowed upload audio payload size in bytes.</summary>
    public long MaxAudioBytes { get; set; } = DefaultMaxAudioBytes;

    /// <summary>Maximum time in seconds to complete the realtime WebSocket connection and handshake acknowledgement.</summary>
    public int HandshakeTimeoutSeconds { get; set; } = DefaultHandshakeTimeoutSeconds;

    /// <summary>Gets a value indicating whether a valid realtime WebSocket route and API key are configured.</summary>
    public bool HasRealtimeRoute => TryGetValidRealtimeUri(out _) && !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Gets a value indicating whether a valid upload HTTP(S) route and API key are configured.</summary>
    public bool HasUploadRoute => TryGetValidUploadUri(out _) && !string.IsNullOrWhiteSpace(ApiKey);

    internal bool TryGetValidRealtimeUri(out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(RealtimeWebSocketUrl)
            || !Uri.TryCreate(RealtimeWebSocketUrl.Trim(), UriKind.Absolute, out var candidate)
            || !string.IsNullOrEmpty(candidate.UserInfo))
        {
            return false;
        }

        if (candidate.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)
            || (candidate.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) && candidate.IsLoopback))
        {
            uri = candidate;
            return true;
        }

        return false;
    }

    internal bool TryGetValidUploadUri(out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(UploadUrl)
            || !Uri.TryCreate(UploadUrl.Trim(), UriKind.Absolute, out var candidate)
            || !string.IsNullOrEmpty(candidate.UserInfo))
        {
            return false;
        }

        if (candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || (candidate.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && candidate.IsLoopback))
        {
            uri = candidate;
            return true;
        }

        return false;
    }
}
