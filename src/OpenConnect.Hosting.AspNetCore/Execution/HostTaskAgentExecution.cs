using OpenConnect.Agents.Execution;

namespace OpenConnect.Hosting.AspNetCore.Execution;

/// <summary>Trusted, single-task inputs required to execute one durable native MAF turn.</summary>
/// <remarks>This wrapper owns the execution context. The runner is fresh and task-scoped by factory contract; it has no
/// disposal API. Persistence and gate resources remain owned by their host-managed implementations.</remarks>
public sealed class HostTaskAgentExecution : IDisposable
{
    /// <summary>Creates a task-scoped execution supplied by trusted host code.</summary>
    /// <param name="runner">Fresh MAF conversation runner for this invocation.</param>
    /// <param name="context">Host-selected execution authority.</param>
    /// <param name="isExecutionRevoked">Current revocation check, re-evaluated by the runner.</param>
    /// <param name="persistenceContext">Host-selected protected persistence, exact binding, and turn gate.</param>
    public HostTaskAgentExecution(MafConversationRunner runner, AgentExecutionContext context,
        IsExecutionRevoked isExecutionRevoked, NativeSessionTurnPersistenceContext persistenceContext)
    {
        Runner = runner ?? throw new ArgumentNullException(nameof(runner));
        Context = context ?? throw new ArgumentNullException(nameof(context));
        IsExecutionRevoked = isExecutionRevoked ?? throw new ArgumentNullException(nameof(isExecutionRevoked));
        PersistenceContext = persistenceContext ?? throw new ArgumentNullException(nameof(persistenceContext));
    }

    /// <summary>Gets the fresh conversation runner for this task.</summary>
    public MafConversationRunner Runner { get; }
    /// <summary>Gets the trusted authority and cancellation context.</summary>
    public AgentExecutionContext Context { get; }
    /// <summary>Gets the live revocation check.</summary>
    public IsExecutionRevoked IsExecutionRevoked { get; }
    /// <summary>Gets the host-selected durable state and turn ownership context.</summary>
    public NativeSessionTurnPersistenceContext PersistenceContext { get; }

    /// <summary>Disposes the execution context owned by this execution.</summary>
    public void Dispose() => Context.Dispose();
}
