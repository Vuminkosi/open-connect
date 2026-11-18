using OpenConnect.Agents.Templates;
using OpenConnect.Abstractions;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class TemplateNarrowingTests
{
    [Theory]
    [InlineData("../outside.md")]
    [InlineData("C:\\outside.md")]
    public async Task InstructionAdapter_RejectsUnsafePaths(string path)
    {
        var adapter = new InstructionLoaderAdapter((_, _) => Task.FromResult(("text", "revision")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.LoadAsync(path));
    }

    [Fact]
    public async Task InstructionAdapter_PassesThroughLoaderAndCancellation()
    {
        var adapter = new InstructionLoaderAdapter((path, _) => Task.FromResult((path, "sha")));
        var result = await adapter.LoadAsync("templates/lead.md");
        Assert.Equal(("templates/lead.md", "sha"), result);
    }

    [Fact]
    public void Register_RejectsWideningUnlessExplicitlyApproved()
    {
        var registry = new TemplateRegistry();
        registry.Register(Template("base", new HashSet<ToolId>([new ToolId("read")])));
        Assert.Throws<InvalidOperationException>(() => registry.Register(Template("derived", new HashSet<ToolId>([new ToolId("read"), new ToolId("write")]), "base")));
        registry.Register(Template("approved", new HashSet<ToolId>([new ToolId("read"), new ToolId("write")]), "base"), allowWidening: true);
    }

    [Theory]
    [InlineData("MaxTokens")]
    [InlineData("MaxToolCalls")]
    public void Register_RejectsChildBudgetAboveParent(string budgetField)
    {
        var registry = new TemplateRegistry();
        var parentBudget = new AgentBudget(MaxToolCalls: 10, MaxTokens: 100);
        var childBudget = budgetField == "MaxTokens"
            ? parentBudget with { MaxTokens = 101 }
            : parentBudget with { MaxToolCalls = 11 };
        registry.Register(Template("budget-base", new HashSet<ToolId>(), budget: parentBudget));

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Register(
            Template("budget-child", new HashSet<ToolId>(), "budget-base", childBudget)));

        Assert.Contains("budget-child", exception.Message);
        Assert.Contains("budget-base", exception.Message);
        Assert.Contains(budgetField, exception.Message);
        Assert.Contains(budgetField == "MaxTokens" ? "101" : "11", exception.Message);
        Assert.Contains(budgetField == "MaxTokens" ? "100" : "10", exception.Message);
    }

    [Fact]
    public void Register_AcceptsEqualOrNarrowerChildBudget()
    {
        var registry = new TemplateRegistry();
        var parentBudget = new AgentBudget(MaxToolCalls: 10, MaxTokens: 100);
        registry.Register(Template("budget-base", new HashSet<ToolId>(), budget: parentBudget));

        registry.Register(Template("equal-budget", new HashSet<ToolId>(), "budget-base", parentBudget));
        registry.Register(Template("narrow-budget", new HashSet<ToolId>(), "budget-base",
            parentBudget with { MaxToolCalls = 9, MaxTokens = 99 }));
    }

    [Fact]
    public void Register_RejectsUnboundedRuntimeWhenParentIsBounded()
    {
        var registry = new TemplateRegistry();
        var parentBudget = new AgentBudget(10, 10, 10, TimeSpan.FromMinutes(5));
        registry.Register(Template("runtime-base", new HashSet<ToolId>(), budget: parentBudget));

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Register(
            Template("runtime-child", new HashSet<ToolId>(), "runtime-base", parentBudget with { MaxRuntime = null })));

        Assert.Contains("runtime-child", exception.Message);
        Assert.Contains("runtime-base", exception.Message);
        Assert.Contains("MaxRuntime", exception.Message);
        Assert.Contains("unlimited", exception.Message);
        Assert.Contains("00:05:00", exception.Message);
    }

    [Fact]
    public void Register_AcceptsEqualRuntime()
    {
        var registry = new TemplateRegistry();
        var parentBudget = new AgentBudget(10, 10, 10, TimeSpan.FromMinutes(5));
        registry.Register(Template("runtime-base", new HashSet<ToolId>(), budget: parentBudget));

        registry.Register(Template("runtime-equal", new HashSet<ToolId>(), "runtime-base", parentBudget));
    }

    [Fact]
    public void Register_AcceptsShorterRuntime()
    {
        var registry = new TemplateRegistry();
        var parentBudget = new AgentBudget(10, 10, 10, TimeSpan.FromMinutes(5));
        registry.Register(Template("runtime-base", new HashSet<ToolId>(), budget: parentBudget));

        registry.Register(Template("runtime-shorter", new HashSet<ToolId>(), "runtime-base",
            parentBudget with { MaxRuntime = TimeSpan.FromMinutes(4) }));
    }

    [Fact]
    public void Register_AcceptsUnboundedRuntimeWhenParentIsUnbounded()
    {
        var registry = new TemplateRegistry();
        var unboundedBudget = new AgentBudget(10, 10, 10, null);
        registry.Register(Template("runtime-base", new HashSet<ToolId>(), budget: unboundedBudget));

        registry.Register(Template("runtime-child", new HashSet<ToolId>(), "runtime-base", unboundedBudget));
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("C:\\outside.md")]
    public void Register_RejectsUnsafeInstructionPath(string path)
    {
        var registry = new TemplateRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(Template("unsafe", new HashSet<ToolId>()) with { InstructionsPath = path }));
    }

    [Fact]
    public async Task RenderAsync_RejectsUnknownPlaceholder()
    {
        var registry = new TemplateRegistry();
        registry.Register(Template("lead", new HashSet<ToolId>()));
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.RenderAsync("lead", new TemplateVariables(),
            (_, _) => Task.FromResult("{{unknownValue}}")));
    }

    [Fact]
    public async Task RenderAsync_PropagatesInstructionLoaderFailure()
    {
        var registry = new TemplateRegistry();
        registry.Register(Template("lead", new HashSet<ToolId>()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RenderAsync(
            "lead", new TemplateVariables(), (_, _) => Task.FromException<string>(new InvalidOperationException("boom"))));

        Assert.Equal("boom", exception.Message);
    }

    [Fact]
    public async Task RenderAsync_PropagatesLoaderCancellation()
    {
        var registry = new TemplateRegistry();
        registry.Register(Template("lead", new HashSet<ToolId>()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.RenderAsync(
            "lead", new TemplateVariables(), (_, token) => Task.FromCanceled<string>(token), cancellation.Token));
    }

    [Fact]
    public void Register_RejectsChildCostAboveParent()
    {
        var registry = new TemplateRegistry();
        var parentBudget = new AgentBudget(MaxToolCalls: 10, MaxTokens: 100, MaxCostMicrocents: 1000);
        registry.Register(Template("cost-base", new HashSet<ToolId>(), budget: parentBudget));

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Register(
            Template("cost-child", new HashSet<ToolId>(), "cost-base",
                parentBudget with { MaxCostMicrocents = 1001 })));

        Assert.Contains("cost-child", exception.Message);
        Assert.Contains("cost-base", exception.Message);
        Assert.Contains("MaxCostMicrocents", exception.Message);
        Assert.Contains("1001", exception.Message);
        Assert.Contains("1000", exception.Message);
    }

    private static AgentTemplate Template(string id, IReadOnlySet<ToolId> tools, string? baseId = null, AgentBudget? budget = null) =>
        new(id, "1", "test", "instructions/lead.md", null, tools, null, budget ?? new AgentBudget(10, 10, 10, TimeSpan.FromMinutes(1)),
            new AgentMemoryPolicy(), AgentContextSharingMode.TaskSummary, ToolAcknowledgementMode.Automatic, "standard", baseId,
            new HashSet<string>(["read"], StringComparer.Ordinal));
}
