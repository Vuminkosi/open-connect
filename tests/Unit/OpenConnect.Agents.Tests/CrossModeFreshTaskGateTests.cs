using OpenConnect.Abstractions;
using OpenConnect.Agents.Catalog;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class CrossModeFreshTaskGateTests
{
    [Fact]
    public void OperationalToInteractive_WithoutFreshTaskToken_Denied()
    {
        var (gate, parent, child) = CreateOperationalParentInteractiveChild();

        // Null token.
        var nullEx = Assert.Throws<AgentCatalogException>(() => gate.AuthorizeDelegation(
            OperationalContext(parent), child, Request(enqueueCrossMode: true, freshTaskToken: null)));
        Assert.Contains("fresh authenticated user task review token", nullEx.Message);

        // Whitespace token.
        var wsEx = Assert.Throws<AgentCatalogException>(() => gate.AuthorizeDelegation(
            OperationalContext(parent), child, Request(enqueueCrossMode: true, freshTaskToken: "   ")));
        Assert.Contains("fresh authenticated user task review token", wsEx.Message);

        // Short token (below the Length >= 16 well-formedness floor).
        var shortEx = Assert.Throws<AgentCatalogException>(() => gate.AuthorizeDelegation(
            OperationalContext(parent), child, Request(enqueueCrossMode: true, freshTaskToken: "short")));
        Assert.Contains("fresh authenticated user task review token", shortEx.Message);
    }

    [Fact]
    public void OperationalToInteractive_WithValidFreshTaskToken_Succeeds()
    {
        var childBudget = Budget(toolCalls: 4, total: 3);
        var (gate, parent, child) = CreateOperationalParentInteractiveChild(childBudget: childBudget);

        var grant = gate.AuthorizeDelegation(
            OperationalContext(parent, budget: Budget(toolCalls: 8, total: 5)),
            child,
            Request(enqueueCrossMode: true, freshTaskToken: "0123456789abcdef"));

        Assert.Equal(child, grant.ChildAgentId);
        Assert.Equal(4, grant.RemainingBudget.MaxToolCalls);
        Assert.Equal(3, grant.RemainingBudget.MaxTotalAgents);
    }

    [Fact]
    public void InteractiveToOperational_WithEnqueue_StillSucceedsWithoutToken()
    {
        // AUTH-011 preserved: interactive -> operational with enqueue needs no fresh-task token.
        var parent = Id("parent");
        var child = Id("child");
        var catalog = new AgentCatalog();
        catalog.Register(Definition(child, mode: AgentExecutionMode.Operational));
        catalog.Register(Definition(parent, children: [child]));
        var gate = new DelegationGate(catalog);

        var grant = gate.AuthorizeDelegation(Context(parent), child, Request(enqueueCrossMode: true));

        Assert.Equal(child, grant.ChildAgentId);
    }

    [Fact]
    public void SameMode_UnaffectedByTokenAbsence()
    {
        var (gate, parent, child) = CreateSameMode();

        // Same-mode delegation succeeds without any token.
        var grant = gate.AuthorizeDelegation(Context(parent), child, Request());
        Assert.Equal(child, grant.ChildAgentId);

        // Cross-mode without enqueue is still denied with the pre-existing message.
        var (oiGate, oiParent, oiChild) = CreateOperationalParentInteractiveChild();
        var ex = Assert.Throws<AgentCatalogException>(() => oiGate.AuthorizeDelegation(
            OperationalContext(oiParent), oiChild, Request(enqueueCrossMode: false)));
        Assert.Contains("explicit enqueue", ex.Message);
    }

    private static (DelegationGate Gate, AgentId Parent, AgentId Child) CreateOperationalParentInteractiveChild(
        AgentBudget? childBudget = null)
    {
        var parent = Id("parent");
        var child = Id("child");
        var catalog = new AgentCatalog();
        catalog.Register(Definition(child, mode: AgentExecutionMode.Interactive, budget: childBudget));
        catalog.Register(Definition(parent, mode: AgentExecutionMode.Operational, children: [child]));
        return (new DelegationGate(catalog), parent, child);
    }

    private static (DelegationGate Gate, AgentId Parent, AgentId Child) CreateSameMode()
    {
        var parent = Id("parent");
        var child = Id("child");
        var catalog = new AgentCatalog();
        catalog.Register(Definition(child, mode: AgentExecutionMode.Interactive));
        catalog.Register(Definition(parent, mode: AgentExecutionMode.Interactive, children: [child]));
        return (new DelegationGate(catalog), parent, child);
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

    private static DelegationParentContext OperationalContext(AgentId id, AgentBudget? budget = null,
        AgentId[]? ancestors = null, int childCount = 0, int active = 0, int queued = 0) =>
        new(id, Guid.NewGuid(), 0, ancestors ?? [], new HashSet<ToolId>([Tool("tool")]),
            new HashSet<string>(["read"]), budget ?? Budget(), AgentExecutionMode.Operational,
            childCount, active, queued);

    private static DelegationParentContext Context(AgentId id, AgentBudget? budget = null) =>
        new(id, Guid.NewGuid(), 0, [], new HashSet<ToolId>([Tool("tool")]),
            new HashSet<string>(["read"]), budget ?? Budget(), AgentExecutionMode.Interactive,
            0, 0, 0);

    private static DelegationRequest Request(bool enqueueCrossMode = false, string? freshTaskToken = null) =>
        new(new HashSet<ToolId> { Tool("tool") }, new HashSet<string> { "read" }, enqueueCrossMode, null, freshTaskToken);

    private static AgentBudget Budget(int toolCalls = 10, int concurrent = 2, int queued = 2, int total = 4) =>
        new(toolCalls, 10, 100, TimeSpan.FromMinutes(1), concurrent, queued, total);

    private static ToolId Tool(string value) => new(value);
    private static AgentId Id(string value) => new(value);
}
