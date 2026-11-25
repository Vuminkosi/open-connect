using System.Collections.Concurrent;
using OpenConnect.Abstractions;

namespace OpenConnect.Privacy;

/// <summary>
/// Thread-safe, in-memory <see cref="ISecurityEvidenceSink"/> with idempotent deduplication by
/// <c>(TenantId, ApplicationId, EventId)</c> and tenant-scoped bounded pagination (FW-100).
/// </summary>
public sealed class InMemorySecurityEvidenceSink : ISecurityEvidenceSink
{
    private const int DefaultMaxStoredEvents = 10_000;
    private readonly ConcurrentDictionary<(string TenantId, string ApplicationId, Guid EventId), SecurityEvidenceEvent> _index = new();
    private readonly List<SecurityEvidenceEvent> _ordered = new();
    private readonly object _gate = new();
    private readonly int _maxStoredEvents;

    /// <summary>Creates a new <see cref="InMemorySecurityEvidenceSink"/> with an optional bounded capacity.</summary>
    public InMemorySecurityEvidenceSink(int maxStoredEvents = DefaultMaxStoredEvents)
    {
        if (maxStoredEvents <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxStoredEvents), "MaxStoredEvents must be positive.");
        _maxStoredEvents = maxStoredEvents;
    }

    /// <inheritdoc />
    public ValueTask<SecurityEvidenceRecordResult> RecordAsync(SecurityEvidenceEvent evidence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();

        var key = (evidence.TenantId, evidence.ApplicationId, evidence.EventId);
        if (!_index.TryAdd(key, evidence))
        {
            return ValueTask.FromResult(new SecurityEvidenceRecordResult(Recorded: true, Deduplicated: true, ReasonCode: "duplicate_event"));
        }

        lock (_gate)
        {
            _ordered.Add(evidence);
            if (_ordered.Count > _maxStoredEvents)
            {
                var oldest = _ordered[0];
                _ordered.RemoveAt(0);
                _index.TryRemove((oldest.TenantId, oldest.ApplicationId, oldest.EventId), out _);
            }
        }

        return ValueTask.FromResult(new SecurityEvidenceRecordResult(Recorded: true, Deduplicated: false, ReasonCode: "recorded"));
    }

    /// <summary>Returns tenant-scoped evidence events in reverse chronological order with bounded pagination.</summary>
    public IReadOnlyList<SecurityEvidenceEvent> Query(
        string tenantId,
        string? applicationId = null,
        string? capability = null,
        int skip = 0,
        int take = 100)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        if (take is <= 0 or > 500) throw new ArgumentOutOfRangeException(nameof(take), "Take must be between 1 and 500.");

        lock (_gate)
        {
            IEnumerable<SecurityEvidenceEvent> query = _ordered
                .Where(e => string.Equals(e.TenantId, tenantId, StringComparison.Ordinal));

            if (!string.IsNullOrWhiteSpace(applicationId))
                query = query.Where(e => string.Equals(e.ApplicationId, applicationId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(capability))
                query = query.Where(e => string.Equals(e.Capability, capability, StringComparison.Ordinal));

            return query
                .OrderByDescending(e => e.OccurredAt)
                .Skip(skip)
                .Take(take)
                .ToArray();
        }
    }

    /// <summary>Gets a single tenant-scoped evidence event by ID.</summary>
    public SecurityEvidenceEvent? GetById(string tenantId, string applicationId, Guid eventId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(applicationId) || eventId == Guid.Empty)
            return null;
        return _index.TryGetValue((tenantId, applicationId, eventId), out var ev) ? ev : null;
    }
}
