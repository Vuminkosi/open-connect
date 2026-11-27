using System.Collections.Concurrent;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.IMessaging;

/// <summary>
/// Consumes inbound messaging events from the private worker or durable NATS stream with
/// bounded event-ID deduplication so duplicate deliveries never trigger duplicate side effects.
/// </summary>
public sealed class WorkerEventConsumer : IMessagingEventDispatcher
{
    public const int DefaultMaxDedupEntries = 10_000;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _processedEvents = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();
    private readonly Func<MessagingEventEnvelope, CancellationToken, ValueTask>? _handler;
    private readonly int _maxDedupEntries;
    private readonly TimeProvider _timeProvider;

    public WorkerEventConsumer(
        Func<MessagingEventEnvelope, CancellationToken, ValueTask>? handler = null,
        int maxDedupEntries = DefaultMaxDedupEntries,
        TimeProvider? timeProvider = null)
    {
        if (maxDedupEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDedupEntries));
        _handler = handler;
        _maxDedupEntries = maxDedupEntries;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Validates and dispatches an event envelope at most once per <c>(tenantId, eventId)</c>.
    /// Returns <see langword="true"/> if newly processed, <see langword="false"/> if deduplicated.
    /// </summary>
    public async ValueTask<bool> DispatchAsync(
        MessagingEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.SchemaVersion != 1)
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Unsupported messaging event schemaVersion.", 400);
        if (envelope.TenantId.Value == default || string.IsNullOrWhiteSpace(envelope.EventId) ||
            string.IsNullOrWhiteSpace(envelope.Type) || string.IsNullOrWhiteSpace(envelope.Cursor))
        {
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Messaging event envelope is missing required fields.", 400);
        }

        var dedupKey = $"{envelope.TenantId.Value}:{envelope.EventId}";
        if (!_processedEvents.TryAdd(dedupKey, _timeProvider.GetUtcNow()))
            return false;

        _insertionOrder.Enqueue(dedupKey);
        TrimIfNeeded();

        if (_handler is not null)
            await _handler(envelope, cancellationToken).ConfigureAwait(false);

        return true;
    }

    private void TrimIfNeeded()
    {
        while (_processedEvents.Count > _maxDedupEntries && _insertionOrder.TryDequeue(out var oldest))
            _processedEvents.TryRemove(oldest, out _);
    }
}
