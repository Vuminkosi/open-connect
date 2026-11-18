namespace OpenConnect.Client;

public enum ClientAuthenticationMode
{
    Host = 0,
    BearerToken = 1,
    Cookie = 2,
    None = 3,
}

public enum VadLocation
{
    Disabled = 0,
    Client = 1,
    Server = 2,
    Hybrid = 3,
}

public enum VadEngine
{
    None = 0,
    SileroOnnx = 1,
    WebRtcFast = 2,
}

public sealed class OpenConnectClientReconnectOptions
{
    public bool Enabled { get; set; } = true;
    public bool ResumeFromLastCursor { get; set; } = true;
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class OpenConnectClientOptions
{
    public Uri? ApiBaseAddress { get; set; }
    public Uri? WebSocketAddress { get; set; }
    public ClientAuthenticationMode Authentication { get; set; } = ClientAuthenticationMode.Host;
    public OpenConnectClientReconnectOptions Reconnect { get; } = new();
    public bool AllowInsecureHttp { get; set; }
    public IDictionary<string, string> WebSocketHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        var allowHttp = AllowInsecureHttp;
        if (ApiBaseAddress is null
            || !ApiBaseAddress.IsAbsoluteUri
            || (!allowHttp && ApiBaseAddress.Scheme != Uri.UriSchemeHttps)
            || (allowHttp && ApiBaseAddress.Scheme != Uri.UriSchemeHttps && ApiBaseAddress.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(ApiBaseAddress.Query)
            || !string.IsNullOrEmpty(ApiBaseAddress.Fragment))
        {
            throw new ArgumentException("The API base address must be an absolute HTTPS URL without a query or fragment.", nameof(ApiBaseAddress));
        }

        if (WebSocketAddress is not null
            && (!WebSocketAddress.IsAbsoluteUri
                || (!allowHttp && WebSocketAddress.Scheme != "wss" && WebSocketAddress.Scheme != Uri.UriSchemeHttps)
                || (allowHttp && WebSocketAddress.Scheme != "wss" && WebSocketAddress.Scheme != "ws" && WebSocketAddress.Scheme != Uri.UriSchemeHttps && WebSocketAddress.Scheme != Uri.UriSchemeHttp)
                || !string.IsNullOrEmpty(WebSocketAddress.Query)
                || !string.IsNullOrEmpty(WebSocketAddress.Fragment)))
        {
            throw new ArgumentException("The WebSocket address must be an absolute WSS or HTTPS URL without a query or fragment.", nameof(WebSocketAddress));
        }

        if (!Enum.IsDefined(Authentication))
        {
            throw new ArgumentOutOfRangeException(nameof(Authentication), "Authentication mode is not valid.");
        }

        if (Reconnect.InitialDelay <= TimeSpan.Zero || Reconnect.MaxDelay < Reconnect.InitialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(Reconnect), "Reconnect delays must be positive and MaxDelay must be at least InitialDelay.");
        }
    }
}

public sealed class ClientVadOptions
{
    public bool Enabled { get; set; } = true;
    public VadLocation Location { get; set; } = VadLocation.Client;
    public VadEngine Engine { get; set; } = VadEngine.SileroOnnx;
    public string ModelAssetBaseUrl { get; set; } = "/assets/openconnect/vad/";
    public bool EnablePreRoll { get; set; } = true;
    public int PreRollMilliseconds { get; set; } = 300;
    public bool EnableSilenceSuppression { get; set; } = true;
    public bool EnableBargeIn { get; set; } = true;
    public bool RequireMicrophonePermission { get; set; } = true;
    public int InferenceSampleRate { get; set; } = 16_000;
    public int FrameSamples { get; set; } = 512;

    public void Validate()
    {
        if (!Enum.IsDefined(Location))
        {
            throw new ArgumentOutOfRangeException(nameof(Location), "VAD location is not valid.");
        }

        if (!Enum.IsDefined(Engine))
        {
            throw new ArgumentOutOfRangeException(nameof(Engine), "VAD engine is not valid.");
        }

        if (!Enabled || Location == VadLocation.Disabled)
        {
            return;
        }

        if (!RequireMicrophonePermission)
        {
            throw new ArgumentException("Client VAD requires explicit microphone permission (RequireMicrophonePermission must be true).", nameof(RequireMicrophonePermission));
        }

        if (Location is VadLocation.Client or VadLocation.Hybrid)
        {
            if (Engine == VadEngine.None)
            {
                throw new ArgumentException("Client or Hybrid VAD requires a non-empty VAD engine.", nameof(Engine));
            }

            if (string.IsNullOrWhiteSpace(ModelAssetBaseUrl)
                || (!ModelAssetBaseUrl.StartsWith("/", StringComparison.Ordinal)
                    && !ModelAssetBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("ModelAssetBaseUrl must be a local path starting with '/' or an HTTPS URL.", nameof(ModelAssetBaseUrl));
            }
        }

        if (InferenceSampleRate != 16_000)
        {
            throw new ArgumentOutOfRangeException(nameof(InferenceSampleRate), "Silero v5 ONNX client VAD requires a 16000 Hz inference sample rate.");
        }

        if (FrameSamples != 512)
        {
            throw new ArgumentOutOfRangeException(nameof(FrameSamples), "Silero v5 ONNX client VAD requires 512-sample frames.");
        }

        if (PreRollMilliseconds is < 0 or > 5_000)
        {
            throw new ArgumentOutOfRangeException(nameof(PreRollMilliseconds), "Pre-roll duration must be between 0 and 5000 ms.");
        }
    }
}

public sealed class VoiceClientOptions
{
    public bool EnableLiveTranscription { get; set; } = true;
    public bool EnableVoiceRecording { get; set; } = true;
    public bool EnablePlayback { get; set; } = true;
    public bool EnableBargeIn { get; set; } = true;
    public int MaxRecordingBytes { get; set; } = 10 * 1024 * 1024;
    public TimeSpan MaxRecordingDuration { get; set; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        if (MaxRecordingBytes <= 0 || MaxRecordingBytes > 50 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRecordingBytes), "MaxRecordingBytes must be between 1 byte and 50 MB.");
        }

        if (MaxRecordingDuration <= TimeSpan.Zero || MaxRecordingDuration > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRecordingDuration), "MaxRecordingDuration must be between 1 second and 30 minutes.");
        }
    }
}

public sealed class MauiAudioOptions
{
    public int SampleRate { get; set; } = 24_000;
    public int Channels { get; set; } = 1;
    public bool RequireMicrophonePermission { get; set; } = true;

    public void Validate()
    {
        if (SampleRate is not (16_000 or 24_000 or 44_100 or 48_000))
        {
            throw new ArgumentOutOfRangeException(nameof(SampleRate), "MAUI audio capture supports 16000, 24000, 44100, or 48000 Hz sample rates.");
        }

        if (Channels != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Channels), "MAUI voice capture requires mono (1 channel) PCM input.");
        }

        if (!RequireMicrophonePermission)
        {
            throw new ArgumentException("MAUI audio capture requires explicit platform microphone permission (RequireMicrophonePermission must be true).", nameof(RequireMicrophonePermission));
        }
    }
}

public sealed class NavigatorClientOptions
{
    public bool Enabled { get; set; } = true;
    public bool EnableHighlighting { get; set; } = true;
    public bool EnableGuidedTours { get; set; } = true;
    public bool RequireConsentForActions { get; set; } = true;

    public void Validate()
    {
        if (Enabled && !RequireConsentForActions)
        {
            throw new ArgumentException("Navigator client actions must require explicit user consent (RequireConsentForActions must be true).", nameof(RequireConsentForActions));
        }
    }
}
