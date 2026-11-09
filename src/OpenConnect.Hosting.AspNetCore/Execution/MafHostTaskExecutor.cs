using OpenConnect.Agents.Execution;
using OpenConnect.Abstractions;

namespace OpenConnect.Hosting.AspNetCore.Execution;

/// <summary>Bridges accepted host tasks to durable Microsoft Agent Framework conversation turns.</summary>
public sealed class MafHostTaskExecutor : IHostTaskExecutor
{
    private readonly IHostTaskAgentExecutionFactory _factory;

    /// <summary>Creates an executor that requires an explicit trusted host execution factory.</summary>
    public MafHostTaskExecutor(IHostTaskAgentExecutionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <inheritdoc />
    public async Task ExecuteAsync(HostTaskExecution task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        cancellationToken.ThrowIfCancellationRequested();
        var execution = await _factory.CreateAsync(task, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The host execution factory returned no execution.");
        using (execution)
        {
            ValidateScope(task, execution);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, execution.Context.CancellationToken);
            _ = await execution.Runner.TurnDurableAsync(task.Message, execution.Context,
                execution.IsExecutionRevoked, execution.PersistenceContext, linkedCancellation.Token).ConfigureAwait(false);
        }
    }

    private static void ValidateScope(HostTaskExecution task, HostTaskAgentExecution execution)
    {
        var context = execution.Context;
        var binding = execution.PersistenceContext.Binding;
        if (context.IdentityKind != AgentIdentityMode.ActiveUser ||
            context.TaskId != task.TaskId || context.SessionId != task.SessionId ||
            !StringComparer.Ordinal.Equals(context.TenantScope, task.TenantId.Value.ToString()) ||
            !StringComparer.Ordinal.Equals(context.PrincipalOrWorkloadScope, task.PrincipalId.Value.ToString()) ||
            binding.TenantId != task.TenantId || binding.PrincipalId != task.PrincipalId ||
            binding.SessionId != task.SessionId ||
            !StringComparer.Ordinal.Equals(context.TenantScope, binding.TenantId.Value.ToString()) ||
            !StringComparer.Ordinal.Equals(context.PrincipalOrWorkloadScope, binding.PrincipalId.Value.ToString()))
            throw new UnauthorizedAccessException("The host execution scope does not match the accepted task.");
    }
}
