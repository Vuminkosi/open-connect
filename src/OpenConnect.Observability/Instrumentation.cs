using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OpenConnect.Observability;

/// <summary>Privacy-safe diagnostic sources. Only dimensions in <see cref="AllowedDimensions"/> may be emitted.</summary>
public static class Instrumentation
{
    public const string MetadataOnly = "MetadataOnly";
    // Keep the mutable diagnostics primitives private: callers must cross the sanitizer boundary.
    private static readonly IReadOnlyDictionary<string, ActivitySource> Sources =
        new Dictionary<string, ActivitySource>(StringComparer.Ordinal)
        {
            ["gateway"] = new("OpenConnect.Gateway"), ["firewall"] = new("OpenConnect.Firewall"),
            ["genai"] = new("OpenConnect.GenAI"), ["agents"] = new("OpenConnect.Agents"),
            ["workers"] = new("OpenConnect.Workers"), ["vault"] = new("OpenConnect.Vault"),
            ["metering"] = new("OpenConnect.Metering"), ["vectors"] = new("OpenConnect.Vectors")
        };
    private static readonly Meter Metrics = new("OpenConnect");
    private static readonly Meter VectorMetrics = new("OpenConnect.Vectors");
    private static readonly Histogram<double> VectorOperationDuration =
        VectorMetrics.CreateHistogram<double>("openconnect.vector.operation.duration", "ms");
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedDimensions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["operation"] = new HashSet<string>(new[] { "chat", "completion", "embedding", "tool", "health", "request", "index", "search", "delete" }, StringComparer.Ordinal),
            ["provider"] = new HashSet<string>(new[] { "openai", "azure_openai", "anthropic", "google", "local", "unknown" }, StringComparer.Ordinal),
            ["model_family"] = new HashSet<string>(new[] { "gpt", "claude", "gemini", "llama", "other" }, StringComparer.Ordinal),
            ["outcome"] = new HashSet<string>(new[] { "success", "failure", "blocked", "cancelled" }, StringComparer.Ordinal),
            ["mode"] = new HashSet<string>(new[] { "strict-local", "local-only", "policy-controlled", "disabled" }, StringComparer.Ordinal),
            ["component"] = new HashSet<string>(new[] { "gateway", "firewall", "agent", "worker", "vault", "metering", "qdrantvactor" }, StringComparer.Ordinal)
        };

    /// <summary>Filters keys against the bounded dimension allowlist; arbitrary values are not accepted as dimensions.</summary>
    public static KeyValuePair<string, object?>[] AllowlistedTags(IEnumerable<KeyValuePair<string, object?>> dimensions)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        return TelemetryPrivacy.CreateDefault().Filter.Filter(dimensions);
    }

    /// <summary>Starts an activity with tags filtered and checked by the default export policy.</summary>
    public static Activity? SafeStartActivity(string source, string operation,
        IEnumerable<KeyValuePair<string, object?>>? tags = null, ActivityKind kind = ActivityKind.Internal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (!Sources.TryGetValue(source, out var activitySource))
            throw new ArgumentOutOfRangeException(nameof(source));
        var safeTags = AllowlistedTags(tags ?? []);
        var activity = activitySource.StartActivity(operation, kind);
        if (activity is null) return null;
        foreach (var tag in safeTags) activity.SetTag(tag.Key, tag.Value);
        return activity;
    }

    /// <summary>Records a vector operation duration with bounded, privacy-filtered dimensions only.</summary>
    public static void SafeRecordVector(double durationMilliseconds, string operation, string outcome)
    {
        if (!double.IsFinite(durationMilliseconds) || durationMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        var tags = AllowlistedTags([
            new("operation", operation),
            new("component", "qdrantvactor"),
            new("outcome", outcome)
        ]);
        if (tags.Length != 3 || !TelemetryPrivacy.CreateDefault().Validator.IsSafe(tags))
            throw new ArgumentOutOfRangeException(nameof(operation), "Vector telemetry dimensions must be allowlisted values.");

        VectorOperationDuration.Record(durationMilliseconds, tags);
    }

    internal static bool IsApprovedDimension(string key, string value) =>
        AllowedDimensions.TryGetValue(key, out var allowed) && allowed.Contains(value);
}
