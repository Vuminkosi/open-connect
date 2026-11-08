using OpenConnect.Navigator;
using OpenConnect.Navigator.Events;

namespace OpenConnect.Navigator.AspNetCore.Outbox;

/// <summary>Validates, deduplicates, and forwards committed events to the host-owned outbox seam.</summary>
public sealed class NavigatorCommitHook(NavigatorOutboxWriter writer) : INavigatorCommitHook
{
    private readonly CommittedEventProjector _projector = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _quarantinedEventIds = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _dedupSkippedEventIds = new();

    /// <summary>Committed event IDs rejected from delivery and retained for explicit recovery.</summary>
    public IReadOnlyCollection<string> QuarantinedEventIds => _quarantinedEventIds.ToArray();

    /// <summary>Committed event IDs skipped because an earlier event in this batch had the same ID.</summary>
    public IReadOnlyCollection<string> DedupSkippedEventIds => _dedupSkippedEventIds.ToArray();

    /// <summary>Attempts to remove one process-local quarantined event ID. This queue is not durable; durable recovery requires a persistent quarantine store.</summary>
    public bool TryDequeueQuarantined(out string? eventId) => _quarantinedEventIds.TryDequeue(out eventId);

    public async Task AfterCommitAsync(string sessionId, IReadOnlyList<NavigatorEvent> committedEvents, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(committedEvents);
        var seenEventIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in committedEvents.OrderBy(item => item.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seenEventIds.Add(value.EventId))
            {
                _dedupSkippedEventIds.Enqueue(value.EventId);
                continue;
            }

            try
            {
                if (!StringComparer.Ordinal.Equals(value.SessionId, sessionId))
                    throw new InvalidOperationException("Committed event does not belong to the committed session.");

                var safe = await _projector.ProjectAsync(value, cancellationToken).ConfigureAwait(false);
                if (safe is not null)
                    await writer.WriteAsync(safe, cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                _quarantinedEventIds.Enqueue(value.EventId);
            }
            catch (InvalidOperationException)
            {
                _quarantinedEventIds.Enqueue(value.EventId);
            }
        }
    }
}
