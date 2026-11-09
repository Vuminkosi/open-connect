using Microsoft.Extensions.Configuration;
using OpenConnect.Core.Configuration;
using Xunit;

namespace OpenConnect.Core.Tests;

public sealed class AgentOptionsMappingTests
{
    [Fact]
    public void ToRunBudget_CopiesEveryConfiguredCapAndPreservesRestrictiveValues()
    {
        var options = new AgentRegistryOptions
        {
            MaxDelegationDepth = 1,
            MaxConcurrentSubagents = 1,
            MaxTotalSubagentsPerRun = 2,
            MaxTotalToolCalls = 3,
            MaxModelCalls = 4,
            MaxTurnDuration = TimeSpan.FromSeconds(5)
        };

        var budget = AgentOptionsMapping.ToRunBudget(options);

        Assert.Equal(3, budget.MaxToolCalls);
        Assert.Equal(4, budget.MaxModelCalls);
        Assert.Equal(long.MaxValue, budget.MaxTokens);
        Assert.Equal(TimeSpan.FromSeconds(5), budget.MaxRuntime);
        Assert.Equal(1, budget.MaxConcurrentAgents);
        Assert.Equal(int.MaxValue, budget.MaxQueuedAgents);
        Assert.Equal(2, budget.MaxTotalAgents);
    }

    [Fact]
    public void ToRunBudget_MapsDefaultsToExpectedBudget()
    {
        var budget = AgentOptionsMapping.ToRunBudget(new AgentRegistryOptions());

        Assert.Equal(new OpenConnect.Abstractions.AgentBudget(
            MaxToolCalls: 20,
            MaxModelCalls: 12,
            MaxTokens: long.MaxValue,
            MaxRuntime: TimeSpan.FromSeconds(120),
            MaxConcurrentAgents: 3,
            MaxQueuedAgents: int.MaxValue,
            MaxTotalAgents: 8), budget);
    }

    [Fact]
    public void ToRunBudget_RejectsNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => AgentOptionsMapping.ToRunBudget(null!));
    }
}
