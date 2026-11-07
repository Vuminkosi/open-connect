using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>Defines and validates the approved lifecycle transitions for agent runs.</summary>
/// <remarks>
/// Hosts must consult <see cref="Validate(AgentRunState, AgentRunState)"/> before committing a
/// transition. Durable per-transition enforcement belongs to the Sessions/Persistence journal.
/// Cancelled transitions from an awaiting state represent abandonment or denial followed by
/// cancellation; completion from CancelRequested represents a real completion that raced with
/// cancellation. Terminal states cannot be resurrected, and backward jumps are not permitted.
/// </remarks>
public static class AgentRunStateMachine
{
    /// <summary>Determines whether a run state is terminal.</summary>
    /// <param name="state">The state to inspect.</param>
    /// <returns><see langword="true"/> only for Completed, Failed, Cancelled, or OutcomeUnknown.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The state is not a defined enum value.</exception>
    public static bool IsTerminal(AgentRunState state)
    {
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Undefined agent run state.");

        return state is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled or AgentRunState.OutcomeUnknown;
    }

    /// <summary>Validates that a transition is explicitly allowed by the run lifecycle contract.</summary>
    /// <param name="from">The current state.</param>
    /// <param name="to">The requested next state.</param>
    /// <exception cref="ArgumentOutOfRangeException">Either state is not a defined enum value.</exception>
    /// <exception cref="InvalidOperationException">The transition is not allowed.</exception>
    public static void Validate(AgentRunState from, AgentRunState to)
    {
        if (!Enum.IsDefined(from))
            throw new ArgumentOutOfRangeException(nameof(from), from, "Undefined agent run state.");
        if (!Enum.IsDefined(to))
            throw new ArgumentOutOfRangeException(nameof(to), to, "Undefined agent run state.");

        var allowed = from switch
        {
            AgentRunState.Accepted => to is AgentRunState.Preparing or AgentRunState.CancelRequested,
            AgentRunState.Preparing => to is AgentRunState.Running or AgentRunState.CancelRequested or AgentRunState.Failed,
            AgentRunState.Running => to is AgentRunState.AwaitingUser or AgentRunState.AwaitingApproval or AgentRunState.Paused
                or AgentRunState.CancelRequested or AgentRunState.Completed or AgentRunState.Failed or AgentRunState.OutcomeUnknown,
            AgentRunState.AwaitingUser or AgentRunState.AwaitingApproval or AgentRunState.Paused =>
                to is AgentRunState.Running or AgentRunState.CancelRequested or AgentRunState.Cancelled or AgentRunState.Failed,
            AgentRunState.CancelRequested => to is AgentRunState.Cancelled or AgentRunState.Completed or AgentRunState.Failed or AgentRunState.OutcomeUnknown,
            _ => false
        };

        if (!allowed)
            throw new InvalidOperationException($"Agent run state transition {from} → {to} is not allowed.");
    }
}
