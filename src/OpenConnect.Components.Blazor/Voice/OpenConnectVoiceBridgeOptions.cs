namespace OpenConnect.Components.Blazor.Voice;

/// <summary>Configuration for the Blazor Server same-origin voice WebSocket bridge.</summary>
public sealed class OpenConnectVoiceBridgeOptions
{
    public Uri? GatewayBaseAddress { get; set; }

    /// <summary>
    /// Allows plain HTTP WebSocket upgrades on loopback hosts (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>)
    /// while still enforcing HTTPS for all non-loopback hosts. Defaults to <see langword="true"/>.
    /// </summary>
    public bool AllowLoopbackHttp { get; set; } = true;

    public int MaxFrameBytes { get; set; } = 96 * 1024;

    public long MaxAudioBytes { get; set; } = 32 * 1024 * 1024;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (GatewayBaseAddress is null || !GatewayBaseAddress.IsAbsoluteUri ||
            (GatewayBaseAddress.Scheme != Uri.UriSchemeHttps &&
             !(GatewayBaseAddress.Scheme == Uri.UriSchemeHttp && GatewayBaseAddress.IsLoopback)) ||
            !string.IsNullOrEmpty(GatewayBaseAddress.UserInfo) ||
            !string.IsNullOrEmpty(GatewayBaseAddress.Query) ||
            !string.IsNullOrEmpty(GatewayBaseAddress.Fragment))
        {
            throw new InvalidOperationException(
                "OpenConnectVoiceBridgeOptions.GatewayBaseAddress must be an absolute HTTPS URI (or loopback HTTP URI for local development) without credentials, query, or fragment.");
        }

        if (MaxFrameBytes is < 1024 or > 1024 * 1024)
            throw new InvalidOperationException("OpenConnectVoiceBridgeOptions.MaxFrameBytes must be between 1024 and 1048576.");

        if (MaxAudioBytes is < 1024 or > 128L * 1024 * 1024)
            throw new InvalidOperationException("OpenConnectVoiceBridgeOptions.MaxAudioBytes must be between 1024 and 134217728.");

        if (ConnectTimeout <= TimeSpan.Zero || ConnectTimeout > TimeSpan.FromMinutes(1))
            throw new InvalidOperationException("OpenConnectVoiceBridgeOptions.ConnectTimeout must be between 1 ms and 60 seconds.");
    }
}
