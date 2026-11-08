using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OpenConnect.Navigator.Diagnostics;

/// <summary>Privacy-conscious diagnostics primitives for Navigator. No exporter is configured here.</summary>
public static class NavigatorActivitySource
{
    public const string Name = "OpenConnect.Navigator";
    public static ActivitySource Activities { get; } = new(Name);
    public static Meter Meter { get; } = new(Name);

    private static readonly HashSet<string> AllowedTags = new(StringComparer.Ordinal)
    {
        "eventId", "sessionId", "taskId", "stepKind", "actionType", "outcome",
        "modelId", "modelRevision", "voiceId", "language", "durationMs", "count"
    };

    public static Activity? StartActivity(string operation, IEnumerable<KeyValuePair<string, object?>>? tags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var safe = SafeTags(tags);
        var activity = Activities.StartActivity(operation);
        if (activity is null) return null;
        foreach (var tag in safe) activity.SetTag(tag.Key, tag.Value);
        return activity;
    }

    /// <summary>Returns only approved, bounded metadata; content-like and secret-bearing values are discarded.</summary>
    public static KeyValuePair<string, object?>[] SafeTags(IEnumerable<KeyValuePair<string, object?>>? tags)
    {
        if (tags is null) return [];
        return tags.Where(tag => AllowedTags.Contains(tag.Key))
            .Select(tag => SafeTag(tag))
            .Where(tag => tag.HasValue)
            .Select(tag => tag!.Value)
            .ToArray();
    }

    /// <summary>Returns only bounded, low-cardinality dimensions suitable for metric labels.</summary>
    public static KeyValuePair<string, object?>[] SafeMetricTags(IEnumerable<KeyValuePair<string, object?>>? tags)
    {
        if (tags is null) return [];
        var metricKeys = new HashSet<string>(["outcome", "kind", "provider"], StringComparer.Ordinal);
        return tags.Where(tag => metricKeys.Contains(tag.Key))
            .Select(SafeTag)
            .Where(tag => tag.HasValue)
            .Select(tag => tag!.Value)
            .ToArray();
    }

    private static KeyValuePair<string, object?>? SafeTag(KeyValuePair<string, object?> tag)
    {
        if (tag.Key is "durationMs" or "count")
        {
            if (tag.Value is long integer && integer >= 0 && integer <= 1_000_000_000)
                return new(tag.Key, integer);
            if (tag.Value is double number && double.IsFinite(number) && number >= 0 && number <= 1_000_000_000)
                return new(tag.Key, number);
            return null;
        }

        if (tag.Value is string value && value.Length is > 0 and <= 128 &&
            !RedactionCheck.ContainsSensitiveContent(value))
            return new(tag.Key, value);

        return null;
    }

    public static Counter<long> Sessions { get; } = Meter.CreateCounter<long>("navigator.sessions");
    public static Counter<long> Steps { get; } = Meter.CreateCounter<long>("navigator.steps");
    public static Counter<long> Denials { get; } = Meter.CreateCounter<long>("navigator.denials");
    public static Counter<long> ApprovalWaits { get; } = Meter.CreateCounter<long>("navigator.approval.waits");
    public static Counter<long> Reconnects { get; } = Meter.CreateCounter<long>("navigator.reconnects");
    public static Counter<long> SpeechFallbacks { get; } = Meter.CreateCounter<long>("navigator.speech.fallbacks");
}
