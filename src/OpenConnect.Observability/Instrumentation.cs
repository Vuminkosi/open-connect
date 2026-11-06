using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OpenConnect.Observability;

/// <summary>Privacy-safe diagnostic sources. Only dimensions in <see cref="AllowedDimensions"/> may be emitted.</summary>
public static class Instrumentation
{
    public const string MetadataOnly = "MetadataOnly";
    public static readonly ActivitySource Gateway = new("OpenConnect.Gateway");
    public static readonly ActivitySource Firewall = new("OpenConnect.Firewall");
    public static readonly ActivitySource GenAI = new("OpenConnect.GenAI");
    public static readonly ActivitySource Agents = new("OpenConnect.Agents");
    public static readonly ActivitySource Workers = new("OpenConnect.Workers");
    public static readonly ActivitySource Vault = new("OpenConnect.Vault");
    public static readonly ActivitySource Metering = new("OpenConnect.Metering");
    public static readonly Meter Metrics = new("OpenConnect");
    private static readonly HashSet<string> AllowedDimensions = new(StringComparer.Ordinal)
    { "operation", "provider", "model_family", "outcome", "mode", "component" };

    /// <summary>Filters keys against the bounded dimension allowlist; arbitrary values are not accepted as dimensions.</summary>
    public static KeyValuePair<string, object?>[] AllowlistedTags(IEnumerable<KeyValuePair<string, object?>> dimensions)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        return dimensions.Where(pair => AllowedDimensions.Contains(pair.Key) &&
            pair.Value is string value && value.Length <= 64 && !LooksSensitive(value))
            .ToArray();
    }

    private static bool LooksSensitive(string value) => value.Contains('@') ||
        value.Contains("bearer ", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("sk-", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("secret", StringComparison.OrdinalIgnoreCase);
}
