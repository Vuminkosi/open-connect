using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;

namespace OpenConnect.Hosting.AspNetCore.Execution;

/// <summary>Bridges accepted host tasks to durable Microsoft Agent Framework conversation turns.</summary>
public sealed class MafHostTaskExecutor : IHostTaskExecutor
{
    private readonly IHostTaskAgentExecutionFactory _factory;
    private readonly InMemorySessionStore? _sessions;

    /// <summary>Creates an executor that requires an explicit trusted host execution factory.</summary>
    /// <remarks>This compatibility constructor does not publish response events. Supply a session store to publish them.</remarks>
    public MafHostTaskExecutor(IHostTaskAgentExecutionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <summary>Creates an executor that also publishes completed responses to the owning session.</summary>
    public MafHostTaskExecutor(IHostTaskAgentExecutionFactory factory, InMemorySessionStore sessions)
        : this(factory) => _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    /// <inheritdoc />
    public async Task ExecuteAsync(HostTaskExecution task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        cancellationToken.ThrowIfCancellationRequested();
        if (_sessions is not null)
        {
            var session = _sessions.Get(task.SessionId.Value);
            if (session is null || session.TenantId != task.TenantId || session.PrincipalId != task.PrincipalId)
                throw new UnauthorizedAccessException("The task session does not match the accepted scope.");
        }
        var execution = await _factory.CreateAsync(task, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The host execution factory returned no execution.");
        using (execution)
        {
            ValidateScope(task, execution);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, execution.Context.CancellationToken);
            var response = await execution.Runner.TurnDurableAsync(task.Message, execution.Context,
                execution.IsExecutionRevoked, execution.PersistenceContext, linkedCancellation.Token).ConfigureAwait(false);
            if (_sessions is not null && !string.IsNullOrWhiteSpace(response.Text))
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                if (await execution.IsExecutionRevoked(execution.Context, linkedCancellation.Token).ConfigureAwait(false))
                    throw new UnauthorizedAccessException("The task execution was revoked before response publication.");
                await _sessions.AppendTaskEventAsync(task.SessionId.Value, task.TaskId, "message", response.Text,
                    linkedCancellation.Token).ConfigureAwait(false);
            }
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
