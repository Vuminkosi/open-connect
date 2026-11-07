using OpenConnect.Abstractions;
using OpenConnect.Agents.Catalog;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class AgentCatalogDepthTests
{
    [Fact]
    public void DelegationGate_AllowsDepthOneAndTwoAndRejectsThree()
    {
        var root = Id("root"); var middle = Id("middle"); var leaf = Id("leaf");
        var catalog = new AgentCatalog();
        catalog.Register(Definition(leaf));
        catalog.Register(Definition(middle, leaf));
        catalog.Register(Definition(root, middle));
        var gate = new DelegationGate(catalog);
        var budget = new AgentBudget(10, 10, 100, TimeSpan.FromMinutes(1), 2, 2, 4);

        var grant1 = gate.AuthorizeDelegation(Context(root, 0, budget), middle, Request());
        Assert.Equal(1, grant1.ChildDepth);
        var grant2 = gate.AuthorizeDelegation(Context(middle, 1, grant1.RemainingBudget), leaf, Request());
        Assert.Equal(2, grant2.ChildDepth);
        Assert.Throws<DelegationDepthExceededException>(() => gate.AuthorizeDelegation(Context(leaf, 2, grant2.RemainingBudget), Id("root"), Request()));
    }

    [Fact]
    public void Catalog_RejectsUnknownChildAndCycles()
    {
        var catalog = new AgentCatalog();
        Assert.Throws<AgentCatalogException>(() => catalog.Register(Definition(Id("root"), Id("missing"))));

        var cyclic = new AgentCatalog();
        Assert.Throws<AgentCatalogException>(() => cyclic.Register(Definition(Id("root"), Id("root"))));
        Assert.Throws<AgentCatalogException>(() => AgentCatalog.Validate([Definition(Id("root"), Id("middle")), Definition(Id("middle"), Id("root"))]));
    }

    private static AgentDefinition Definition(AgentId id, params AgentId[] children) => new(id, id.Value)
    {
        Orchestration = new(AllowedChildAgents: children),
        ToolPolicy = new(new[] { new ToolId("tool") }, new[] { "read" }),
        Budget = new AgentBudget(10, 10, 100, TimeSpan.FromMinutes(1), 2, 2, 4)
    };

    private static DelegationParentContext Context(AgentId id, int depth, AgentBudget budget) => new(id, Guid.NewGuid(), depth,
        [], new HashSet<ToolId>([new ToolId("tool")]), new HashSet<string>(["read"]), budget, AgentExecutionMode.Interactive);
    private static DelegationRequest Request() => new(new HashSet<ToolId>([new ToolId("tool")]), new HashSet<string>(["read"]));
    private static AgentId Id(string value) => new(value);
}
