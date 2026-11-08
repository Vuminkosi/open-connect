using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Core;
using OpenConnect.Tools;
using Xunit;

namespace OpenConnect.Core.Tests;

public sealed class At001CoreWithoutNavigatorTests
{
    [Fact]
    public async Task Core_lead_and_tool_discovery_work_without_navigator_tts_or_playwright()
    {
        // NAV-AT-001: core lead registration and tool discovery must not require Navigator/TTS/Playwright.
        var services = new ServiceCollection();
        services.AddOpenConnect();
        services.AddSingleton<IToolAccessPolicy, AllowAllToolPolicy>();
        services.AddSingleton<ToolAuthorizationService>();
        services.AddSingleton<ToolCatalog>();
        services.AddSingleton<ToolBroker>();

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ImplementationType?.Assembly.GetName().Name is { } name &&
            (name.Contains("Navigator", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Playwright", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Tts", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("TTS", StringComparison.Ordinal)));

        var invoked = false;
        using var provider = services.BuildServiceProvider();
        var lead = provider.GetRequiredService<IAgentDefinitionRegistry>().GetAll().Single();
        Assert.Equal("lead", lead.Id.Value);

        var catalog = provider.GetRequiredService<ToolCatalog>();
        var toolId = new ToolId("sample");
        const string schema = """{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""";
        catalog.Register(new ToolRegistration(new ToolDescriptor(toolId, "Sample", lead.Id), schema,
            (args, _) => { invoked = true; return ValueTask.FromResult(args.Clone()); }), [lead.Id]);

        var principal = new PrincipalId(Ulid.NewUlid());
        var tenant = new TenantId(Ulid.NewUlid());
        var task = new TaskId(Ulid.NewUlid());
        var discovered = await catalog.DiscoverAsync(lead.Id, principal, tenant, task);
        Assert.Contains(discovered, descriptor => descriptor.Id == toolId);

        using var arguments = JsonDocument.Parse("""{"value":"works"}""");
        var result = await provider.GetRequiredService<ToolBroker>().InvokeAsync(
            new ToolAccessRequest(lead.Id, toolId, principal, tenant, task), arguments.RootElement);

        Assert.True(invoked);
        Assert.Equal("works", result.GetProperty("value").GetString());
    }

    private sealed class AllowAllToolPolicy : IToolAccessPolicy
    {
        public ValueTask<bool> IsAllowedAsync(ToolAccessRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }
}
