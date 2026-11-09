using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>Result of authorizing an explicit identity-mode task transition.</summary>
/// <param name="Allowed">Whether the transition is authorized for enqueue.</param>
/// <param name="RequiresFreshUserTask">Whether a fresh user task/review is required before continuing.</param>
public sealed record TransitionAuthorization(bool Allowed, bool RequiresFreshUserTask);

/// <summary>Enforces interactive/workload identity boundaries without ambient authentication dependencies.</summary>
public static class OperationalBoundary
{
    /// <summary>Requires an authenticated-user authority scope for an interactive execution.</summary>
    /// <param name="context">Host-created execution context.</param>
    public static void AssertInteractive(AgentExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IdentityKind != AgentIdentityMode.ActiveUser || string.IsNullOrWhiteSpace(context.PrincipalOrWorkloadScope))
            throw new UnauthorizedAccessException("Interactive execution requires an active-user identity and principal scope.");
        // A raw bearer token is never present in this context by construction; this is an authority scope only.
    }

    /// <summary>Requires the configured workload identity and an exact, non-ambient scope match.</summary>
    /// <param name="context">Host-created execution context.</param>
    /// <param name="workloadIdentityRef">Configured workload identity reference.</param>
    public static void AssertOperational(AgentExecutionContext context, string workloadIdentityRef)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadIdentityRef);
        if (context.IdentityKind != AgentIdentityMode.WorkloadIdentity ||
            !string.Equals(context.PrincipalOrWorkloadScope, workloadIdentityRef, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Operational execution requires the exact configured workload identity scope.");
    }

    /// <summary>Authorizes only same-mode execution or an explicitly enqueued cross-mode task.</summary>
    /// <param name="from">Identity mode of the originating task.</param>
    /// <param name="to">Identity mode requested by the next task.</param>
    /// <param name="isExplicitEnqueue">Whether a separately authorized enqueue was performed.</param>
    /// <returns>Authorization, including whether operational-to-interactive requires fresh user review.</returns>
    public static TransitionAuthorization AssertCrossModeTransition(AgentExecutionMode from, AgentExecutionMode to, bool isExplicitEnqueue)
    {
        if (!Enum.IsDefined(from) || !Enum.IsDefined(to))
            throw new UnauthorizedAccessException("Undefined identity mode cannot authorize a task transition.");
        if (from == to) return new(true, false);
        if (!isExplicitEnqueue)
            throw new UnauthorizedAccessException("Cross-mode transitions require an explicit authorized enqueue.");
        if (from == AgentExecutionMode.Interactive && to == AgentExecutionMode.Operational)
            return new(true, false);
        if (from == AgentExecutionMode.Operational && to == AgentExecutionMode.Interactive)
            return new(true, true);
        throw new UnauthorizedAccessException("Unsupported identity-mode transition.");
    }
}
