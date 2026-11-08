using OpenConnect.Abstractions;

namespace OpenConnect.Abstractions.Persistence;

/// <summary>Provides atomic lifecycle transitions for tasks.</summary>
public interface ITaskLifecycleStore
{
    /// <summary>
    /// Transitions a task only when its tenant, principal, identity, and current state match.
    /// Returns <see langword="null"/> only when the scoped compare-and-set did not match.
    /// A non-null result is the task's observed state after the successful update and may reflect
    /// a subsequent transition; it is not necessarily <paramref name="newState"/>.
    /// </summary>
    Task<TaskRecord?> TransitionAsync(
        TaskId taskId,
        TenantId tenantId,
        PrincipalId principalId,
        AgentRunState expectedState,
        AgentRunState newState,
        CancellationToken cancellationToken = default);
}
