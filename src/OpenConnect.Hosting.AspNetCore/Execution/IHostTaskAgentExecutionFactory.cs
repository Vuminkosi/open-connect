namespace OpenConnect.Hosting.AspNetCore.Execution;

/// <summary>Creates a fresh, host-authorized native agent execution for one accepted task.</summary>
/// <remarks>The implementation selects the agent, policy, grants, provider, revision and binding from trusted host state;
/// it must not derive authority from the task message. The returned execution is owned and disposed by the executor.</remarks>
public interface IHostTaskAgentExecutionFactory
{
    /// <summary>Creates an execution scoped to the supplied host task.</summary>
    /// <param name="task">The authenticated task selected by the host.</param>
    /// <param name="cancellationToken">Cancellation for factory work.</param>
    /// <returns>A fresh runner and its complete trusted execution context.</returns>
    Task<HostTaskAgentExecution> CreateAsync(HostTaskExecution task, CancellationToken cancellationToken);
}
