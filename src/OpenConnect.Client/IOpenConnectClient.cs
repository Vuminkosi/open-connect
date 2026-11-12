using OpenConnect.Abstractions;
namespace OpenConnect.Client;
public interface IOpenConnectClient
{
    Task<TaskRecord> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default);
    Task<TaskRecord> CancelTaskAsync(TaskId taskId, CancellationToken cancellationToken = default);
    Task<TaskRecord> GetTaskAsync(TaskId taskId, CancellationToken cancellationToken = default);
    Task<SessionRecord> CreateSessionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException("Session creation is not supported by this client implementation.");
    Task<SessionRecord> GetSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Retrieves a finite snapshot of events for a session after the specified sequence.
    /// This method does not poll or reconnect. To retrieve subsequent events, call it again
    /// using the sequence cursor of the last event received.
    /// </summary>
    /// <param name="sessionId">The session whose events are retrieved.</param>
    /// <param name="afterSequence">The exclusive sequence cursor; defaults to zero.</param>
    /// <param name="cancellationToken">A token used to cancel the request or enumeration.</param>
    /// <returns>The events available after the cursor at the time of the request.</returns>
    IAsyncEnumerable<EventRecord> SubscribeEventsAsync(SessionId sessionId, long afterSequence = 0, CancellationToken cancellationToken = default);
    /// <summary>
    /// Replays canonical event envelopes for a public session UUID after the specified sequence.
    /// This method performs one finite request; to resume, call it again with the last received
    /// event's sequence as the cursor. The cursor is exclusive: the server returns only events
    /// with a greater sequence, in ascending order.
    /// </summary>
    /// <param name="sessionId">The public session UUID from the canonical route (never derived from a ULID).</param>
    /// <param name="afterSequence">The exclusive sequence cursor; defaults to zero.</param>
    /// <param name="cancellationToken">A token used to cancel the request or enumeration.</param>
    /// <returns>The canonical envelopes available after the cursor at the time of the request.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="afterSequence"/> is negative.</exception>
    /// <exception cref="OpenConnectClientException">
    /// Thrown when the service answers with a failure status. Status 422 with error code
    /// <c>legacy_event_without_identity</c> means a stored event lacks genuine canonical
    /// identity and cannot be projected; the caller must not silently skip it.
    /// </exception>
    IAsyncEnumerable<CanonicalAgentEvent> ReplayEventsAsync(Guid sessionId, long afterSequence = 0, CancellationToken cancellationToken = default);
}
public sealed record CreateTaskRequest(string ClientTaskId, string IdempotencyKey, string? Message = null, string? SessionId = null);
public sealed record SessionRecord(SessionId Id, TenantId TenantId, PrincipalId PrincipalId, DateTimeOffset CreatedAt);
