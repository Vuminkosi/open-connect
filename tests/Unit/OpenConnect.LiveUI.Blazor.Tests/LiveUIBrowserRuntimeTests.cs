using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Blazor;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class LiveUIBrowserRuntimeTests
{
    [Fact]
    public async Task NavigateAsync_uses_manifest_route_and_returns_browser_readiness_receipt()
    {
        var manifest = CreateManifest();
        var currentRoute = manifest.Routes.Single(route => route.Id == "home");
        var destination = manifest.Routes.Single(route => route.Id == "tasks");
        var action = new LiveUIAction("action-1", LiveUISchema.CurrentVersion, "session-1", manifest.ContentHash,
            currentRoute.Id, null, LiveUIActionType.Navigate,
            new LiveUIActionParameters(DestinationRouteId: destination.Id), TimeSpan.FromSeconds(5));
        var context = new LiveUIExecutionContext(manifest, currentRoute, null, action);
        var navigation = new TestNavigationManager("https://localhost/", "https://localhost/start");
        var module = new TestJsModule("""{"outcome":"Completed","code":"route_ready","postconditionVerified":true,"verifiedState":"ready"}""");
        var javascript = new TestJsRuntime(module);
        await using var runtime = new LiveUIBrowserRuntime(javascript, navigation,
            Options.Create(new LiveUIOptions()));

        var result = await runtime.ExecuteAsync(context);

        Assert.Equal("https://localhost/tasks", navigation.Uri);
        Assert.Equal(LiveUIActionOutcome.Completed, result.Outcome);
        Assert.Equal("route_ready", result.Code);
        Assert.True(result.PostconditionVerified);
        Assert.Equal("ready", result.VerifiedState);
        Assert.Equal("awaitRoute", module.LastIdentifier);
    }

    [Fact]
    public async Task NavigateAsync_does_not_leave_the_page_while_a_modal_is_open()
    {
        var manifest = CreateManifest();
        var route = manifest.Routes.Single(item => item.Id == "home");
        var action = new LiveUIAction("action-modal", LiveUISchema.CurrentVersion, "session-1", manifest.ContentHash,
            route.Id, null, LiveUIActionType.Navigate,
            new LiveUIActionParameters(DestinationRouteId: "tasks"), TimeSpan.FromSeconds(5));
        var navigation = new TestNavigationManager("https://localhost/", "https://localhost/start");
        var module = new TestJsModule("{}", navigationAllowed: false);
        await using var runtime = new LiveUIBrowserRuntime(new TestJsRuntime(module), navigation, Options.Create(new LiveUIOptions()));

        var result = await runtime.ExecuteAsync(new LiveUIExecutionContext(manifest, route, null, action));

        Assert.Equal("https://localhost/start", navigation.Uri);
        Assert.Equal(LiveUIActionOutcome.Denied, result.Outcome);
        Assert.Equal("modal_open", result.Code);
        Assert.False(result.PostconditionVerified);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_a_browser_route_that_differs_from_the_bound_route()
    {
        var manifest = CreateManifest();
        var route = manifest.Routes.Single(item => item.Id == "home");
        var action = new LiveUIAction("action-2", LiveUISchema.CurrentVersion, "session-1", manifest.ContentHash,
            route.Id, null, LiveUIActionType.Inspect, new LiveUIActionParameters(), TimeSpan.FromSeconds(5));
        var context = new LiveUIExecutionContext(manifest, route, null, action);
        var navigation = new TestNavigationManager("https://localhost/", "https://localhost/other");
        var module = new TestJsModule("{}");
        var javascript = new TestJsRuntime(module);
        await using var runtime = new LiveUIBrowserRuntime(javascript, navigation,
            Options.Create(new LiveUIOptions()));

        var result = await runtime.ExecuteAsync(context);

        Assert.Equal(LiveUIActionOutcome.Conflict, result.Outcome);
        Assert.Equal("route_mismatch", result.Code);
        Assert.Null(javascript.ImportedModule);
    }

    [Fact]
    public async Task ExecuteAsync_binds_parameterized_route_to_the_observed_concrete_path()
    {
        var target = new LiveUITarget("docs.heading", LiveUITargetKind.Heading, "Installation",
            new HashSet<LiveUICapability> { LiveUICapability.Inspect });
        var manifest = CreateManifest() with { Routes = [new LiveUIRoute("docs", "/docs/{Version}/{Slug}", "Document", [target])] };
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var route = Assert.Single(manifest.Routes);
        var action = new LiveUIAction("action-dynamic", LiveUISchema.CurrentVersion, "session-1", manifest.ContentHash,
            route.Id, target.Id, LiveUIActionType.Inspect, new LiveUIActionParameters(), TimeSpan.FromSeconds(5))
        { ExpectedPath = "/docs/v1/installation" };
        var navigation = new TestNavigationManager("https://localhost/", "https://localhost/docs/v1/installation");
        var module = new TestJsModule("{}", """{"outcome":"Completed","code":"inspection_verified","postconditionVerified":true}""");
        await using var runtime = new LiveUIBrowserRuntime(new TestJsRuntime(module), navigation, Options.Create(new LiveUIOptions()));

        var result = await runtime.ExecuteAsync(new LiveUIExecutionContext(manifest, route, target, action));

        Assert.Equal(LiveUIActionOutcome.Completed, result.Outcome);
        var browserContext = Assert.IsType<LiveUIExecutionContext>(module.LastArguments![0]);
        Assert.Equal("/docs/v1/installation", browserContext.Route.Path);
        Assert.Equal("/docs/{Version}/{Slug}", browserContext.Manifest.Routes[0].Path);
    }

    [Fact]
    public async Task ExecuteAsync_preserves_encoded_literal_route_support()
    {
        var manifest = CreateManifest() with { Routes = [new LiveUIRoute("cafe", "/caf\u00e9", "Cafe", [])] };
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var route = Assert.Single(manifest.Routes);
        var action = new LiveUIAction("action-unicode", LiveUISchema.CurrentVersion, "session-1", manifest.ContentHash,
            route.Id, null, LiveUIActionType.Inspect, new LiveUIActionParameters(), TimeSpan.FromSeconds(5));
        var navigation = new TestNavigationManager("https://localhost/", "https://localhost/caf%C3%A9");
        var module = new TestJsModule("{}", """{"outcome":"Completed","code":"inspection_verified","postconditionVerified":true}""");
        await using var runtime = new LiveUIBrowserRuntime(new TestJsRuntime(module), navigation, Options.Create(new LiveUIOptions()));

        var result = await runtime.ExecuteAsync(new LiveUIExecutionContext(manifest, route, null, action));

        Assert.Equal(LiveUIActionOutcome.Completed, result.Outcome);
    }

    [Theory]
    [InlineData(null, "/docs/v1/installation")]
    [InlineData("/docs/v1/installation", "/docs/v1/security")]
    [InlineData("/other/v1/installation", "/other/v1/installation")]
    public async Task ExecuteAsync_rejects_missing_or_stale_dynamic_route_bindings(string? expectedPath, string actualPath)
    {
        var manifest = CreateManifest() with { Routes = [new LiveUIRoute("docs", "/docs/{Version}/{Slug}", "Document", [])] };
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var route = Assert.Single(manifest.Routes);
        var action = new LiveUIAction("action-stale", LiveUISchema.CurrentVersion, "session-1", manifest.ContentHash,
            route.Id, null, LiveUIActionType.Inspect, new LiveUIActionParameters(), TimeSpan.FromSeconds(5))
        { ExpectedPath = expectedPath };
        var navigation = new TestNavigationManager("https://localhost/", "https://localhost" + actualPath);
        var javascript = new TestJsRuntime(new TestJsModule("{}"));
        await using var runtime = new LiveUIBrowserRuntime(javascript, navigation, Options.Create(new LiveUIOptions()));

        var result = await runtime.ExecuteAsync(new LiveUIExecutionContext(manifest, route, null, action));

        Assert.Equal(LiveUIActionOutcome.Conflict, result.Outcome);
        Assert.Null(javascript.ImportedModule);
    }

    [Fact]
    public async Task NavigateAsync_rejects_a_stale_source_route_before_navigation()
    {
        var manifest = CreateManifest();
        var route = manifest.Routes.Single(item => item.Id == "home");
        var action = new LiveUIAction("action-stale-navigation", LiveUISchema.CurrentVersion, "session-1", manifest.ContentHash,
            route.Id, null, LiveUIActionType.Navigate, new LiveUIActionParameters(DestinationRouteId: "tasks"), TimeSpan.FromSeconds(5));
        var navigation = new TestNavigationManager("https://localhost/", "https://localhost/other");
        await using var runtime = new LiveUIBrowserRuntime(new TestJsRuntime(new TestJsModule("{}")), navigation, Options.Create(new LiveUIOptions()));

        var result = await runtime.ExecuteAsync(new LiveUIExecutionContext(manifest, route, null, action));

        Assert.Equal(LiveUIActionOutcome.Conflict, result.Outcome);
        Assert.Equal("https://localhost/other", navigation.Uri);
    }

    [Fact]
    public async Task NavigateAsync_does_not_navigate_to_an_unresolved_route_template()
    {
        var manifest = CreateManifest() with
        {
            Routes = [
            new LiveUIRoute("home", "/start", "Start", []),
            new LiveUIRoute("docs", "/docs/{Version}/{Slug}", "Document", [])]
        };
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var route = manifest.Routes[0];
        var action = new LiveUIAction("action-template", LiveUISchema.CurrentVersion, "session-1", manifest.ContentHash,
            route.Id, null, LiveUIActionType.Navigate, new LiveUIActionParameters(DestinationRouteId: "docs"), TimeSpan.FromSeconds(5));
        var navigation = new TestNavigationManager("https://localhost/", "https://localhost/start");
        await using var runtime = new LiveUIBrowserRuntime(new TestJsRuntime(new TestJsModule("{}")), navigation, Options.Create(new LiveUIOptions()));

        var result = await runtime.ExecuteAsync(new LiveUIExecutionContext(manifest, route, null, action));

        Assert.Equal(LiveUIActionOutcome.Denied, result.Outcome);
        Assert.Equal("https://localhost/start", navigation.Uri);
    }

    [Fact]
    public async Task ExecuteAsync_returns_validated_browser_measurements()
    {
        var manifest = CreateManifest([
            new LiveUITarget("target.list", LiveUITargetKind.Region, "Task list",
                new HashSet<LiveUICapability> { LiveUICapability.Inspect }, Selector: "section")
        ]);
        var route = manifest.Routes.Single(item => item.Id == "home");
        var target = Assert.Single(route.Targets);
        var action = new LiveUIAction("action-3", LiveUISchema.CurrentVersion, "session-1", manifest.ContentHash,
            route.Id, target.Id, LiveUIActionType.Inspect, new LiveUIActionParameters(), TimeSpan.FromSeconds(5));
        var context = new LiveUIExecutionContext(manifest, route, target, action);
        var navigation = new TestNavigationManager("https://localhost/", "https://localhost/start");
        var module = new TestJsModule("{}", """{"outcome":"Completed","code":"inspection_verified","postconditionVerified":true,"verifiedState":"control_visible","measurements":{"targetResolutionMilliseconds":12.5,"browserActionMilliseconds":3.25}}""");
        await using var runtime = new LiveUIBrowserRuntime(new TestJsRuntime(module), navigation,
            Options.Create(new LiveUIOptions()));

        var result = await runtime.ExecuteAsync(context);

        Assert.Equal(12.5, result.Measurements?.TargetResolutionMilliseconds);
        Assert.Equal(3.25, result.Measurements?.BrowserActionMilliseconds);
        Assert.Equal("execute", module.LastIdentifier);
    }

    [Fact]
    public async Task ExecuteAsync_discards_invalid_browser_measurements()
    {
        var manifest = CreateManifest([
            new LiveUITarget("target.list", LiveUITargetKind.Region, "Task list",
                new HashSet<LiveUICapability> { LiveUICapability.Inspect }, Selector: "section")
        ]);
        var route = manifest.Routes.Single(item => item.Id == "home");
        var target = Assert.Single(route.Targets);
        var action = new LiveUIAction("action-4", LiveUISchema.CurrentVersion, "session-1", manifest.ContentHash,
            route.Id, target.Id, LiveUIActionType.Inspect, new LiveUIActionParameters(), TimeSpan.FromSeconds(5));
        var context = new LiveUIExecutionContext(manifest, route, target, action);
        var navigation = new TestNavigationManager("https://localhost/", "https://localhost/start");
        var module = new TestJsModule("{}", """{"outcome":"Completed","code":"inspection_verified","postconditionVerified":true,"verifiedState":"control_visible","measurements":{"targetResolutionMilliseconds":120001,"browserActionMilliseconds":-3}}""");
        await using var runtime = new LiveUIBrowserRuntime(new TestJsRuntime(module), navigation,
            Options.Create(new LiveUIOptions()));

        var result = await runtime.ExecuteAsync(context);

        Assert.Null(result.Measurements);
    }

    private static LiveUIManifest CreateManifest(IReadOnlyList<LiveUITarget>? homeTargets = null)
    {
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build-001", string.Empty,
        [
            new LiveUIRoute("home", "/start", "Start", homeTargets ?? []),
            new LiveUIRoute("tasks", "/tasks", "Tasks", [])
        ]);
        return manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string baseUri, string uri) => Initialize(baseUri, uri);

        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();

        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).ToString();
    }

    private sealed class TestJsRuntime(TestJsModule module) : IJSRuntime
    {
        public IJSObjectReference? ImportedModule { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "import")
                throw new JSException("Unexpected JavaScript invocation.");
            ImportedModule = module;
            return ValueTask.FromResult((TValue)(object)module);
        }
    }

    private sealed class TestJsModule(string routeResult, string actionResult = "{}", bool navigationAllowed = true) : IJSObjectReference
    {
        public string? LastIdentifier { get; private set; }
        public object?[]? LastArguments { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            LastIdentifier = identifier;
            LastArguments = args;
            var response = identifier switch
            {
                "awaitRoute" => routeResult,
                "canNavigate" => navigationAllowed ? "true" : "false",
                "execute" => actionResult,
                _ => throw new JSException("Unexpected module invocation.")
            };
            var value = JsonSerializer.Deserialize<TValue>(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return ValueTask.FromResult(value!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
