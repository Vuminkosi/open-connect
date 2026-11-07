using OpenConnect.Abstractions;
using OpenConnect.Agents.Catalog;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class LeadCapabilityRouterTests
{
    [Fact]
    public void ResolveChild_ReturnsFirstListedChild()
    {
        var lead = Id("lead");
        var first = Id("first");
        var second = Id("second");
        var catalog = Catalog(Definition(first), Definition(second), Definition(lead, first, second));
        var router = Router(("research", new[] { first, second }));

        router.Validate(catalog, lead);

        Assert.Equal(first, router.ResolveChild("research"));
    }

    [Fact]
    public void ResolveChild_UnknownCapabilityIsDenied()
    {
        var router = Router(("research", new[] { Id("child") }));

        var exception = Assert.Throws<UnauthorizedAccessException>(() => router.ResolveChild("writing"));

        Assert.Equal("No child agent is available for this capability.", exception.Message);
    }

    [Fact]
    public void ResolveChild_WhitespaceCapabilityIsDenied() =>
        Assert.Throws<UnauthorizedAccessException>(() => Router(("research", new[] { Id("child") })).ResolveChild("  "));

    [Fact]
    public void Validate_RejectsUnregisteredChild()
    {
        var lead = Id("lead");
        var router = Router(("research", new[] { Id("missing") }));

        var exception = Assert.Throws<InvalidOperationException>(() => router.Validate(Catalog(Definition(lead)), lead));

        Assert.Contains("research", exception.Message);
        Assert.Contains("missing", exception.Message);
    }

    [Fact]
    public void Validate_RejectsChildNotAllowedByLead()
    {
        var lead = Id("lead");
        var child = Id("child");
        var router = Router(("research", new[] { child }));

        var exception = Assert.Throws<InvalidOperationException>(() => router.Validate(Catalog(Definition(lead), Definition(child)), lead));

        Assert.Contains("research", exception.Message);
        Assert.Contains("child", exception.Message);
    }

    [Fact]
    public void Constructor_RejectsEmptyChildList()
    {
        var routes = new Dictionary<string, IReadOnlyList<AgentId>>(StringComparer.Ordinal)
        {
            ["research"] = Array.Empty<AgentId>()
        };

        Assert.Throws<ArgumentException>(() => new LeadCapabilityRouter(routes));
    }

    private static LeadCapabilityRouter Router(params (string Capability, AgentId[] Children)[] routes) =>
        new(routes.ToDictionary(x => x.Capability, x => (IReadOnlyList<AgentId>)x.Children, StringComparer.Ordinal));

    private static AgentCatalog Catalog(params AgentDefinition[] definitions)
    {
        var catalog = new AgentCatalog();
        foreach (var definition in definitions) catalog.Register(definition);
        return catalog;
    }

    private static AgentDefinition Definition(AgentId id, params AgentId[] children) => new(id, id.Value)
    {
        Orchestration = new(AllowedChildAgents: children),
        ToolPolicy = new(new[] { new ToolId("tool") }, new[] { "read" }),
        Budget = new AgentBudget(10, 10, 100, TimeSpan.FromMinutes(1), 2, 2, 4)
    };

    private static AgentId Id(string value) => new(value);
}
