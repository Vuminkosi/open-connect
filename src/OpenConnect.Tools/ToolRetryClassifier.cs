using OpenConnect.Abstractions;

namespace OpenConnect.Tools;

/// <summary>Policy-controlled retry classification for tool invocations.</summary>
public static class ToolRetryClassifier
{
    /// <summary>
    /// Determines whether a tool invocation with the given idempotency policy and execution state may be retried.
    /// Non-idempotent operations and operations with uncertain outcome are never automatically replayed.
    /// </summary>
    public static bool CanRetry(ToolIdempotency idempotency, ToolExecutionState executionState)
    {
        // Never automatically replay non-idempotent effects under any circumstances.
        if (idempotency == ToolIdempotency.NonIdempotent)
            return false;

        // Never replay when outcome is uncertain unless tool is explicitly marked SafeToRetry.
        if (executionState == ToolExecutionState.OutcomeUnknown)
            return idempotency == ToolIdempotency.SafeToRetry;

        // Only retriable on explicit transient/failed states.
        return executionState is ToolExecutionState.Failed;
    }
}
