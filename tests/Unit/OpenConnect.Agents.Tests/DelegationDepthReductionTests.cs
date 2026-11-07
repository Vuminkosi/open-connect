using OpenConnect.Abstractions;
using OpenConnect.Agents.Catalog;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class DelegationDepthReductionTests
{
    [Fact]
    public void ConfiguredGateMaxDepthOne_AllowsFirstLevelDeniesSecond()
    {
        var (catalog, lead, middle, leaf) = CreateCatalog();
        var gate = new DelegationGate(catalog, configuredMaxDepth: 1);
        var budget = Budget();

        var first = gate.AuthorizeDelegation(Context(lead, 0, budget), middle, Request());
        Assert.Equal(1, first.ChildDepth);

        var error = Assert.Throws<DelegationDepthExceededException>(() =>
            gate.AuthorizeDelegation(Context(middle, 1, first.RemainingBudget), leaf, Request()));
        Assert.Equal(2, error.RequestedDepth);
        Assert.Equal(1, error.MaximumDepth);
    }

    [Fact]
    public void PerAgent_MaxDepthOne_NarrowsFrameworkMax()
    {
        var lead = Id("lead"); var middle = Id("middle"); var leaf = Id("leaf");
        var catalog = new AgentCatalog();
        catalog.Register(Definition(leaf));
        catalog.Register(Definition(middle, [leaf], maxDepth: 1));
        catalog.Register(Definition(lead, [middle], maxDepth: 1));
        var gate = new DelegationGate(catalog);
        var first = gate.AuthorizeDelegation(Context(lead, 0, Budget()), middle, Request());
        Assert.Equal(1, first.ChildDepth);

        var error = Assert.Throws<DelegationDepthExceededException>(() =>
            gate.AuthorizeDelegation(Context(middle, 1, first.RemainingBudget), leaf, Request()));
        Assert.Equal(2, error.RequestedDepth);
        Assert.Equal(1, error.MaximumDepth);
    }

    [Fact]
    public void ZeroConfiguredDepth_AllowsNoDelegation()
    {
        // The gate constructor accepts zero (rejecting only negative values), so test the global cap directly.
        var (catalog, lead, middle, _) = CreateCatalog();
        var gate = new DelegationGate(catalog, configuredMaxDepth: 0);

        var error = Assert.Throws<DelegationDepthExceededException>(() =>
            gate.AuthorizeDelegation(Context(lead, 0, Budget()), middle, Request()));
        Assert.Equal(1, error.RequestedDepth);
        Assert.Equal(0, error.MaximumDepth);
    }

    private static (AgentCatalog Catalog, AgentId Lead, AgentId Middle, AgentId Leaf) CreateCatalog()
    {
        var lead = Id("lead"); var middle = Id("middle"); var leaf = Id("leaf");
        var catalog = new AgentCatalog();
        catalog.Register(Definition(leaf));
        catalog.Register(Definition(middle, [leaf]));
        catalog.Register(Definition(lead, [middle]));
        return (catalog, lead, middle, leaf);
    }

    private static AgentDefinition Definition(AgentId id, AgentId[]? children = null, int maxDepth = 2) => new(id, id.Value)
    {
        Orchestration = new(MaxDepth: maxDepth, AllowedChildAgents: children),
        ToolPolicy = new(new[] { new ToolId("tool") }, new[] { "read" }),
        Budget = Budget()
    };

    private static DelegationParentContext Context(AgentId id, int depth, AgentBudget budget) => new(id, Guid.NewGuid(), depth,
        [], new HashSet<ToolId>([new ToolId("tool")]), new HashSet<string>(["read"]), budget, AgentExecutionMode.Interactive);

    private static DelegationRequest Request() => new(new HashSet<ToolId>([new ToolId("tool")]), new HashSet<string>(["read"]));
    private static AgentBudget Budget() => new(10, 10, 100, TimeSpan.FromMinutes(1), 2, 2, 4);
    private static AgentId Id(string value) => new(value);
}
