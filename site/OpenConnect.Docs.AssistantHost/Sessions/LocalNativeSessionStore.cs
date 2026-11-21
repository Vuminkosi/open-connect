using OpenConnect.Abstractions.Persistence;

namespace OpenConnect.Docs.AssistantHost.Sessions;

// Development-only, process-local encrypted state. Capacity is not reclaimed until restart.
internal sealed class LocalNativeSessionStore : INativeAgentSessionStore
{
    private const int Capacity = 2048;
    private readonly Dictionary<NativeAgentSessionBinding, NativeAgentSessionRecord> _records = [];

    public Task<NativeAgentSessionRecord?> GetAsync(NativeAgentSessionBinding binding, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_records) return Task.FromResult(_records.GetValueOrDefault(binding));
    }

    public Task<NativeAgentSessionSaveOutcome> SaveAsync(NativeAgentSessionRecord record, long expectedVersion, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_records)
        {
            var existing = _records.GetValueOrDefault(record.Binding);
            if (expectedVersion < 0 || expectedVersion == long.MaxValue || record.Version != expectedVersion + 1 ||
                (existing?.Version ?? 0) != expectedVersion || (existing is null && _records.Count >= Capacity))
                return Task.FromResult(NativeAgentSessionSaveOutcome.Conflict);
            _records[record.Binding] = record;
            return Task.FromResult(NativeAgentSessionSaveOutcome.Saved);
        }
    }
}
