namespace OpenConnect.Navigator;

/// <summary>Resolves optional speech providers and rejects events from interrupted speech epochs.</summary>
public sealed class SpeechRouter
{
    private readonly NavigatorSpeechOptions _options;
    private long _speechEpoch;

    public SpeechRouter(NavigatorSpeechOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public long CurrentEpoch => Interlocked.Read(ref _speechEpoch);

    /// <summary>Chooses only the explicitly preferred healthy provider; otherwise callers remain text-first.</summary>
    public SpeechProviderResolution ResolveProvider(IEnumerable<SpeechCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!_options.Enabled || _options.PreferredProvider == NavigatorSpeechProvider.Disabled)
            return new(null, SpeechUnavailableReason.Disabled);

        var capability = capabilities.FirstOrDefault(x =>
            x.Provider == _options.PreferredProvider);
        if (capability is null || !capability.Available)
            return new(null, SpeechUnavailableReason.ProviderNotConfigured);
        if (!capability.Healthy)
            return new(null, SpeechUnavailableReason.ProviderUnhealthy);
        return new(capability.ProviderId, null);
    }

    public long Interrupt() => Interlocked.Increment(ref _speechEpoch);

    /// <summary>Starts a new epoch for a request. Request content is reference-only and never emitted.</summary>
    public SpeechSynthesisRequest CreateRequest(string requestId, string textReference, string? locale = null,
        string? voiceId = null, string format = "audio/wav", DateTimeOffset? deadline = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(textReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        if (!_options.Enabled) throw new InvalidOperationException("Speech is disabled.");
        return new(requestId, textReference, locale ?? _options.DefaultLanguage, voiceId ?? _options.DefaultVoice,
            format, CurrentEpoch, deadline ?? DateTimeOffset.UtcNow.AddMinutes(1));
    }

    /// <summary>Returns false for stale events after interruption, preventing late audio from being presented.</summary>
    public bool IsCurrent(SpeechSynthesisEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.SpeechEpoch == CurrentEpoch;
    }
}
