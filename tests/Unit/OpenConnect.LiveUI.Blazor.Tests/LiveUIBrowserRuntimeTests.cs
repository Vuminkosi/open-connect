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

    private static LiveUIManifest CreateManifest()
    {
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build-001", string.Empty,
        [
            new LiveUIRoute("home", "/start", "Start", []),
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
            if (identifier != "import") throw new JSException("Unexpected JavaScript invocation.");
            ImportedModule = module;
            return ValueTask.FromResult((TValue)(object)module);
        }
    }

    private sealed class TestJsModule(string routeResult) : IJSObjectReference
    {
        public string? LastIdentifier { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            LastIdentifier = identifier;
            if (identifier != "awaitRoute") throw new JSException("Unexpected module invocation.");
            var value = JsonSerializer.Deserialize<TValue>(routeResult, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return ValueTask.FromResult(value!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
