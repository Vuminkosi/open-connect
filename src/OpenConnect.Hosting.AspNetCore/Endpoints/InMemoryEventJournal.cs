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
    private readonly HashSet<Ulid> _claims = new();
    private readonly object _bindingGate = new();
    private readonly Dictionary<string, BoundConnection> _boundConnections = new(StringComparer.Ordinal);
    private readonly Dictionary<(Ulid Session, string Correlation), PendingInteraction> _pendingInteractions = new();
    private readonly HashSet<string> _usedConnectionIds = new(StringComparer.Ordinal);

    // These APIs form the connection-bound LiveUI state primitive. All decisions that
    // depend on both connection and pending-interaction state share one lock.
    internal BoundConnectionRegistration? TryRegisterBoundConnection(
        SessionId sessionId, TenantId tenantId, PrincipalId principalId)
    {
        lock (_bindingGate)
        {
            var connectionId = Ulid.NewUlid().ToString();
            while (!_usedConnectionIds.Add(connectionId))
                connectionId = Ulid.NewUlid().ToString();
            var connection = new BoundConnection(connectionId, sessionId, tenantId, principalId);
            _boundConnections.Add(connectionId, connection);
            return new BoundConnectionRegistration(this, connection);
        }
    }

    internal string? TryClaimSoleBoundConnection(SessionId sessionId, TenantId tenantId, PrincipalId principalId)
    {
        lock (_bindingGate)
        {
            var matches = _boundConnections.Values.Where(connection =>
                connection.SessionId == sessionId && connection.TenantId == tenantId &&
                connection.PrincipalId == principalId).Take(2).ToArray();
            return matches.Length == 1 ? matches[0].ConnectionId : null;
        }
    }

    internal string? TryClaimAndBindPendingInteraction(
        SessionId sessionId, TenantId tenantId, PrincipalId principalId, string correlationId, string? expectedConnectionId = null)
    {
        lock (_bindingGate)
        {
            var matches = _boundConnections.Values.Where(connection =>
                connection.SessionId == sessionId && connection.TenantId == tenantId &&
                connection.PrincipalId == principalId &&
                (expectedConnectionId is null || connection.ConnectionId == expectedConnectionId)).Take(2).ToArray();
            if (matches.Length != 1)
                return null;
            var connection = matches[0];
            var key = (sessionId.Value, correlationId);
            if (_pendingInteractions.ContainsKey(key) ||
                !_boundConnections.TryGetValue(connection.ConnectionId, out var active) || active != connection)
                return null;
            _pendingInteractions.Add(key, new PendingInteraction(connection));
            return connection.ConnectionId;
        }
    }

    internal bool TryBindPendingInteraction(string connectionId, string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        lock (_bindingGate)
        {
            if (!_boundConnections.TryGetValue(connectionId, out var connection))
                return false;
            var key = (connection.SessionId.Value, correlationId);
            if (_pendingInteractions.ContainsKey(key))
                return false;
            _pendingInteractions.Add(key, new PendingInteraction(connection));
            return true;
        }
    }

    internal bool TryExpirePendingInteraction(SessionId sessionId, string correlationId)
    {
        lock (_bindingGate)
        {
            var key = (sessionId.Value, correlationId);
            if (!_pendingInteractions.TryGetValue(key, out var pending) || pending.Completed)
                return false;

            _pendingInteractions[key] = pending with { Completed = true };
            return true;
        }
    }

    internal bool IsPendingInteractionDisconnected(SessionId sessionId, string correlationId, string connectionId)
    {
        lock (_bindingGate)
            return _pendingInteractions.TryGetValue((sessionId.Value, correlationId), out var pending) &&
                pending.Connection.ConnectionId == connectionId && pending.Disconnected;
    }

    internal bool IsInteractionEventForConnection(EventRecord record, string connectionId)
    {
        lock (_bindingGate)
        {
            if (record.Type == ClientInteractionEventTypes.Request)
            {
                try
                {
                    var request = JsonSerializer.Deserialize<ClientInteractionRequest>(record.Data, ClientInteractionJsonOptions);
                    return request?.ConnectionId == connectionId;
                }
                catch (JsonException) { return false; }
            }
            if (record.Type == ClientInteractionEventTypes.Cancellation)
            {
                try
                {
                    var cancellation = JsonSerializer.Deserialize<ClientInteractionCancellation>(record.Data, ClientInteractionJsonOptions);
                    return cancellation is not null &&
                           _pendingInteractions.TryGetValue((record.SessionId.Value, cancellation.CorrelationId), out var pending) &&
                           pending.Connection.ConnectionId == connectionId;
                }
                catch (JsonException) { return false; }
            }
            return true;
        }
    }

    internal ClientInteractionResponseAppendResult TryAppendBoundClientInteractionResponse(
        string connectionId, SessionId sessionId, TenantId tenantId, PrincipalId principalId,
        ClientInteractionResponse response, string eventData)
    {
        ArgumentNullException.ThrowIfNull(response);
        lock (_bindingGate)
        {
            if (!_boundConnections.TryGetValue(connectionId, out var connection) ||
                connection.SessionId != sessionId || connection.TenantId != tenantId ||
                connection.PrincipalId != principalId || response.ConnectionId != connectionId)
                return ClientInteractionResponseAppendResult.RequestNotFound;
            var key = (sessionId.Value, response.CorrelationId);
            if (!_pendingInteractions.TryGetValue(key, out var pending) ||
                pending.Connection.ConnectionId != connectionId || pending.Completed)
                return ClientInteractionResponseAppendResult.RequestNotFound;
            if (!_sessions.TryGetValue(sessionId.Value, out var events))
                return ClientInteractionResponseAppendResult.SessionNotFound;
            var result = events.TryAppendClientInteractionResponse(response,
                new EventRecord(sessionId, 0, ClientInteractionEventTypes.Response, eventData), EventLimit);
            if (result == ClientInteractionResponseAppendResult.Accepted)
                _pendingInteractions[key] = pending with { Completed = true };
            return result;
        }
    }

    private void DisconnectBoundConnection(BoundConnection connection)
    {
        lock (_bindingGate)
        {
            if (!_boundConnections.TryGetValue(connection.ConnectionId, out var active) || active != connection)
                return;
            _boundConnections.Remove(connection.ConnectionId);
            foreach (var key in _pendingInteractions
                         .Where(pair => pair.Value.Connection.ConnectionId == connection.ConnectionId && !pair.Value.Completed)
                         .Select(pair => pair.Key).ToArray())
                _pendingInteractions[key] = _pendingInteractions[key] with { Completed = true, Disconnected = true };
        }
    }

    internal sealed record BoundConnection(string ConnectionId, SessionId SessionId, TenantId TenantId, PrincipalId PrincipalId);
    private sealed record PendingInteraction(BoundConnection Connection, bool Completed = false, bool Disconnected = false);

    internal sealed class BoundConnectionRegistration(InMemoryEventJournal journal, BoundConnection connection) : IDisposable
    {
        private int _disposed;
        internal string ConnectionId => connection.ConnectionId;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                journal.DisconnectBoundConnection(connection);
        }
    }

    internal int GetConnectionCount(SessionId sessionId)
    {
        lock (_bindingGate)
            return _boundConnections.Values.Count(connection => connection.SessionId == sessionId);
    }

    internal ConnectionClaim? TryClaimSoleConnection(SessionId sessionId)
    {
        lock (_bindingGate)
        {
            var matches = _boundConnections.Values.Where(connection => connection.SessionId == sessionId).Take(2).ToArray();
            if (matches.Length != 1 ||
                !_claims.Add(sessionId.Value))
                return null;
            return new ConnectionClaim(this, sessionId.Value, matches[0].ConnectionId);
        }
    }

    internal sealed class ConnectionClaim(InMemoryEventJournal journal, Ulid sessionId, string connectionId) : IDisposable
    {
        internal string ConnectionId => connectionId;
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            lock (journal._bindingGate)
                journal._claims.Remove(sessionId);
        }
    }

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
        if (afterSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
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
        if (afterSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
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
                while (_events.Count > limit)
                    _events.Dequeue();
                return sequence;
            }
        }

        public IReadOnlyList<EventRecord> GetAfter(long sequence)
        {
            lock (_gate)
                return _events.Where(item => item.Sequence > sequence).ToArray();
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
                    if (!item.Type.Equals(ClientInteractionEventTypes.Response, StringComparison.Ordinal))
                        continue;
                    try
                    {
                        var prior = JsonSerializer.Deserialize<ClientInteractionResponse>(item.Data, ClientInteractionJsonOptions);
                        if (prior?.CorrelationId != response.CorrelationId)
                            continue;
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
                    if (!item.Type.Equals(ClientInteractionEventTypes.Cancellation, StringComparison.Ordinal))
                        return false;
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
                if (wasCancelled)
                    return ClientInteractionResponseAppendResult.Cancelled;

                var hasMatchingRequest = _events.Any(item =>
                {
                    if (!item.Type.Equals(ClientInteractionEventTypes.Request, StringComparison.Ordinal))
                        return false;
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
                if (!hasMatchingRequest)
                    return ClientInteractionResponseAppendResult.RequestNotFound;

                var sequence = checked(++_sequence);
                _events.Enqueue(responseEvent with { Sequence = sequence });
                while (_events.Count > limit)
                    _events.Dequeue();
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
