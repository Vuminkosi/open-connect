using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class LiveUIInferenceTests
{
    private static LiveUIInferenceRequest Request => new("Show completed tasks", "session-1", "todo", "openconnect.liveui/v1",
        [new("todo.tab.completed", ["activate"])]);

    [Fact]
    public void Options_DefaultToBrowserFirstAndAllowConfiguredGatewayOnProviderUnavailability()
    {
        var options = new LiveUIInferenceOptions();

        Assert.Equal(LiveUIInferenceMode.BrowserFirst, options.Mode);
        Assert.True(options.Browser.Enabled);
        Assert.True(options.Gateway.Enabled);
        Assert.Equal(LiveUIFallbackPolicy.OnUnavailable, options.Gateway.FallbackPolicy);
        Assert.Equal(TimeSpan.FromSeconds(35), options.Planning.Timeout);
    }

    [Theory]
    [InlineData("Mode")]
    [InlineData("Browser.Provider")]
    [InlineData("Gateway.FallbackPolicy")]
    public void Options_RejectUnsupportedEnumValuesAtStartup(string setting)
    {
        var services = new ServiceCollection();
        services.AddOpenConnectLiveUIInference(options =>
        {
            switch (setting)
            {
                case "Mode":
                    options.Mode = (LiveUIInferenceMode)int.MaxValue;
                    break;
                case "Browser.Provider":
                    options.Browser.Provider = (LiveUIBrowserProvider)int.MaxValue;
                    break;
                case "Gateway.FallbackPolicy":
                    options.Gateway.FallbackPolicy = (LiveUIFallbackPolicy)int.MaxValue;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(setting));
            }
        });

        using var serviceProvider = services.BuildServiceProvider();
        var exception = Assert.Throws<OptionsValidationException>(
            () => serviceProvider.GetRequiredService<IOptions<LiveUIInferenceOptions>>().Value);

        Assert.Contains(exception.Failures, failure => failure.StartsWith(setting, StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsTargetOutsideAuthorizedCapabilities()
    {
        var plan = new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "invented.target")]);
        Assert.False(new LiveUIActionPlanValidator().Validate(Request, plan, 8, out var reason));
        Assert.Equal("capability_not_allowed", reason);
    }

    [Fact]
    public void Mapper_ConvertsValidatedTabActivationToExistingLiveUIAction()
    {
        var manifest = new LiveUIManifest(Request.SchemaVersion, "build", "hash", [new LiveUIRoute("todo", "/", null,
            [new LiveUITarget("todo.tab.completed", LiveUITargetKind.Tab, "Completed", new HashSet<LiveUICapability> { LiveUICapability.Activate })])]);
        var request = Request with { ManifestHash = "hash" };
        var plan = new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]);

        var action = new LiveUIInferenceActionMapper(new LiveUIActionPlanValidator())
            .Map(request, plan, manifest, TimeSpan.FromSeconds(5))[0];

        Assert.Equal(LiveUIActionType.SelectTab, action.Type);
        Assert.Equal("todo.tab.completed", action.TargetId);
        Assert.Equal("hash", action.ManifestHash);
        Assert.Equal(request.SessionId, action.SessionId);
    }

    [Fact]
    public async Task BrowserFirst_UsesGatewayWhenBrowserUnavailableAndPolicyAllowsIt()
    {
        var browser = new FakeProvider(LiveUIInferenceProviderKind.Browser, LiveUIInferenceAvailabilityStatus.Unavailable, null);
        var gateway = new FakeProvider(LiveUIInferenceProviderKind.Gateway, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var settings = new LiveUIInferenceOptions { };
        settings.Gateway.Enabled = true;
        settings.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnUnavailable;
        var router = new LiveUIInferenceRouter([browser, gateway], new LiveUIActionPlanValidator(), Options.Create(settings));

        var result = await router.GeneratePlanAsync(Request);

        Assert.Equal(LiveUIInferenceProviderKind.Gateway, result.Provider);
        Assert.Equal(1, gateway.GenerateCalls);
    }

    [Fact]
    public async Task BrowserProvider_TreatsJavaScriptInteropFailureAsUnavailableSoServerInferenceCanFallback()
    {
        var provider = new BrowserPromptApiInferenceProvider(new FailingJavaScriptRuntime(),
            Options.Create(new LiveUIInferenceOptions()));

        var availability = await provider.GetAvailabilityAsync();

        Assert.Equal(LiveUIInferenceAvailabilityStatus.Unavailable, availability.Status);
        Assert.Equal("browser_prompt_api_unavailable", availability.Reason);
    }

    [Fact]
    public async Task BrowserFirst_UsesLocalWorkerBeforeNormalGatewayInference()
    {
        var browser = new FakeProvider(LiveUIInferenceProviderKind.Browser, LiveUIInferenceAvailabilityStatus.Unavailable, null);
        var worker = new FakeProvider(LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var gateway = new FakeProvider(LiveUIInferenceProviderKind.Gateway, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var settings = new LiveUIInferenceOptions();
        settings.Worker.Enabled = true;
        settings.Gateway.Enabled = true;
        settings.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnUnavailable;
        var router = new LiveUIInferenceRouter([browser, worker, gateway], new LiveUIActionPlanValidator(), Options.Create(settings));

        var result = await router.GeneratePlanAsync(Request);

        Assert.Equal(LiveUIInferenceProviderKind.LocalWorker, result.Provider);
        Assert.Equal(1, worker.GenerateCalls);
        Assert.Equal(0, gateway.GenerateCalls);
    }

    [Fact]
    public async Task BrowserFirst_UsesConfiguredNormalInferenceByDefaultWhenBrowserAndWorkerAreUnavailable()
    {
        var browser = new FakeProvider(LiveUIInferenceProviderKind.Browser, LiveUIInferenceAvailabilityStatus.Unavailable, null);
        var worker = new FakeProvider(LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceAvailabilityStatus.Unavailable, null);
        var gateway = new FakeProvider(LiveUIInferenceProviderKind.Gateway, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var settings = new LiveUIInferenceOptions();
        settings.Worker.Enabled = true;
        var router = new LiveUIInferenceRouter([browser, worker, gateway], new LiveUIActionPlanValidator(), Options.Create(settings));

        var result = await router.GeneratePlanAsync(Request);

        Assert.Equal(LiveUIInferenceProviderKind.Gateway, result.Provider);
        Assert.Equal(1, worker.GetAvailabilityCalls);
        Assert.Equal(1, gateway.GenerateCalls);
    }

    [Fact]
    public async Task PrivacyDenialStopsFallbackToOtherProviders()
    {
        var gateway = new FakeProvider(LiveUIInferenceProviderKind.Gateway, LiveUIInferenceAvailabilityStatus.Available, null,
            new PrivacyBlockedException("blocked"));
        var worker = new FakeProvider(LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var browser = new FakeProvider(LiveUIInferenceProviderKind.Browser, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var settings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.GatewayFirst };
        settings.Gateway.Enabled = true;
        settings.Worker.Enabled = true;
        settings.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnFailure;
        var router = new LiveUIInferenceRouter([gateway, worker, browser], new LiveUIActionPlanValidator(), Options.Create(settings));

        await Assert.ThrowsAsync<PrivacyBlockedException>(() => router.GeneratePlanAsync(Request));
        Assert.Equal(0, worker.GenerateCalls);
        Assert.Equal(0, browser.GenerateCalls);
    }

    [Fact]
    public async Task LocalWorkerProvider_UsesPrivateEndpointAndMapsVersionedStructuredPlan()
    {
        var handler = new RecordingHttpMessageHandler(request =>
        {
            Assert.Equal("Bearer worker-secret", request.Headers.Authorization?.ToString());
            Assert.Equal("v1/liveui/inference/plan", request.RequestUri!.PathAndQuery.TrimStart('/'));
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var requestId = body.RootElement.GetProperty("requestId").GetString();
            var schemaVersion = body.RootElement.GetProperty("schemaVersion").GetString();
            var responseJson = JsonSerializer.Serialize(new
            {
                requestId,
                schemaVersion,
                provider = "qwen3-0.6b",
                status = "completed",
                plan = new
                {
                    schemaVersion,
                    actions = new[] { new { type = "activate", target = "todo.tab.completed", value = (string?)null } }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://liveui-inference:8080/") };
        var provider = new LocalWorkerLiveUIInferenceProvider(client, Options.Create(new LiveUIWorkerClientOptions
        {
            Endpoint = new Uri("http://liveui-inference:8080"),
            BearerToken = "worker-secret"
        }));

        var result = await provider.GeneratePlanAsync(Request);

        Assert.Equal(LiveUIInferenceProviderKind.LocalWorker, result.Provider);
        Assert.Equal("completed", result.Outcome);
        Assert.Equal("todo.tab.completed", Assert.Single(result.Plan!.Actions).TargetId);
    }

    [Fact]
    public async Task BrowserOnly_NeverCallsGateway()
    {
        var browser = new FakeProvider(LiveUIInferenceProviderKind.Browser, LiveUIInferenceAvailabilityStatus.Unavailable, null);
        var gateway = new FakeProvider(LiveUIInferenceProviderKind.Gateway, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var settings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserOnly };
        settings.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnFailure;
        var router = new LiveUIInferenceRouter([browser, gateway], new LiveUIActionPlanValidator(), Options.Create(settings));

        var result = await router.GeneratePlanAsync(Request);

        Assert.Null(result.Plan);
        Assert.Equal(0, gateway.GetAvailabilityCalls);
        Assert.Equal(0, gateway.GenerateCalls);
    }

    private sealed class FakeProvider(LiveUIInferenceProviderKind kind, LiveUIInferenceAvailabilityStatus status,
        LiveUIActionPlan? plan, Exception? failure = null)
        : ILiveUIInferenceProvider
    {
        public int GenerateCalls { get; private set; }
        public int GetAvailabilityCalls { get; private set; }
        public LiveUIInferenceProviderKind Kind => kind;
        public Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
        {
            GetAvailabilityCalls++;
            return Task.FromResult(new LiveUIInferenceAvailability(kind, status));
        }
        public Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default)
        {
            GenerateCalls++;
            if (failure is not null) throw failure;
            return Task.FromResult(new LiveUIInferenceResult(kind, plan, "completed"));
        }
    }

    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class FailingJavaScriptRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromException<TValue>(new JSException("Browser interop is unavailable."));

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromException<TValue>(new JSException("Browser interop is unavailable."));
    }
}
