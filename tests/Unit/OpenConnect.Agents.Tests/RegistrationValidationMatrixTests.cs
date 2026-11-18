// <copyright file="RegistrationValidationMatrixTests.cs">
// Registration validation matrix (CONFIG-006 remainder, AG-MAP-006 SLICE-C2).
// Proves invalid lead/agent/template builder inputs fail fast with field-naming
// diagnostics and leave no partial registration behind. No src changes expected:
// all rejections already exist in AgentCatalog.ValidateDefinition and
// TemplateRegistry.ValidateTemplate. Hosting-builder reuse leg stays BLOCKED
// (MAF-018, no stable package) and is explicitly out of scope here.
// </copyright>

using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Catalog;
using OpenConnect.Agents.Templates;
using Xunit;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Fail-fast matrix for lead/agent/template registration inputs.
/// One fact per invalid class; each asserts the exact exception type plus a
/// field-naming message, and that no partial registration escapes.
/// </summary>
public sealed class RegistrationValidationMatrixTests
{
    [Fact]
    public void RegisterLead_RejectsEmptyId_NothingRegistered()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<AgentCatalogException>(
            () => services.AddOpenConnectLeadAgent(string.Empty, builder => builder.Name = "MatrixLead"));

        Assert.Contains("id", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name", exception.Message, StringComparison.OrdinalIgnoreCase);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentDefinitionRegistry>();
        Assert.Null(registry.Get(new AgentId(string.Empty)));
    }

    [Fact]
    public void RegisterAgent_RejectsEmptyName_NothingRegistered()
    {
        // AgentDefinitionBuilder falls back to the id when Name is blank, so the
        // honest empty-name path is a direct definition: the catalog must reject it.
        var catalog = new AgentCatalog();
        var id = new AgentId("matrix-nameless");
        var definition = new AgentDefinition(id, string.Empty);

        var exception = Assert.Throws<AgentCatalogException>(() => catalog.Register(definition));

        Assert.Contains("id", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(catalog.Get(id));
    }

    [Fact]
    public void RegisterTemplate_RejectsMissingInstructionsPath_NothingRegistered()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentException>(
            () => services.AddOpenConnectAgentTemplate(
                "matrix-tpl-missing",
                builder => builder.Description = "Matrix template"));

        Assert.Contains("instruction path", exception.Message, StringComparison.OrdinalIgnoreCase);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<TemplateRegistry>();
        Assert.Throws<KeyNotFoundException>(() => registry.Resolve("matrix-tpl-missing"));
    }

    [Fact]
    public void RegisterTemplate_RejectsUnsafeInstructionsPath_NothingRegistered()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentException>(
            () => services.AddOpenConnectAgentTemplate(
                "matrix-tpl-unsafe",
                builder =>
                {
                    builder.Description = "Matrix template";
                    builder.InstructionsPath = "../escape.md";
                }));

        Assert.Contains("relative path", exception.Message, StringComparison.OrdinalIgnoreCase);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<TemplateRegistry>();
        Assert.Throws<KeyNotFoundException>(() => registry.Resolve("matrix-tpl-unsafe"));
    }

    [Fact]
    public void RegisterAgent_RejectsNegativeBudget_NothingRegistered()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<AgentCatalogException>(
            () => services.AddOpenConnectAgent(
                "matrix-negbudget",
                builder =>
                {
                    builder.Name = "MatrixNegativeBudget";
                    builder.Budget = new AgentBudget(MaxToolCalls: -1);
                }));

        Assert.Contains("budget", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("negative", exception.Message, StringComparison.OrdinalIgnoreCase);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentDefinitionRegistry>();
        Assert.Null(registry.Get(new AgentId("matrix-negbudget")));
    }

    [Fact]
    public void RegisterAgent_RejectsUndefinedExecutionMode_NothingRegistered()
    {
        // AgentDefinitionBuilder exposes no ExecutionMode setter, so the undefined-enum
        // path goes through a direct definition; the catalog must reject it.
        var catalog = new AgentCatalog();
        var id = new AgentId("matrix-badmode");
        var definition = new AgentDefinition(id, "MatrixBadMode")
        {
            ExecutionMode = (AgentExecutionMode)999,
        };

        var exception = Assert.Throws<AgentCatalogException>(() => catalog.Register(definition));

        Assert.Contains("execution", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("supported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(catalog.Get(id));
    }

    [Fact]
    public void RegisterAgent_RejectsDuplicateId_OriginalPreserved()
    {
        var catalog = new AgentCatalog();
        var id = new AgentId("matrix-duplicate");
        catalog.Register(new AgentDefinition(id, "MatrixOriginal"));

        var exception = Assert.Throws<AgentCatalogException>(
            () => catalog.Register(new AgentDefinition(id, "MatrixCopy")));

        Assert.Contains("matrix-duplicate", exception.Message, StringComparison.Ordinal);
        Assert.Contains("already registered", exception.Message, StringComparison.OrdinalIgnoreCase);

        var preserved = catalog.Get(id);
        Assert.NotNull(preserved);
        Assert.Equal("MatrixOriginal", preserved.Name);
    }
}
