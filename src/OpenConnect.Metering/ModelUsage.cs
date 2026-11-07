using OpenConnect.Abstractions;

namespace OpenConnect.Metering;

public enum UsageUnit { Tokens, Requests }
public enum CostState { Known, Unknown }
public enum UsageProvenance { ProviderReported, Estimated, Unavailable }

/// <summary>Nonnegative quantity; null denotes unavailable rather than zero.</summary>
public sealed record UsageQuantity(decimal? Input, decimal? Output, decimal? CacheRead = null, decimal? CacheWrite = null)
{
    public UsageQuantity Validate()
    {
        if (new[] { Input, Output, CacheRead, CacheWrite }.Any(x => x < 0))
            throw new ArgumentOutOfRangeException(nameof(UsageQuantity), "Usage quantities cannot be negative.");
        return this;
    }
}

/// <summary>Normalized usage where cache tokens are a subset of input and never added a second time.</summary>
public sealed record NormalizedUsage(UsageQuantity Quantity)
{
    public NormalizedUsage Validate()
    {
        Quantity.Validate();
        if (Quantity.Input is decimal input && Quantity.CacheRead is decimal read && read > input)
            throw new ArgumentException("Cache-read usage cannot exceed input usage.");
        if (Quantity.Input is decimal total && Quantity.CacheWrite is decimal write && write > total)
            throw new ArgumentException("Cache-write usage cannot exceed input usage.");
        return this;
    }
}

public sealed record ModelUsageEvent(Ulid EventId, Ulid AttemptId, string Provider, string Model,
    NormalizedUsage Usage, decimal? Cost, CostState CostState, UsageProvenance Provenance)
{
    public ModelUsageEvent Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);
        Usage.Validate();
        if (Cost < 0) throw new ArgumentOutOfRangeException(nameof(Cost));
        if ((CostState == CostState.Unknown) != (Cost is null))
            throw new ArgumentException("Unknown costs must remain null; known costs must have a value.");
        return this;
    }
}

public interface IModelUsageMeter { ModelUsageEvent Normalize(ModelUsageEvent usage); }
public interface IUsageLedger
{
    /// <summary>Atomically inserts by EventId. Repeated identical events are a no-op; conflicting duplicates fail.</summary>
    ValueTask<bool> AppendAsync(ModelUsageEvent usage, CancellationToken cancellationToken = default);
}

/// <summary>Process-local ledger for deterministic tests and development; production durability is supplied by a persistence adapter.</summary>
public sealed class InMemoryUsageLedger : IUsageLedger
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Ulid, ModelUsageEvent> _events = new();

    public ValueTask<bool> AppendAsync(ModelUsageEvent usage, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        usage.Validate();
        if (_events.TryAdd(usage.EventId, usage)) return ValueTask.FromResult(true);
        if (_events.TryGetValue(usage.EventId, out var existing) && existing == usage)
            return ValueTask.FromResult(false);
        throw new InvalidOperationException("An event with this identifier already exists with different content.");
    }
}
public interface IPriceCatalog
{
    ModelPrice? Find(string provider, string model);
}
public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion, decimal CacheReadPerMillion = 0,
    decimal CacheWritePerMillion = 0)
{
    public decimal Calculate(NormalizedUsage usage)
    {
        usage.Validate();
        var q = usage.Quantity;
        if (q.Input is null || q.Output is null) throw new InvalidOperationException("Cannot price incomplete usage.");
        if (new[] { InputPerMillion, OutputPerMillion, CacheReadPerMillion, CacheWritePerMillion }.Any(x => x < 0))
            throw new InvalidOperationException("Prices cannot be negative.");
        var regularInput = q.Input.Value - (q.CacheRead ?? 0) - (q.CacheWrite ?? 0);
        if (regularInput < 0) throw new ArgumentException("Cache usage cannot exceed input usage.");
        return (regularInput * InputPerMillion + q.Output.Value * OutputPerMillion +
                (q.CacheRead ?? 0) * CacheReadPerMillion + (q.CacheWrite ?? 0) * CacheWritePerMillion) / 1_000_000m;
    }
}
