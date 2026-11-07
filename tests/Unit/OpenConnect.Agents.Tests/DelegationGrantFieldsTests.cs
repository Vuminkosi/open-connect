using OpenConnect.Abstractions;
using OpenConnect.Agents.Catalog;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class DelegationGrantFieldsTests
{
    [Fact]
    public void AuthorizeDelegation_PreservesTrimmedReason()
    {
        var grant = Authorize("  report generation  ");

        Assert.Equal("report generation", grant.Reason);
    }

    [Fact]
    public void AuthorizeDelegation_RecordsChildDefinitionVersion()
    {
        var grant = Authorize();

        Assert.Equal("9.9.9", grant.ChildDefinitionRevision);
    }

    [Theory]
    [InlineData("x\ny")]
    [InlineData("x\ty")]
    public void AuthorizeDelegation_RejectsControlCharacters(string reason)
    {
        Assert.Throws<AgentCatalogException>(() => Authorize(reason));
    }

    [Fact]
    public void DelegationReasonDefaultsToEmpty_AndLegacyGrantConstructionCompiles()
    {
        var grant = Authorize();
        var legacyGrant = new DelegationGrant(Id("parent"), Id("child"), 1,
            new HashSet<ToolId>(), new HashSet<string>(), new AgentBudget(), Guid.NewGuid(), "policy");

        Assert.Equal(string.Empty, grant.Reason);
        Assert.Equal(string.Empty, legacyGrant.Reason);
        Assert.Equal("1", legacyGrant.ChildDefinitionRevision);
    }

    [Fact]
    public void AuthorizeDelegation_RejectsReasonLongerThan500Characters()
    {
        Assert.Throws<AgentCatalogException>(() => Authorize(new string('x', 501)));
    }

    private static DelegationGrant Authorize(string? reason = null)
    {
        var parentId = Id("parent");
        var childId = Id("child");
        var catalog = new AgentCatalog();
        catalog.Register(Definition(childId) with { Version = "9.9.9" });
        catalog.Register(Definition(parentId, childId));
        var budget = new AgentBudget(10, 10, 100, TimeSpan.FromMinutes(1), 2, 2, 4);
        var context = new DelegationParentContext(parentId, Guid.NewGuid(), 0, [],
            new HashSet<ToolId>([new("tool")]), new HashSet<string>(["read"]), budget,
            AgentExecutionMode.Interactive);
        var request = new DelegationRequest(new HashSet<ToolId>([new("tool")]), new HashSet<string>(["read"]), Reason: reason);

        return new DelegationGate(catalog).AuthorizeDelegation(context, childId, request);
    }

    private static AgentDefinition Definition(AgentId id, params AgentId[] children) => new(id, id.Value)
    {
        Orchestration = new(AllowedChildAgents: children),
        ToolPolicy = new(new[] { new ToolId("tool") }, new[] { "read" }),
        Budget = new AgentBudget(10, 10, 100, TimeSpan.FromMinutes(1), 2, 2, 4)
    };

    private static AgentId Id(string value) => new(value);
}
