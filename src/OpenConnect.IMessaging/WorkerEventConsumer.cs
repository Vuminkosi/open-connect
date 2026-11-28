using System.Collections.Concurrent;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.IMessaging;

/// <summary>
/// Consumes inbound messaging events from the private worker or durable NATS stream with
/// bounded, process-local <c>(tenantId, eventId)</c> deduplication.
/// </summary>
/// <remarks>
/// An event is recorded as processed only after the handler completes successfully. If the handler
/// throws or is cancelled, the event stays redeliverable and the exception propagates to the caller
/// so the transport can leave the message unacknowledged. Concurrent duplicates of an in-flight event
/// wait for the first attempt: they return <see langword="false"/> if it succeeds and take over as the
/// new owner if it fails.
/// Deduplication state lives in memory only. It is bounded to <c>maxDedupEntries</c> completed events
/// (the oldest completed entry is evicted first), and it is not shared across process restarts or
/// multiple replicas. Handlers must therefore be idempotent.
/// </remarks>
public sealed class WorkerEventConsumer : IMessagingEventDispatcher
{
    public const int DefaultMaxDedupEntries = 10_000;

    private sealed class Entry
    {
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset CompletedAt { get; set; }
    }

    private readonly ConcurrentDictionary<string, Entry> _events = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();
    private readonly Func<MessagingEventEnvelope, CancellationToken, ValueTask>? _handler;
    private readonly int _maxDedupEntries;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a consumer.</summary>
    /// <param name="handler">
    /// Handler invoked once per successfully processed event. When <see langword="null"/> the consumer is a
    /// no-op that still records the event and reports <see langword="true"/>.
    /// </param>
    /// <param name="maxDedupEntries">Maximum number of completed events remembered for deduplication.</param>
    /// <param name="timeProvider">Clock used to stamp completed entries.</param>
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
    /// Validates an event envelope and runs the handler once per successfully processed <c>(tenantId, eventId)</c>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the handler completed during this call; <see langword="false"/> if the event
    /// was already completed earlier (or by a concurrent attempt this call waited on).
    /// </returns>
    /// <exception cref="MessagingDomainException">The envelope is invalid.</exception>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled. Handler exceptions and cancellation propagate and leave the event redeliverable.
    /// </exception>
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
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = new Entry();
            if (_events.TryAdd(dedupKey, entry))
                return await RunAsync(dedupKey, entry, envelope, cancellationToken).ConfigureAwait(false);

            if (!_events.TryGetValue(dedupKey, out var existing))
                continue;

            // Wait for the in-flight (or completed) attempt. A failed attempt releases the key, so retry as owner.
            if (await existing.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false))
                return false;
        }
    }

    private async ValueTask<bool> RunAsync(
        string dedupKey,
        Entry entry,
        MessagingEventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var succeeded = false;
        try
        {
            if (_handler is not null)
                await _handler(envelope, cancellationToken).ConfigureAwait(false);

            entry.CompletedAt = _timeProvider.GetUtcNow();
            _insertionOrder.Enqueue(dedupKey);
            succeeded = true;
            TrimIfNeeded();
            return true;
        }
        finally
        {
            if (!succeeded)
                _events.TryRemove(new KeyValuePair<string, Entry>(dedupKey, entry));
            entry.Completion.TrySetResult(succeeded);
        }
    }

    private void TrimIfNeeded()
    {
        // Only completed entries are queued, so in-flight entries are never evicted.
        while (_insertionOrder.Count > _maxDedupEntries && _insertionOrder.TryDequeue(out var oldest))
            _events.TryRemove(oldest, out _);
    }
}
