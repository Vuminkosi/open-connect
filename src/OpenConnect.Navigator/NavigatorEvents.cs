namespace OpenConnect.Navigator;

/// <summary>Safe metadata event; event payloads must never contain raw DOM, media, or personal data.</summary>
public sealed record NavigatorEvent(
    string EventId,
    string Type,
    string SessionId,
    string? TaskId,
    long Sequence,
    string? CorrelationId,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>Allowed privacy-safe Navigator event names.</summary>
public static class NavigatorEventTypes
{
    private static readonly string[] Prefixes = ["navigator.session.", "tour.step.", "command.", "speech."];

    /// <summary>Checks event naming and rejects metadata keys suggesting raw or personal content.</summary>
    public static bool IsSafe(NavigatorEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value.EventId) || string.IsNullOrWhiteSpace(value.SessionId) ||
            value.Sequence < 0 || !Prefixes.Any(prefix => value.Type.StartsWith(prefix, StringComparison.Ordinal)) && value.Type != "capture.consent")
            return false;
        return value.Metadata is null || value.Metadata.Keys.All(key =>
            !new[] { "dom", "html", "screenshot", "audio", "transcript", "pii", "text" }
                .Any(blocked => key.Contains(blocked, StringComparison.OrdinalIgnoreCase)));
    }
}
