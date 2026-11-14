using System.Collections.Frozen;
using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Blazor;
using OpenConnect.LiveUI.Agents;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class LiveUIBlazorTests
{
    private static readonly LiveUIManifest Manifest = CreateManifest();

    private static LiveUIManifest CreateManifest()
    {
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build-001", string.Empty,
            [new LiveUIRoute("home", "/", "Home", [])]);
        return manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
    }

    [Fact]
    public async Task ManifestState_StoresOnlyValidatedManifest()
    {
        var state = new LiveUIManifestState(new LiveUIManifestValidator());
        state.SetManifest(Manifest);

        Assert.Same(Manifest, await state.GetCurrentAsync());
        Assert.Throws<ArgumentException>(() => state.SetManifest(Manifest with { SchemaVersion = "unknown" }));
        Assert.Same(Manifest, await state.GetCurrentAsync());
    }

    [Fact]
    public void CompiledManifestLoader_CachesValidatedBuildAndFreezesItsCollections()
    {
        var assembly = typeof(LiveUIBlazorTests).Assembly;

        var first = LiveUICompiledManifestLoader.LoadFromEntryAssembly(assembly);
        var second = LiveUICompiledManifestLoader.LoadFromEntryAssembly(assembly);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.IsAssignableFrom<FrozenSet<LiveUICapability>>(first!.Routes[0].Targets[0].Capabilities);
        Assert.True(((IList)first.Routes).IsReadOnly);
        Assert.True(((IList)first.Routes[0].Targets).IsReadOnly);
        Assert.Throws<NotSupportedException>(() => ((IList)first.Routes).Add(first.Routes[0]));
        Assert.Throws<NotSupportedException>(() => ((IList)first.Routes[0].Targets).Clear());
    }

    [Fact]
    public async Task Registration_DeniesActionsWhenHostAuthorizationPolicyIsMissing()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectLiveUIBlazor();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var state = scope.ServiceProvider.GetRequiredService<LiveUIManifestState>();
        state.SetManifest(Manifest);
        var authorizer = scope.ServiceProvider.GetRequiredService<ILiveUIActionAuthorizer>();
        var context = new LiveUIExecutionContext(Manifest, Manifest.Routes[0], null,
            new LiveUIAction("a1", LiveUISchema.CurrentVersion, "s1", Manifest.ContentHash, "home", null,
                LiveUIActionType.Inspect, new LiveUIActionParameters(), TimeSpan.FromSeconds(1)));

        var decision = await authorizer.AuthorizeAsync(context);

        Assert.False(decision.Allowed);
        Assert.Equal("authorization_policy_not_configured", decision.Code);
    }

    [Fact]
    public void ManifestValidator_RejectsExternalAndProtocolRelativeRoutePaths()
    {
        var validator = new LiveUIManifestValidator();

        Assert.Contains("manifest_route_path_invalid",
            validator.Validate(Manifest with { Routes = [Manifest.Routes[0] with { Path = "//external.test" }] }));
        Assert.Contains("manifest_route_path_invalid",
            validator.Validate(Manifest with { Routes = [Manifest.Routes[0] with { Path = "https://external.test" }] }));
    }

    [Fact]
    public async Task BlazorAgentContext_uses_the_active_manifest_route_and_scoped_session_id()
    {
        var navigation = new TestNavigationManager("https://localhost/app/", "https://localhost/app/");
        var state = new LiveUIManifestState(new LiveUIManifestValidator());
        var scopedManifest = Manifest with
        {
            Routes = [Manifest.Routes[0] with { Path = "/app/" }]
        };
        state.SetManifest(scopedManifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(scopedManifest) });
        var accessor = new BlazorLiveUIAgentContextAccessor(navigation, state);

        var context = await accessor.GetCurrentAsync();

        Assert.NotNull(context);
        Assert.Equal("home", context.CurrentRouteId);
        Assert.Equal(32, context.SessionId.Length);
        Assert.Equal(context.SessionId, (await accessor.GetCurrentAsync())?.SessionId);
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string baseUri, string uri) => Initialize(baseUri, uri);
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).ToString();
    }
}
