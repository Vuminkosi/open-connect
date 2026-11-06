using System.Collections.Concurrent;
using OpenConnect.Abstractions;

namespace OpenConnect.Hosting.AspNetCore;

/// <summary>A process-local, bounded event journal for session replay.</summary>
public sealed class InMemoryEventJournal : IEventJournal
{
    private const int EventLimit = 1000;
    private readonly ConcurrentDictionary<Ulid, SessionEvents> _sessions = new();

    public Task<long> AppendAsync(EventRecord @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        cancellationToken.ThrowIfCancellationRequested();
        var events = _sessions.GetOrAdd(@event.SessionId.Value, static _ => new SessionEvents());
        return Task.FromResult(events.Append(@event, EventLimit));
    }

    public async IAsyncEnumerable<EventRecord> ReplayAsync(
        SessionId sessionId,
        long afterSequence = 0,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        var events = GetEvents(sessionId, afterSequence);
        foreach (var item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.CompletedTask;
        }
    }

    internal IReadOnlyList<EventRecord> GetEvents(SessionId sessionId, long afterSequence)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        return _sessions.TryGetValue(sessionId.Value, out var events)
            ? events.GetAfter(afterSequence)
            : Array.Empty<EventRecord>();
    }

    private sealed class SessionEvents
    {
        private readonly object _gate = new();
        private readonly Queue<EventRecord> _events = new();
        private long _sequence;

        public long Append(EventRecord item, int limit)
        {
            lock (_gate)
            {
                var sequence = checked(++_sequence);
                _events.Enqueue(item with { Sequence = sequence });
                while (_events.Count > limit) _events.Dequeue();
                return sequence;
            }
        }

        public IReadOnlyList<EventRecord> GetAfter(long sequence)
        {
            lock (_gate) return _events.Where(item => item.Sequence > sequence).ToArray();
        }
    }
}
