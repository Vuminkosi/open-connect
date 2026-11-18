namespace OpenConnect.Samples.Workers.Contracts;

public static class SampleTiersAndProviders
{
    private static readonly Dictionary<string, string> CanonicalTierMap = new(StringComparer.Ordinal)
    {
        ["static"] = "static",
        ["t0"] = "static",
        ["contract"] = "contract",
        ["t1"] = "contract",
        ["image-startup"] = "image-startup",
        ["t2"] = "image-startup",
        ["integration"] = "integration",
        ["t3"] = "integration",
        ["model-live"] = "model-live",
        ["t4"] = "model-live",
        ["offline-revision"] = "offline-revision",
        ["t5"] = "offline-revision",
        ["release"] = "release",
        ["t6"] = "release"
    };

    private static readonly HashSet<string> ValidProviders = new(StringComparer.Ordinal)
    {
        "sqlite",
        "postgres",
        "sqlserver",
        "local"
    };

    private static readonly HashSet<string> DestructiveFlags = new(StringComparer.Ordinal)
    {
        "-v",
        "--volumes",
        "--rmi",
        "--force-rm",
        "--prune"
    };

    public static bool TryNormalizeTier(string? tier, out string normalized)
    {
        if (!string.IsNullOrWhiteSpace(tier) && CanonicalTierMap.TryGetValue(tier, out var mapped))
        {
            normalized = mapped;
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    public static bool IsValidProvider(string? provider) =>
        !string.IsNullOrWhiteSpace(provider) && ValidProviders.Contains(provider);

    public static bool IsDestructiveFlag(string? arg) =>
        !string.IsNullOrWhiteSpace(arg) && DestructiveFlags.Contains(arg);

    public static string ComposeProjectNameFor(string workerOrSample)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerOrSample);
        if (!CanonicalWorkers.IsValidWorkerOrSample(workerOrSample))
        {
            throw new ArgumentException($"Unknown worker or sample '{workerOrSample}'.", nameof(workerOrSample));
        }

        return $"ocw-e2e-{workerOrSample}";
    }
}
