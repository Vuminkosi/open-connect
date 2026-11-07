using System.Diagnostics;

namespace OpenConnect.Observability;

/// <summary>Sanitization helper to call at the final exporter boundary.</summary>
public static class SafeExporter
{
    public static bool ValidateAndSanitizeActivity(Activity activity, ITelemetryPrivacyFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(activity);
        filter ??= TelemetryPrivacy.CreateDefault().Filter;

        var original = activity.TagObjects
            .Select(tag => new KeyValuePair<string, object?>(tag.Key, tag.Value))
            .ToArray();
        var safe = filter.Filter(original);
        var safeNames = safe.Select(tag => tag.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var tag in original)
        {
            if (!safeNames.Contains(tag.Key))
                activity.SetTag(tag.Key, null);
        }

        return safe.Length == original.Length;
    }
}
