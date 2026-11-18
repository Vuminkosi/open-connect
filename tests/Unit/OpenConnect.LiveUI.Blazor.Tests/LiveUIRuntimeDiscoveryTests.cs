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
        public Navigation() => Initialize("https://example.test/", "https://example.test/");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }

    private sealed class InspectOnlyAuthorizer : ILiveUIActionAuthorizer
    {
        public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LiveUIAuthorizationDecision(context.Action.Type == LiveUIActionType.Inspect, "policy"));
    }

    private sealed class Browser(bool unavailable = false) : IJSRuntime, IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import") return ValueTask.FromResult((TValue)(object)this);
            if (identifier == "discoverTargets" && unavailable) throw new JSException("unavailable");
            var json = identifier == "discoverTargets"
                ? "[{\"kind\":\"Heading\",\"label\":\"Tasks\",\"role\":\"heading\"}]"
                : "{\"outcome\":\"Completed\",\"code\":\"inspection_verified\",\"postconditionVerified\":true,\"verifiedState\":\"control_visible\"}";
            return ValueTask.FromResult(JsonSerializer.Deserialize<TValue>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
