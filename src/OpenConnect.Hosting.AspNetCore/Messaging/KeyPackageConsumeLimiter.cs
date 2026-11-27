namespace OpenConnect.Hosting.AspNetCore.Messaging;

/// <summary>
/// Bounded fixed-window counter used to limit key package consumption per caller and per target.
/// Memory is capped; when the cap is reached with no expired windows to evict, new keys are rejected (fail closed).
/// </summary>
internal sealed class KeyPackageConsumeLimiter(TimeProvider timeProvider)
{
    private const int MaxTrackedKeys = 10_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset WindowStart, int Count)> _windows = new(StringComparer.Ordinal);

    /// <summary>Attempts to record one consumption for <paramref name="key"/>. Non-positive limits or windows always deny.</summary>
    public bool TryAcquire(string key, int limit, TimeSpan window, out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        retryAfter = window > TimeSpan.Zero ? window : TimeSpan.FromMinutes(1);
        if (limit <= 0 || window <= TimeSpan.Zero)
            return false;

        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_windows.TryGetValue(key, out var entry) && now - entry.WindowStart < window)
            {
                retryAfter = entry.WindowStart + window - now;
                if (entry.Count >= limit)
                    return false;
                _windows[key] = (entry.WindowStart, entry.Count + 1);
                return true;
            }

            if (!_windows.ContainsKey(key) && _windows.Count >= MaxTrackedKeys)
            {
                var expired = _windows.Where(kv => now - kv.Value.WindowStart >= window).Select(kv => kv.Key).ToArray();
                foreach (var k in expired)
                    _windows.Remove(k);
                if (_windows.Count >= MaxTrackedKeys)
                    return false;
            }

            _windows[key] = (now, 1);
            return true;
        }
    }
}
