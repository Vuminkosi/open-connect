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

    [Fact]
    public async Task BlazorAgentContext_resolves_a_parameterized_document_route()
    {
        var navigation = new TestNavigationManager("https://localhost/", "https://localhost/docs/v1/installation");
        var state = new LiveUIManifestState(new LiveUIManifestValidator());
        var manifest = Manifest with
        {
            Routes = [new LiveUIRoute("docs.page", "/docs/{Version}/{Slug}", "Document", [])]
        };
        state.SetManifest(manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) });
        var accessor = new BlazorLiveUIAgentContextAccessor(navigation, state);

        var context = await accessor.GetCurrentAsync();

        Assert.NotNull(context);
        Assert.Equal("docs.page", context.CurrentRouteId);
        Assert.Equal("/docs/v1/installation", context.CurrentPath);
    }

    [Fact]
    public void Concrete_path_is_not_exposed_in_action_serialization()
    {
        var action = new LiveUIAction("action", LiveUISchema.CurrentVersion, "session", Manifest.ContentHash,
            "home", null, LiveUIActionType.Inspect, new LiveUIActionParameters(), TimeSpan.FromSeconds(1));
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

        Assert.DoesNotContain("expectedPath", System.Text.Json.JsonSerializer.Serialize(action, options), StringComparison.Ordinal);
        Assert.DoesNotContain("expectedPath",
            System.Text.Json.JsonSerializer.Serialize(action with { ExpectedPath = "/docs/v1/installation" }, options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlazorAgentContext_prefers_a_literal_route_over_a_parameter_route()
    {
        var navigation = new TestNavigationManager("https://localhost/", "https://localhost/docs/search");
        var state = new LiveUIManifestState(new LiveUIManifestValidator());
        var manifest = Manifest with
        {
            Routes = [
            new LiveUIRoute("docs.dynamic", "/docs/{Slug}", "Document", []),
            new LiveUIRoute("docs.search", "/docs/search", "Search", [])]
        };
        state.SetManifest(manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) });

        var context = await new BlazorLiveUIAgentContextAccessor(navigation, state).GetCurrentAsync();

        Assert.Equal("docs.search", context?.CurrentRouteId);
    }

    [Theory]
    [InlineData("/docs/{First}", "/docs/{Second}", "/docs/installation")]
    [InlineData("/docs/{Slug:int}", "/other", "/docs/123")]
    [InlineData("/docs/{Slug}", "/other", "/docs/one%2Ftwo")]
    public async Task BlazorAgentContext_fails_closed_for_ambiguous_or_unsupported_routes(string first, string second, string path)
    {
        var navigation = new TestNavigationManager("https://localhost/", "https://localhost" + path);
        var state = new LiveUIManifestState(new LiveUIManifestValidator());
        var manifest = Manifest with
        {
            Routes = [
            new LiveUIRoute("first", first, "First", []), new LiveUIRoute("second", second, "Second", [])]
        };
        state.SetManifest(manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) });

        Assert.Null(await new BlazorLiveUIAgentContextAccessor(navigation, state).GetCurrentAsync());
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string baseUri, string uri) => Initialize(baseUri, uri);
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).ToString();
    }
}
