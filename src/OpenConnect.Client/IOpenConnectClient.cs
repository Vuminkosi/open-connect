using OpenConnect.Abstractions;
namespace OpenConnect.Client;
public interface IOpenConnectClient
{
    Task<TaskRecord> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default);
    Task<TaskRecord> CancelTaskAsync(TaskId taskId, CancellationToken cancellationToken = default);
    Task<TaskRecord> GetTaskAsync(TaskId taskId, CancellationToken cancellationToken = default);
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
}
public sealed record CreateTaskRequest(string ClientTaskId, string IdempotencyKey);
public sealed record SessionRecord(SessionId Id, TenantId TenantId, PrincipalId PrincipalId, DateTimeOffset CreatedAt);
