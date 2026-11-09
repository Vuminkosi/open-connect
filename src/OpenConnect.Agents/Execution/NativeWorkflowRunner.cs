using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace OpenConnect.Agents.Execution;

/// <summary>Runs a host-constructed native Microsoft Agent Framework workflow.</summary>
/// <remarks>The caller owns workflow construction and authorization of its executors and tools.</remarks>
public sealed class NativeWorkflowRunner
{
    /// <summary>Executes a native MAF workflow and returns its genuine workflow events.</summary>
    /// <typeparam name="TInput">The host-constructed input type accepted by the workflow.</typeparam>
    /// <param name="workflow">The native MAF workflow to execute.</param>
    /// <param name="input">Trusted input constructed by the host.</param>
    /// <param name="cancellationToken">Cancellation request for the workflow execution.</param>
    /// <returns>Native events and output events after the run completes successfully.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the run.</exception>
    public async Task<NativeWorkflowRunResult> RunAsync<TInput>(
        Workflow workflow,
        TInput input,
        CancellationToken cancellationToken = default)
        where TInput : notnull
    {
        return await RunCoreAsync(workflow, input, startAgentTurn: false, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Executes a native MAF agent workflow for the supplied chat turn.</summary>
    /// <param name="workflow">A native agent workflow using the Agent Workflow Chat Protocol.</param>
    /// <param name="input">The host-constructed chat messages for the initial turn.</param>
    /// <param name="cancellationToken">Cancellation request for workflow execution.</param>
    /// <returns>Native events and output events after the agent turn completes successfully.</returns>
    /// <exception cref="InvalidOperationException">The workflow does not accept a native turn token.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the run.</exception>
    public Task<NativeWorkflowRunResult> RunAgentTurnAsync(
        Workflow workflow,
        List<ChatMessage> input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return RunCoreAsync(workflow, input, startAgentTurn: true, cancellationToken);
    }

    private async Task<NativeWorkflowRunResult> RunCoreAsync<TInput>(
        Workflow workflow,
        TInput input,
        bool startAgentTurn,
        CancellationToken cancellationToken)
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        await using var run = await InProcessExecution.Default
            .RunStreamingAsync(workflow, input, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var events = new List<WorkflowEvent>();
        try
        {
            if (startAgentTurn && !await run.TrySendMessageAsync(new TurnToken(true)).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The native workflow does not accept an agent turn token.");
            }

            await foreach (var workflowEvent in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (workflowEvent is WorkflowErrorEvent errorEvent)
                {
                    throw errorEvent.Exception ?? new InvalidOperationException("Native workflow execution failed.");
                }

                events.Add(workflowEvent);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new NativeWorkflowRunResult(events.AsReadOnly());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await run.CancelRunAsync().ConfigureAwait(false);
            throw;
        }
    }
}
