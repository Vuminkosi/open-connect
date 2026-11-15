using System.Collections.Concurrent;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Serialization;

namespace OpenConnect.Hosting.AspNetCore;

/// <summary>A process-local, bounded event journal for session replay.</summary>
public sealed class InMemoryEventJournal : IEventJournal
{
    private const int EventLimit = 1000;
    private static readonly JsonSerializerOptions ClientInteractionJsonOptions = OpenConnectHttpJson.CreateOptions();
    private readonly ConcurrentDictionary<Ulid, SessionEvents> _sessions = new();

    internal long Append(EventRecord @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var events = _sessions.GetOrAdd(@event.SessionId.Value, static _ => new SessionEvents());
        return events.Append(@event, EventLimit);
    }

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

    internal ClientInteractionResponseAppendResult TryAppendClientInteractionResponse(
        SessionId sessionId,
        ClientInteractionResponse response,
        string eventData)
    {
        if (!_sessions.TryGetValue(sessionId.Value, out var events))
            return ClientInteractionResponseAppendResult.RequestNotFound;

        return events.TryAppendClientInteractionResponse(
            response,
            new EventRecord(sessionId, 0, ClientInteractionEventTypes.Response, eventData),
            EventLimit);
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

        public ClientInteractionResponseAppendResult TryAppendClientInteractionResponse(
            ClientInteractionResponse response,
            EventRecord responseEvent,
            int limit)
        {
            lock (_gate)
            {
                foreach (var item in _events)
                {
                    if (!item.Type.Equals(ClientInteractionEventTypes.Response, StringComparison.Ordinal)) continue;
                    try
                    {
                        var prior = JsonSerializer.Deserialize<ClientInteractionResponse>(item.Data, ClientInteractionJsonOptions);
                        if (prior?.CorrelationId != response.CorrelationId) continue;
                        return prior == response
                            ? ClientInteractionResponseAppendResult.Duplicate
                            : ClientInteractionResponseAppendResult.ConflictingResponse;
                    }
                    catch (JsonException)
                    {
                        // Ignore malformed historical records and continue checking the journal.
                    }
                }

                var wasCancelled = _events.Any(item =>
                {
                    if (!item.Type.Equals(ClientInteractionEventTypes.Cancellation, StringComparison.Ordinal)) return false;
                    try
                    {
                        var cancellation = JsonSerializer.Deserialize<ClientInteractionCancellation>(item.Data, ClientInteractionJsonOptions);
                        return cancellation?.CorrelationId == response.CorrelationId;
                    }
                    catch (JsonException)
                    {
                        return false;
                    }
                });
                if (wasCancelled) return ClientInteractionResponseAppendResult.Cancelled;

                var hasMatchingRequest = _events.Any(item =>
                {
                    if (!item.Type.Equals(ClientInteractionEventTypes.Request, StringComparison.Ordinal)) return false;
                    try
                    {
                        var request = JsonSerializer.Deserialize<ClientInteractionRequest>(item.Data, ClientInteractionJsonOptions);
                        var expectedKind = request?.Operation switch
                        {
                            "get_current_page" => "page",
                            "infer_plan" => "plan",
                            "execute" or "navigate" or "inspect" or "await_state" or "highlight" or "clear_highlight" => "action",
                            _ => null
                        };
                        return request?.CorrelationId == response.CorrelationId && expectedKind == response.ResultKind;
                    }
                    catch (JsonException)
                    {
                        return false;
                    }
                });
                if (!hasMatchingRequest) return ClientInteractionResponseAppendResult.RequestNotFound;

                var sequence = checked(++_sequence);
                _events.Enqueue(responseEvent with { Sequence = sequence });
                while (_events.Count > limit) _events.Dequeue();
                return ClientInteractionResponseAppendResult.Accepted;
            }
        }
    }
}

internal enum ClientInteractionResponseAppendResult
{
    Accepted,
    Duplicate,
    RequestNotFound,
    ConflictingResponse,
    Cancelled,
    SessionNotFound
}
