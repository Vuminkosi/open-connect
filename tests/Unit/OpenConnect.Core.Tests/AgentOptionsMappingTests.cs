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
            MaxQueuedAgents = 2,
            MaxTotalSubagentsPerRun = 2,
            MaxTotalToolCalls = 3,
            MaxModelCalls = 4,
            MaxTokens = 500,
            MaxCostMicrocents = 600,
            MaxTurnDuration = TimeSpan.FromSeconds(5)
        };

        var budget = AgentOptionsMapping.ToRunBudget(options);

        Assert.Equal(3, budget.MaxToolCalls);
        Assert.Equal(4, budget.MaxModelCalls);
        Assert.Equal(500, budget.MaxTokens);
        Assert.Equal(TimeSpan.FromSeconds(5), budget.MaxRuntime);
        Assert.Equal(1, budget.MaxConcurrentAgents);
        Assert.Equal(2, budget.MaxQueuedAgents);
        Assert.Equal(2, budget.MaxTotalAgents);
        Assert.Equal(600, budget.MaxCostMicrocents);
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
            MaxTotalAgents: 8,
            MaxCostMicrocents: long.MaxValue), budget);
    }

    [Fact]
    public void ToRunBudget_PreservesRestrictiveValuesBelowSentinels()
    {
        var options = new AgentRegistryOptions
        {
            MaxTokens = 0,
            MaxQueuedAgents = 0,
            MaxCostMicrocents = 0
        };

        var budget = AgentOptionsMapping.ToRunBudget(options);

        Assert.Equal(0, budget.MaxTokens);
        Assert.Equal(0, budget.MaxQueuedAgents);
        Assert.Equal(0, budget.MaxCostMicrocents);
    }

    [Fact]
    public void ToRunBudget_RejectsNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => AgentOptionsMapping.ToRunBudget(null!));
    }
}
