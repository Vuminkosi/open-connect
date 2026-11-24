namespace OpenConnect.Core;

/// <summary>Non-authoritative adaptive endpointing recommendation.</summary>
public enum AdaptiveEndpointDecision
{
    ContinueListening = 0,
    EndpointReady = 1,
    BackchannelIgnore = 2
}

/// <summary>Inputs for evaluating whether a user utterance has reached a natural endpoint.</summary>
public sealed record AdaptiveEndpointInput(
    int SilenceDurationMs,
    string PartialTranscript,
    long TranscriptRevision,
    int StableDurationMs,
    bool IsAssistantSpeaking,
    bool SpeechDetected);

/// <summary>Non-authoritative recommendation emitted by <see cref="AdaptiveEndpointEvaluator"/>.</summary>
public sealed record AdaptiveEndpointResult(
    AdaptiveEndpointDecision Decision,
    double Confidence,
    int RecommendedSilenceMs,
    string Reason,
    bool Authoritative = false);

/// <summary>Pure .NET adaptive endpointing and backchannel heuristic combining silence, stability, punctuation, and hesitation tokens.</summary>
public sealed class AdaptiveEndpointEvaluator
{
    private static readonly HashSet<string> BackchannelTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "mhm", "mm-hmm", "mmhmm", "uh-huh", "uh huh", "yeah", "yep", "yes", "ok", "okay", "right", "sure", "got it", "hmm"
    };

    private static readonly HashSet<string> HesitationTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "um", "uh", "umm", "uhh", "er", "erm", "hmm", "like", "so", "and", "or", "but", "because", "the", "a", "an", "to", "of", "with"
    };

    private readonly bool _conservativeMode;

    public AdaptiveEndpointEvaluator(bool conservativeMode = false)
    {
        _conservativeMode = conservativeMode;
    }

    /// <summary>Evaluates acoustic silence and partial transcript cues to produce a non-authoritative endpointing recommendation.</summary>
    public AdaptiveEndpointResult Evaluate(AdaptiveEndpointInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var silenceMs = Math.Max(0, input.SilenceDurationMs);
        var stableMs = Math.Max(0, input.StableDurationMs);
        var trimmed = (input.PartialTranscript ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            var defaultWait = _conservativeMode ? 1100 : 750;
            return new AdaptiveEndpointResult(
                AdaptiveEndpointDecision.ContinueListening,
                0.2,
                defaultWait,
                "empty_transcript",
                Authoritative: false);
        }

        var normalized = NormalizeForTokenMatch(trimmed);
        if (input.IsAssistantSpeaking && BackchannelTokens.Contains(normalized))
        {
            return new AdaptiveEndpointResult(
                AdaptiveEndpointDecision.BackchannelIgnore,
                0.9,
                _conservativeMode ? 1100 : 750,
                "backchannel_during_playback",
                Authoritative: false);
        }

        if (EndsWithHesitation(trimmed))
        {
            var hesitationThreshold = _conservativeMode ? 1900 : 1400;
            if (silenceMs >= hesitationThreshold)
            {
                return new AdaptiveEndpointResult(
                    AdaptiveEndpointDecision.EndpointReady,
                    0.65,
                    hesitationThreshold,
                    "hesitation_timeout_elapsed",
                    Authoritative: false);
            }

            return new AdaptiveEndpointResult(
                AdaptiveEndpointDecision.ContinueListening,
                0.85,
                hesitationThreshold,
                "hesitation_detected",
                Authoritative: false);
        }

        var hasTerminalPunctuation = trimmed.EndsWith('.') || trimmed.EndsWith('?') || trimmed.EndsWith('!');
        var baseThreshold = hasTerminalPunctuation
            ? (_conservativeMode ? 750 : 450)
            : (_conservativeMode ? 1100 : 750);

        if (stableMs >= 250 && hasTerminalPunctuation && silenceMs >= baseThreshold && !input.SpeechDetected)
        {
            return new AdaptiveEndpointResult(
                AdaptiveEndpointDecision.EndpointReady,
                0.92,
                baseThreshold,
                "terminal_punctuation_and_silence",
                Authoritative: false);
        }

        if (silenceMs >= baseThreshold && !input.SpeechDetected)
        {
            return new AdaptiveEndpointResult(
                AdaptiveEndpointDecision.EndpointReady,
                hasTerminalPunctuation ? 0.85 : 0.78,
                baseThreshold,
                "silence_threshold_elapsed",
                Authoritative: false);
        }

        return new AdaptiveEndpointResult(
            AdaptiveEndpointDecision.ContinueListening,
            0.7,
            baseThreshold,
            "awaiting_silence_threshold",
            Authoritative: false);
    }

    private static bool EndsWithHesitation(string trimmed)
    {
        if (trimmed.EndsWith("...", StringComparison.Ordinal) || trimmed.EndsWith('…') || trimmed.EndsWith(',') || trimmed.EndsWith('-'))
        {
            return true;
        }

        var lastSpace = trimmed.LastIndexOf(' ');
        var lastToken = (lastSpace >= 0 ? trimmed[(lastSpace + 1)..] : trimmed)
            .TrimEnd('.', ',', '!', '?', ';', ':', '…', '-');
        return HesitationTokens.Contains(lastToken);
    }

    private static string NormalizeForTokenMatch(string text)
    {
        var cleaned = text.Trim().TrimEnd('.', ',', '!', '?');
        return string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
