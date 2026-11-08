using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class CoverageBackfillTests
{
    [Fact]
    public void SubagentReservation_RejectsQueueAtLimitAndNegativeCountsWithoutDebiting()
    {
        var ledger = new RunBudgetLedger(new AgentBudget(0, 0, 0, TimeSpan.Zero, 2, 2, 3));

        Assert.False(ledger.TryReserveSubagent(0, 2));
        Assert.False(ledger.TryReserveSubagent(-1, 0));
        Assert.False(ledger.TryReserveSubagent(0, -1));
        Assert.Equal(3, ledger.Remaining.MaxTotalAgents);

        Assert.True(ledger.TryReserveSubagent(0, 1));
        Assert.Equal(2, ledger.Remaining.MaxTotalAgents);
    }

    [Fact]
    public void SubagentReservation_RejectsImmediateActiveCountOverConcurrentLimit()
    {
        var ledger = new RunBudgetLedger(new AgentBudget(0, 0, 0, TimeSpan.Zero, 2, 2, 3));

        Assert.False(ledger.TryReserveSubagent(3, 0));
        Assert.True(ledger.TryReserveSubagent(2, 0));
        Assert.Equal(2, ledger.Remaining.MaxTotalAgents);
    }
}
