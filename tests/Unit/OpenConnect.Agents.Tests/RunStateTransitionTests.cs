using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class RunStateTransitionTests
{
    public static TheoryData<AgentRunState, AgentRunState> AllowedTransitions => new()
    {
        { AgentRunState.Accepted, AgentRunState.Preparing },
        { AgentRunState.Accepted, AgentRunState.CancelRequested },
        { AgentRunState.Preparing, AgentRunState.Running },
        { AgentRunState.Preparing, AgentRunState.CancelRequested },
        { AgentRunState.Preparing, AgentRunState.Failed },
        { AgentRunState.Running, AgentRunState.AwaitingUser },
        { AgentRunState.Running, AgentRunState.AwaitingApproval },
        { AgentRunState.Running, AgentRunState.Paused },
        { AgentRunState.Running, AgentRunState.CancelRequested },
        { AgentRunState.Running, AgentRunState.Completed },
        { AgentRunState.Running, AgentRunState.Failed },
        { AgentRunState.Running, AgentRunState.OutcomeUnknown },
        { AgentRunState.AwaitingUser, AgentRunState.Running },
        { AgentRunState.AwaitingUser, AgentRunState.CancelRequested },
        { AgentRunState.AwaitingUser, AgentRunState.Cancelled },
        { AgentRunState.AwaitingUser, AgentRunState.Failed },
        { AgentRunState.AwaitingApproval, AgentRunState.Running },
        { AgentRunState.AwaitingApproval, AgentRunState.CancelRequested },
        { AgentRunState.AwaitingApproval, AgentRunState.Cancelled },
        { AgentRunState.AwaitingApproval, AgentRunState.Failed },
        { AgentRunState.Paused, AgentRunState.Running },
        { AgentRunState.Paused, AgentRunState.CancelRequested },
        { AgentRunState.Paused, AgentRunState.Cancelled },
        { AgentRunState.Paused, AgentRunState.Failed },
        { AgentRunState.CancelRequested, AgentRunState.Cancelled },
        { AgentRunState.CancelRequested, AgentRunState.Completed },
        { AgentRunState.CancelRequested, AgentRunState.Failed },
        { AgentRunState.CancelRequested, AgentRunState.OutcomeUnknown }
    };

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void Validate_AllowsContractedTransition(AgentRunState from, AgentRunState to) =>
        AgentRunStateMachine.Validate(from, to);

    public static TheoryData<AgentRunState, AgentRunState> DisallowedTransitions => new()
    {
        { AgentRunState.Accepted, AgentRunState.Accepted },
        { AgentRunState.Accepted, AgentRunState.Running },
        { AgentRunState.Preparing, AgentRunState.Completed },
        { AgentRunState.Running, AgentRunState.Accepted },
        { AgentRunState.Running, AgentRunState.Preparing },
        { AgentRunState.AwaitingUser, AgentRunState.Completed },
        { AgentRunState.Cancelled, AgentRunState.Paused },
        { AgentRunState.Completed, AgentRunState.Running },
        { AgentRunState.Failed, AgentRunState.Running },
        { AgentRunState.OutcomeUnknown, AgentRunState.Running },
        { AgentRunState.Completed, AgentRunState.Completed },
        { AgentRunState.Failed, AgentRunState.Failed },
        { AgentRunState.Cancelled, AgentRunState.Cancelled },
        { AgentRunState.OutcomeUnknown, AgentRunState.OutcomeUnknown }
    };

    [Theory]
    [MemberData(nameof(DisallowedTransitions))]
    public void Validate_RejectsUncontractedTransition(AgentRunState from, AgentRunState to) =>
        Assert.Throws<InvalidOperationException>(() => AgentRunStateMachine.Validate(from, to));

    [Theory]
    [InlineData((AgentRunState)int.MaxValue, AgentRunState.Accepted)]
    [InlineData(AgentRunState.Accepted, (AgentRunState)int.MaxValue)]
    public void Validate_RejectsUndefinedStatesBeforeTransitionLookup(AgentRunState from, AgentRunState to) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentRunStateMachine.Validate(from, to));

    [Theory]
    [InlineData(AgentRunState.Completed, true)]
    [InlineData(AgentRunState.Failed, true)]
    [InlineData(AgentRunState.Cancelled, true)]
    [InlineData(AgentRunState.OutcomeUnknown, true)]
    [InlineData(AgentRunState.Accepted, false)]
    [InlineData(AgentRunState.Preparing, false)]
    [InlineData(AgentRunState.Running, false)]
    [InlineData(AgentRunState.AwaitingUser, false)]
    [InlineData(AgentRunState.AwaitingApproval, false)]
    [InlineData(AgentRunState.Paused, false)]
    [InlineData(AgentRunState.CancelRequested, false)]
    public void IsTerminal_MatchesContract(AgentRunState state, bool expected) =>
        Assert.Equal(expected, AgentRunStateMachine.IsTerminal(state));
}
