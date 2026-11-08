using Microsoft.Agents.AI.Workflows;

namespace OpenConnect.Agents.Execution;

/// <summary>Contains the native MAF events observed during a completed workflow run.</summary>
/// <remarks>These events are execution observations only; they are not persisted application events.</remarks>
public sealed class NativeWorkflowRunResult
{
    internal NativeWorkflowRunResult(IReadOnlyList<WorkflowEvent> events)
    {
        Events = events;
        Outputs = events.OfType<WorkflowOutputEvent>().ToArray();
    }

    /// <summary>Gets the actual MAF events in the order they were observed.</summary>
    public IReadOnlyList<WorkflowEvent> Events { get; }

    /// <summary>Gets the actual MAF workflow output events.</summary>
    public IReadOnlyList<WorkflowOutputEvent> Outputs { get; }
}
