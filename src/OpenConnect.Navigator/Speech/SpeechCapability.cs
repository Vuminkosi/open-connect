namespace OpenConnect.Navigator;

/// <summary>Describes a speech provider advertised by the host and its current health.</summary>
public sealed record SpeechCapability(string ProviderId, NavigatorSpeechProvider Provider, bool Available, bool Healthy);

/// <summary>Provider selection result. Unavailable results intentionally select text-only output.</summary>
public sealed record SpeechProviderResolution(string? ProviderId, SpeechUnavailableReason? UnavailableReason)
{
    public bool IsAvailable => ProviderId is not null;
}

public enum SpeechUnavailableReason
{
    Disabled,
    ProviderNotConfigured,
    ProviderUnhealthy
}
