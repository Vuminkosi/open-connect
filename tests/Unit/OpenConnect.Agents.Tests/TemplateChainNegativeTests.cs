using OpenConnect.Abstractions;
using OpenConnect.Agents.Templates;
using Xunit;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Chain-negative coverage for template inheritance (CONFIG-008 SLICE-C3).
/// Proves self-base and missing-base are rejected with precise diagnostics,
/// and guards that a valid deep narrowing chain still resolves.
/// Two-node A→B→A has no direct test: first registration necessarily fails missing-base (TemplateRegistry.cs:200-201) and re-registration is barred as duplicate (41-42).
/// </summary>
public sealed class TemplateChainNegativeTests
{
    [Fact]
    public void Register_RejectsSelfBase_AsCycleNamingOffendingId()
    {
        var registry = new TemplateRegistry();

        var exception = Assert.Throws<InvalidOperationException>(
            () => registry.Register(Template("chain-self", new HashSet<ToolId>(), "chain-self")));

        Assert.Contains("chain-self", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_RejectsMissingBase_NamingChildAndBase()
    {
        var registry = new TemplateRegistry();

        var exception = Assert.Throws<InvalidOperationException>(
            () => registry.Register(Template("chain-orphan", new HashSet<ToolId>(), "chain-missing-base")));

        Assert.Contains("chain-orphan", exception.Message, StringComparison.Ordinal);
        Assert.Contains("chain-missing-base", exception.Message, StringComparison.Ordinal);
        Assert.Contains("missing base", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_AcceptsDeepNarrowingChain_AndResolvesLeaf()
    {
        var registry = new TemplateRegistry();
        var budget = new AgentBudget(10, 10, 100, TimeSpan.FromMinutes(5));
        registry.Register(Template("chain-base", new HashSet<ToolId>([new ToolId("read")]), budget: budget));
        registry.Register(Template("chain-mid", new HashSet<ToolId>([new ToolId("read")]), "chain-base", budget));
        registry.Register(Template("chain-leaf", new HashSet<ToolId>(), "chain-mid", budget));

        var resolved = registry.Resolve("chain-leaf");

        Assert.Equal("chain-leaf", resolved.Id);
    }

    private static AgentTemplate Template(string id, IReadOnlySet<ToolId> tools, string? baseId = null, AgentBudget? budget = null) =>
        new(id, "1", "test", "instructions/lead.md", null, tools, null, budget ?? new AgentBudget(10, 10, 10, TimeSpan.FromMinutes(1)),
            new AgentMemoryPolicy(), AgentContextSharingMode.TaskSummary, ToolAcknowledgementMode.Automatic, "standard", baseId,
            new HashSet<string>(["read"], StringComparer.Ordinal));
}
