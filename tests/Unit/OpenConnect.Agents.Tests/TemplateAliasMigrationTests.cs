using OpenConnect.Abstractions;
using OpenConnect.Agents.Templates;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class TemplateAliasMigrationTests
{
    [Fact]
    public void Resolve_AliasResolvesCanonicalTemplateAndItsMergedValues()
    {
        var registry = new TemplateRegistry();
        registry.Register(Template("base", ["read"], scopes: ["base-scope"]));
        registry.Register(Template("derived", ["read"], "base", scopes: null));
        registry.RegisterAlias("legacy-derived", "derived");

        var result = registry.Resolve("legacy-derived");
        Assert.Equal("derived", result.Id);
        Assert.Equal(new HashSet<ToolId>([new ToolId("read")]), result.AllowedToolIds);
        Assert.Contains("base-scope", result.AllowedScopes!);
    }

    [Fact]
    public void RegisterAlias_RejectsDuplicateAndTemplateShadowing()
    {
        var registry = new TemplateRegistry();
        registry.Register(Template("one", ["read"]));
        registry.Register(Template("two", ["read"]));
        registry.RegisterAlias("legacy", "one");

        Assert.Throws<InvalidOperationException>(() => registry.RegisterAlias("legacy", "two"));
        Assert.Throws<InvalidOperationException>(() => registry.RegisterAlias("two", "one"));
        Assert.Throws<InvalidOperationException>(() => registry.Register(Template("legacy", ["read"])));
    }

    [Fact]
    public void RegisterAlias_RejectsMissingTarget()
    {
        var registry = new TemplateRegistry();
        Assert.Throws<InvalidOperationException>(() => registry.RegisterAlias("legacy", "missing"));
    }

    [Fact]
    public void RegisterMigration_RecordsAdvisoryMigrationWithoutRedirectingResolve()
    {
        var registry = new TemplateRegistry();
        registry.Register(Template("old", ["read"]));
        registry.Register(Template("new", ["write"]));
        registry.RegisterMigration("old", "new");

        Assert.True(registry.TryGetMigration("old", out var target));
        Assert.Equal("new", target);
        Assert.Equal("old", registry.Resolve("old").Id);
        Assert.False(registry.TryGetMigration("new", out _));
    }

    [Fact]
    public void RegisterMigration_RejectsCyclesSelfMigrationAndMissingTarget()
    {
        var registry = new TemplateRegistry();
        registry.Register(Template("a", ["read"]));
        registry.Register(Template("b", ["read"]));
        registry.Register(Template("c", ["read"]));
        registry.RegisterMigration("a", "b");

        Assert.Throws<InvalidOperationException>(() => registry.RegisterMigration("b", "a"));
        Assert.Throws<ArgumentException>(() => registry.RegisterMigration("c", "c"));
        Assert.Throws<InvalidOperationException>(() => registry.RegisterMigration("c", "missing"));
        Assert.False(registry.TryGetMigration("c", out _));
    }

    [Fact]
    public void Resolve_UnknownIdStillThrowsKeyNotFoundException()
    {
        var registry = new TemplateRegistry();
        Assert.Throws<KeyNotFoundException>(() => registry.Resolve("missing"));
    }

    private static AgentTemplate Template(string id, string[] tools, string? baseId = null, string[]? scopes = null) =>
        new(id, "1", "test", "instructions/lead.md", null, new HashSet<ToolId>(tools.Select(tool => new ToolId(tool))), null,
            new AgentBudget(10, 10, 10, TimeSpan.FromMinutes(1)), new AgentMemoryPolicy(), AgentContextSharingMode.TaskSummary,
            ToolAcknowledgementMode.Automatic, "standard", baseId,
            scopes is null ? null : new HashSet<string>(scopes, StringComparer.Ordinal));
}
