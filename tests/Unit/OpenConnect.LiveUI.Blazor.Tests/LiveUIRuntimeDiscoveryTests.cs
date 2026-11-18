using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Agents;
using OpenConnect.LiveUI.Blazor;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class LiveUIRuntimeDiscoveryTests
{
    [Fact]
    public async Task Duplicate_headings_are_exposed_with_authorized_section_context()
    {
        const string observations = "[" +
            "{\"kind\":\"Region\",\"label\":\"Desktop setup\",\"role\":\"region\"}," +
            "{\"kind\":\"Region\",\"label\":\"Mobile setup\",\"role\":\"region\"}," +
            "{\"kind\":\"Heading\",\"label\":\"Configuration\",\"role\":\"heading\",\"parentLabel\":\"Desktop setup\"}," +
            "{\"kind\":\"Heading\",\"label\":\"Configuration\",\"role\":\"heading\",\"parentLabel\":\"Mobile setup\"}]";
        var services = new ServiceCollection();
        services.AddOpenConnectLiveUIBlazor(options => options.Discovery.Mode = LiveUIDiscoveryMode.RuntimeOnly);
        services.AddScoped<NavigationManager>(_ => new Navigation());
        services.AddScoped<IJSRuntime>(_ => new Browser(false, observations));
        services.AddScoped<ILiveUIActionAuthorizer, InspectOnlyAuthorizer>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var state = scope.ServiceProvider.GetRequiredService<LiveUIManifestState>();
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "test", "", [new LiveUIRoute("home", "/", "Home", [])]);
        state.SetManifest(manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) });

        var page = await scope.ServiceProvider.GetRequiredService<LiveUIAgentTools>().GetCurrentPageAsync();

        Assert.NotNull(page);
        var headings = page.Targets.Where(target => target.Label == "Configuration").ToArray();
        Assert.Equal(2, headings.Length);
        Assert.Equal(new[] { "Desktop setup", "Mobile setup" }, headings.Select(target => target.ParentLabel).Order(StringComparer.Ordinal));
        Assert.NotEqual(headings[0].TargetId, headings[1].TargetId);
        var currentManifest = (await state.GetCurrentAsync())!;
        Assert.All(currentManifest.Routes[0].Targets.Where(target => target.Kind == LiveUITargetKind.Heading),
            target => Assert.Contains(currentManifest.Routes[0].Targets, parent => parent.Id == target.ParentId && parent.Kind == LiveUITargetKind.Region));
    }

    [Fact]
    public async Task Duplicate_compiled_observations_do_not_exhaust_runtime_target_capacity()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectLiveUIBlazor(options => options.Discovery.MaximumTargetsPerRoute = 2);
        services.AddScoped<NavigationManager>(_ => new Navigation());
        services.AddScoped<IJSRuntime>(_ => new Browser(false,
            "[{\"kind\":\"Heading\",\"label\":\"Compiled\",\"role\":\"heading\"},{\"kind\":\"Heading\",\"label\":\"Tasks\",\"role\":\"heading\"}]"));
        services.AddScoped<ILiveUIActionAuthorizer, InspectOnlyAuthorizer>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var state = scope.ServiceProvider.GetRequiredService<LiveUIManifestState>();
        var compiled = new LiveUITarget("compiled", LiveUITargetKind.Heading, "Compiled",
            new HashSet<LiveUICapability> { LiveUICapability.Inspect }, Role: "heading");
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "test", "", [new LiveUIRoute("home", "/", "Home", [compiled])]);
        state.SetManifest(manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) });

        var page = await scope.ServiceProvider.GetRequiredService<LiveUIAgentTools>().GetCurrentPageAsync();

        Assert.NotNull(page);
        Assert.Equal(2, page.Targets.Count);
        Assert.Contains(page.Targets, target => target.Label == "Tasks");
    }

    [Theory]
    [InlineData("https://example.test/docs/v1/other")]
    [InlineData("https://example.test/docs/v1/INSTALLATION")]
    public async Task Discovered_target_cannot_be_reused_on_another_concrete_parameterized_route(string destination)
    {
        var navigation = new Navigation("https://example.test/docs/v1/installation");
        var services = new ServiceCollection();
        services.AddOpenConnectLiveUIBlazor(options => options.Discovery.Mode = LiveUIDiscoveryMode.RuntimeOnly);
        services.AddScoped<NavigationManager>(_ => navigation);
        services.AddScoped<IJSRuntime>(_ => new Browser());
        services.AddScoped<ILiveUIActionAuthorizer, InspectOnlyAuthorizer>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var state = scope.ServiceProvider.GetRequiredService<LiveUIManifestState>();
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "test", "", [new LiveUIRoute("docs", "/docs/{version}/{slug}", "Docs", [])]);
        state.SetManifest(manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) });
        var tools = scope.ServiceProvider.GetRequiredService<LiveUIAgentTools>();
        var target = Assert.Single((await tools.GetCurrentPageAsync())!.Targets);
        Assert.Equal(LiveUIActionOutcome.Completed, (await tools.InspectAsync(target.TargetId)).Outcome);

        navigation.MoveTo(destination);
        var staleReceipt = await tools.InspectAsync(target.TargetId);

        Assert.Equal(LiveUIActionOutcome.Conflict, staleReceipt.Outcome);
        Assert.Equal("runtime_target_stale", staleReceipt.Code);
        Assert.False(staleReceipt.PostconditionVerified);

        var currentManifest = (await state.GetCurrentAsync())!;
        var directAction = new LiveUIAction(Guid.NewGuid().ToString("N"), LiveUISchema.CurrentVersion,
            "session", currentManifest.ContentHash, "docs", target.TargetId, LiveUIActionType.Inspect,
            new LiveUIActionParameters(), TimeSpan.FromSeconds(1)) { ExpectedPath = new Uri(destination).AbsolutePath };
        var directReceipt = await scope.ServiceProvider.GetRequiredService<LiveUIOrchestrator>().ExecuteAsync(directAction);
        Assert.Equal(LiveUIActionOutcome.Conflict, directReceipt.Outcome);
        Assert.Equal("runtime_target_stale", directReceipt.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_only_uses_discovered_targets_and_fails_closed_when_discovery_is_unavailable(bool unavailable)
    {
        var services = new ServiceCollection();
        services.AddOpenConnectLiveUIBlazor(options => options.Discovery.Mode = LiveUIDiscoveryMode.RuntimeOnly);
        services.AddScoped<NavigationManager>(_ => new Navigation());
        services.AddScoped<IJSRuntime>(_ => new Browser(unavailable));
        services.AddScoped<ILiveUIActionAuthorizer, InspectOnlyAuthorizer>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var state = scope.ServiceProvider.GetRequiredService<LiveUIManifestState>();
        var compiled = new LiveUITarget("compiled", LiveUITargetKind.Heading, "Compiled only",
            new HashSet<LiveUICapability> { LiveUICapability.Inspect });
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "test", "", [new LiveUIRoute("home", "/", "Home", [compiled])]);
        state.SetManifest(manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) });

        var page = await scope.ServiceProvider.GetRequiredService<LiveUIAgentTools>().GetCurrentPageAsync();

        if (unavailable) Assert.Null(page);
        else
        {
            Assert.Equal("Tasks", Assert.Single(page!.Targets).Label);
            Assert.DoesNotContain(page.Targets, target => target.TargetId == "compiled");
        }
    }

    [Fact]
    public async Task Discovered_control_flows_through_manifest_and_authorization_to_runtime()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectLiveUIBlazor();
        services.AddScoped<NavigationManager>(_ => new Navigation());
        services.AddScoped<IJSRuntime>(_ => new Browser());
        services.AddScoped<ILiveUIActionAuthorizer, InspectOnlyAuthorizer>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var state = scope.ServiceProvider.GetRequiredService<LiveUIManifestState>();
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "test", "", [new LiveUIRoute("home", "/", "Home", [])]);
        state.SetManifest(manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) });
        var tools = scope.ServiceProvider.GetRequiredService<LiveUIAgentTools>();

        var page = await tools.GetCurrentPageAsync();
        var target = Assert.Single(page!.Targets);
        Assert.Equal("Tasks", target.Label);
        Assert.Equal([LiveUICapability.Inspect], target.Capabilities);
        var receipt = await tools.InspectAsync(target.TargetId);
        Assert.Equal(LiveUIActionOutcome.Completed, receipt.Outcome);
        Assert.True(receipt.PostconditionVerified);
        var denied = await tools.ExecuteAsync(target.TargetId, "click");
        Assert.Equal(LiveUIActionOutcome.Denied, denied.Outcome);
    }

    private sealed class Navigation : NavigationManager
    {
        public Navigation(string uri = "https://example.test/") => Initialize("https://example.test/", uri);
        public void MoveTo(string uri) { Uri = uri; NotifyLocationChanged(false); }
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }

    private sealed class InspectOnlyAuthorizer : ILiveUIActionAuthorizer
    {
        public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LiveUIAuthorizationDecision(context.Action.Type == LiveUIActionType.Inspect, "policy"));
    }

    private sealed class Browser(bool unavailable = false, string? observations = null) : IJSRuntime, IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import") return ValueTask.FromResult((TValue)(object)this);
            if (identifier == "discoverTargets" && unavailable) throw new JSException("unavailable");
            var json = identifier == "discoverTargets"
                ? observations ?? "[{\"kind\":\"Heading\",\"label\":\"Tasks\",\"role\":\"heading\"}]"
                : "{\"outcome\":\"Completed\",\"code\":\"inspection_verified\",\"postconditionVerified\":true,\"verifiedState\":\"control_visible\"}";
            return ValueTask.FromResult(JsonSerializer.Deserialize<TValue>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
