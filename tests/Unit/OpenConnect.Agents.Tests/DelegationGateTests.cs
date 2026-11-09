using OpenConnect.Abstractions;
using OpenConnect.Agents.Catalog;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class DelegationGateTests
{
    [Fact]
    public void UnknownChild_Throws()
    {
        var (gate, parent, _) = Create();

        Assert.Throws<AgentCatalogException>(() => gate.AuthorizeDelegation(Context(parent), Id("missing"), Request()));
    }

    [Fact]
    public void UnknownParent_Throws()
    {
        var (gate, _, child) = Create();

        Assert.Throws<AgentCatalogException>(() => gate.AuthorizeDelegation(Context(Id("missing")), child, Request()));
    }

    [Fact]
    public void SelfDelegation_And_Reentry_Throw()
    {
        var (gate, parent, child) = Create();

        Assert.Throws<AgentCatalogException>(() => gate.AuthorizeDelegation(Context(parent), parent, Request()));
        Assert.Throws<AgentCatalogException>(() => gate.AuthorizeDelegation(Context(parent, ancestors: [child]), child, Request()));
    }

    [Fact]
    public void UnauthorizedChild_Throws()
    {
        var (gate, _, child) = Create(allowChild: false);

        Assert.Throws<AgentCatalogException>(() => gate.AuthorizeDelegation(Context(Id("parent")), child, Request()));
    }

    [Fact]
    public void MaxChildren_Limit_Enforced()
    {
        var (gate, parent, child) = Create(maxChildren: 1);

        Assert.Throws<AgentCatalogException>(() => gate.AuthorizeDelegation(Context(parent, childCount: 1), child, Request()));
        AssertGrant(gate.AuthorizeDelegation(Context(parent), child, Request()));
    }

    [Fact]
    public void Concurrent_Queued_Total_Budget_Enforced()
    {
        var (gate, parent, child) = Create();
        var budget = Budget(concurrent: 1, queued: 1, total: 2);

        Assert.Throws<AgentCatalogException>(() => gate.AuthorizeDelegation(Context(parent, budget: budget, active: 1), child, Request()));
        Assert.Throws<AgentCatalogException>(() => gate.AuthorizeDelegation(Context(parent, budget: budget, queued: 1), child, Request()));
        Assert.Throws<AgentCatalogException>(() => gate.AuthorizeDelegation(Context(parent, budget: Budget(total: 0)), child, Request()));
    }

    [Fact]
    public void CrossMode_RequiresExplicitEnqueue()
    {
        var (gate, parent, child) = Create(childMode: AgentExecutionMode.Operational);

        Assert.Throws<AgentCatalogException>(() => gate.AuthorizeDelegation(Context(parent), child, Request()));
        AssertGrant(gate.AuthorizeDelegation(Context(parent), child, Request(enqueueCrossMode: true)));

        var (sameModeGate, sameParent, sameChild) = Create();
        AssertGrant(sameModeGate.AuthorizeDelegation(Context(sameParent), sameChild, Request()));
    }

    [Fact]
    public void RequestBeyondEffectiveGrantIsRejected_AndNarrowedGrantIntersects()
    {
        var parentId = Id("parent");
        var childId = Id("child");
        var catalog = Catalog(parentId, childId,
            parentTools: [Tool("shared"), Tool("parent-only")], childTools: [Tool("shared"), Tool("child-only")],
            parentScopes: ["shared", "parent-only"], childScopes: ["shared", "child-only"]);
        var gate = new DelegationGate(catalog);
        var context = Context(parentId,
            tools: [Tool("shared"), Tool("parent-only"), Tool("context-only")],
            scopes: ["shared", "parent-only", "context-only"]);

        Assert.Throws<AgentCatalogException>(() => gate.AuthorizeDelegation(context, childId,
            new DelegationRequest(new HashSet<ToolId> { Tool("child-only") }, new HashSet<string>())));
        var grant = gate.AuthorizeDelegation(context, childId,
            new DelegationRequest(new HashSet<ToolId> { Tool("shared") }, new HashSet<string> { "shared" }));
        Assert.Equal(new HashSet<ToolId> { Tool("shared") }, grant.EffectiveToolIds);
        Assert.Equal(new HashSet<string> { "shared" }, grant.EffectiveScopes);
    }

    [Fact]
    public void RemainingBudget_NarrowsToMinimumPerComponent()
    {
        var (gate, parent, child) = Create(childBudget: Budget(toolCalls: 4, total: 3));
        var grant = gate.AuthorizeDelegation(Context(parent, budget: Budget(toolCalls: 8, total: 5)), child, Request());

        Assert.Equal(4, grant.RemainingBudget.MaxToolCalls);
        Assert.Equal(3, grant.RemainingBudget.MaxTotalAgents);
    }

    [Fact]
    public void NullParentContext_And_Request_Throw()
    {
        var (gate, parent, child) = Create();
        Assert.Throws<ArgumentNullException>(() => gate.AuthorizeDelegation(null!, child, Request()));
        Assert.Throws<ArgumentNullException>(() => gate.AuthorizeDelegation(Context(parent), child, null!));
    }

    private static (DelegationGate Gate, AgentId Parent, AgentId Child) Create(
        bool allowChild = true, int maxChildren = 0, AgentExecutionMode childMode = AgentExecutionMode.Interactive,
        AgentBudget? childBudget = null)
    {
        var parent = Id("parent");
        var child = Id("child");
        return (new DelegationGate(Catalog(parent, child, allowChild, maxChildren, childMode, childBudget)), parent, child);
    }

    private static AgentCatalog Catalog(AgentId parent, AgentId child, bool allowChild = true, int maxChildren = 0,
        AgentExecutionMode childMode = AgentExecutionMode.Interactive, AgentBudget? childBudget = null,
        ToolId[]? parentTools = null, ToolId[]? childTools = null,
        string[]? parentScopes = null, string[]? childScopes = null)
    {
        var catalog = new AgentCatalog();
        catalog.Register(Definition(child, mode: childMode, budget: childBudget,
            tools: childTools ?? [Tool("tool")], scopes: childScopes ?? ["read"]));
        catalog.Register(Definition(parent, allowChild ? [child] : [], maxChildren: maxChildren,
            tools: parentTools ?? [Tool("tool")], scopes: parentScopes ?? ["read"]));
        return catalog;
    }

    private static AgentDefinition Definition(AgentId id, AgentId[]? children = null, int maxChildren = 0,
        AgentExecutionMode mode = AgentExecutionMode.Interactive, AgentBudget? budget = null,
        ToolId[]? tools = null, string[]? scopes = null) => new(id, id.Value)
    {
        ExecutionMode = mode,
        IdentityMode = mode == AgentExecutionMode.Operational ? AgentIdentityMode.WorkloadIdentity : AgentIdentityMode.ActiveUser,
        Operational = mode == AgentExecutionMode.Operational ? new OperationalPolicy(WorkloadIdentityRef: "test") : null,
        Orchestration = new(MaxChildren: maxChildren, AllowedChildAgents: children ?? []),
        ToolPolicy = new(tools ?? [Tool("tool")], scopes ?? ["read"]),
        Budget = budget ?? Budget()
    };

    private static DelegationParentContext Context(AgentId id, AgentBudget? budget = null, AgentId[]? ancestors = null,
        int childCount = 0, int active = 0, int queued = 0, ToolId[]? tools = null, string[]? scopes = null) =>
        new(id, Guid.NewGuid(), 0, ancestors ?? [], new HashSet<ToolId>(tools ?? [Tool("tool")]),
            new HashSet<string>(scopes ?? ["read"]), budget ?? Budget(), AgentExecutionMode.Interactive,
            childCount, active, queued);

    private static DelegationRequest Request(bool enqueueCrossMode = false) =>
        new(new HashSet<ToolId> { Tool("tool") }, new HashSet<string> { "read" }, enqueueCrossMode);

    private static AgentBudget Budget(int toolCalls = 10, int concurrent = 2, int queued = 2, int total = 4) =>
        new(toolCalls, 10, 100, TimeSpan.FromMinutes(1), concurrent, queued, total);

    private static void AssertGrant(DelegationGrant grant) => Assert.Equal(Id("child"), grant.ChildAgentId);
    private static ToolId Tool(string value) => new(value);
    private static AgentId Id(string value) => new(value);
}
