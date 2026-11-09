using OpenConnect.Abstractions;
using OpenConnect.Agents.Catalog;
using OpenConnect.Agents.Templates;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class RegistryImmutabilityTests
{
    [Fact]
    public void Catalog_rejects_duplicate_agent_and_keeps_original()
    {
        var catalog = new AgentCatalog();
        var id = new AgentId("agent");
        catalog.Register(Definition(id, "Original", "1"));

        Assert.Throws<AgentCatalogException>(() => catalog.Register(Definition(id, "Replacement", "2")));

        var resolved = catalog.Get(id);
        Assert.NotNull(resolved);
        Assert.Equal("Original", resolved.Name);
        Assert.Equal("1", resolved.Version);
    }

    [Fact]
    public void TemplateRegistry_rejects_duplicate_template_and_keeps_original()
    {
        var registry = new TemplateRegistry();
        registry.Register(Template("template", "1", "Original instructions"));
        var before = registry.Resolve("template");

        Assert.Throws<InvalidOperationException>(() => registry.Register(Template("template", "2", "Replacement instructions")));

        var after = registry.Resolve("template");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Description, after.Description);
        Assert.Equal(before.InstructionsPath, after.InstructionsPath);
        Assert.Equal("instructions/original.md", after.InstructionsPath);
    }

    [Fact]
    public void Unrelated_registrations_do_not_change_existing_resolutions()
    {
        var catalog = new AgentCatalog();
        var agentId = new AgentId("agent");
        catalog.Register(Definition(agentId, "Original", "1"));
        var agentSnapshot = catalog.Get(agentId);

        var registry = new TemplateRegistry();
        registry.Register(Template("template", "1", "Original instructions"));
        var templateSnapshot = registry.Resolve("template");

        catalog.Register(Definition(new AgentId("other-agent"), "Other", "1"));
        registry.Register(Template("other-template", "1", "Other instructions"));

        Assert.Equal(agentSnapshot, catalog.Get(agentId));
        var resolvedTemplate = registry.Resolve("template");
        Assert.Equal(templateSnapshot.Id, resolvedTemplate.Id);
        Assert.Equal(templateSnapshot.Version, resolvedTemplate.Version);
        Assert.Equal(templateSnapshot.Description, resolvedTemplate.Description);
        Assert.Equal(templateSnapshot.InstructionsPath, resolvedTemplate.InstructionsPath);
    }

    private static AgentDefinition Definition(AgentId id, string name, string version) =>
        new(id, name) { Version = version };

    private static AgentTemplate Template(string id, string version, string description) =>
        new(id, version, description, $"instructions/{description.Split(' ')[0].ToLowerInvariant()}.md", null,
            new HashSet<ToolId>(), null, new AgentBudget(10, 10, 10, TimeSpan.FromMinutes(1)),
            new AgentMemoryPolicy(), AgentContextSharingMode.TaskSummary, ToolAcknowledgementMode.Automatic,
            "standard", null, new HashSet<string>(StringComparer.Ordinal));
}
