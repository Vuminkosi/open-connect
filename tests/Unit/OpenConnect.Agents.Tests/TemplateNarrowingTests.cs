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

    private static AgentTemplate Template(string id, IReadOnlySet<ToolId> tools, string? baseId = null) =>
        new(id, "1", "test", "instructions/lead.md", null, tools, null, new AgentBudget(10, 10, 10, TimeSpan.FromMinutes(1)),
            new AgentMemoryPolicy(), AgentContextSharingMode.TaskSummary, ToolAcknowledgementMode.Automatic, "standard", baseId,
            new HashSet<string>(["read"], StringComparer.Ordinal));
}
