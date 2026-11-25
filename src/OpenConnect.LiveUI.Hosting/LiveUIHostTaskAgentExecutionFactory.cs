using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.LiveUI.Agents;

namespace OpenConnect.LiveUI.Hosting;

/// <summary>Host-selected authority, model and durable state for a single accepted task.</summary>
/// <remarks>These inputs are trusted host state, not model arguments. The returned context is owned by the execution.</remarks>
public sealed record LiveUIHostExecutionInputs(
    MafRealAgentFactory AgentFactory,
    AgentId AgentId,
    AgentExecutionContext Context,
    IsExecutionRevoked IsExecutionRevoked,
    NativeSessionTurnPersistenceContext PersistenceContext,
    string? Instructions = null,
    IReadOnlyList<AITool>? AdditionalTools = null);

/// <summary>Supplies host-authorized execution inputs without deriving authority from prompt text.</summary>
public interface ILiveUIHostExecutionInputsFactory
{
    /// <summary>Creates task-scoped inputs for the authenticated task.</summary>
    Task<LiveUIHostExecutionInputs> CreateAsync(HostTaskExecution task, CancellationToken cancellationToken);
}

/// <summary>Composes a durable agent execution with LiveUI tools for the accepted client connection.</summary>
public sealed class LiveUIHostTaskAgentExecutionFactory(
    ILiveUIHostExecutionInputsFactory inputsFactory,
    IClientInteractionBroker broker) : IHostTaskAgentExecutionFactory
{
    /// <inheritdoc />
    public async Task<HostTaskAgentExecution> CreateAsync(HostTaskExecution task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(inputsFactory);
        ArgumentNullException.ThrowIfNull(broker);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(task.Origin))
            throw new InvalidOperationException("The task origin is invalid.");
        if (task.Origin == HostTaskOrigin.MiniComposer && string.IsNullOrWhiteSpace(task.ClientConnectionId))
            throw new InvalidOperationException("An interactive task requires its accepted client connection.");

        var inputs = await inputsFactory.CreateAsync(task, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The host execution inputs factory returned no inputs.");
        try
        {
            IReadOnlyList<AITool> liveUiTools = task.Origin == HostTaskOrigin.MiniComposer
                ? LiveUIClientAgentToolFactory.CreateToolsForConnection(broker, task.SessionId, task.ClientConnectionId!)
                : [];
            IReadOnlyList<AITool> tools = inputs.AdditionalTools is { Count: > 0 }
                ? [.. liveUiTools, .. inputs.AdditionalTools]
                : liveUiTools;
            var instructions = task.Origin == HostTaskOrigin.MiniComposer
                ? string.IsNullOrWhiteSpace(inputs.Instructions) ? LiveUIAgentGuidance.Instructions
                    : inputs.Instructions + "\n\n" + LiveUIAgentGuidance.Instructions
                : inputs.Instructions;
            var runner = MafConversationRunner.CreateWithTools(inputs.AgentFactory, inputs.AgentId, tools, instructions);
            return new HostTaskAgentExecution(runner, inputs.Context, inputs.IsExecutionRevoked, inputs.PersistenceContext);
        }
        catch
        {
            inputs.Context?.Dispose();
            throw;
        }
    }
}
