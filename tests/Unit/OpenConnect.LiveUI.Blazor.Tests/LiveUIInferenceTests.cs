using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

[CollectionDefinition("LiveUI inference metrics", DisableParallelization = true)]
public sealed class LiveUIInferenceMetricsCollection;

[Collection("LiveUI inference metrics")]
public sealed class LiveUIInferenceTests
{
    private static LiveUIInferenceRequest Request => new("Show completed tasks", "session-1", "todo", "openconnect.liveui/v1",
        [new("todo.tab.completed", ["activate"])]);

    [Fact]
    public void Capability_labels_are_optional_and_round_trip_through_the_wire_contract()
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacy = JsonSerializer.Deserialize<LiveUIInferenceCapability>("{\"targetId\":\"todo\",\"actions\":[\"inspect\"]}", jsonOptions);
        Assert.NotNull(legacy);
        Assert.Null(legacy.Label);
        var labeled = legacy with { Label = "Task list" };
        var roundTrip = JsonSerializer.Deserialize<LiveUIInferenceCapability>(JsonSerializer.Serialize(labeled, jsonOptions), jsonOptions);
        Assert.Equal("Task list", roundTrip!.Label);
        Assert.Equal(legacy.TargetId, roundTrip.TargetId);
        Assert.Equal(legacy.Actions, roundTrip.Actions);
    }

    [Fact]
    public void Options_DefaultToLocalFirstWithProvidersDisabled()
    {
        var options = new LiveUIInferenceOptions();

        Assert.Equal(LiveUIInferenceMode.BrowserFirst, options.Mode);
        Assert.False(options.Browser.Enabled);
        Assert.False(options.Worker.Enabled);
        Assert.False(options.Gateway.Enabled);
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
    public void Validator_RejectsMalformedProviderPlansAndAmbiguousCapabilities()
    {
        var validator = new LiveUIActionPlanValidator();
        var validPlan = new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]);
        var duplicate = Request with { Capabilities = [new("todo.tab.completed", ["activate"]), new("todo.tab.completed", ["highlight"])] };
        Assert.False(validator.Validate(duplicate, validPlan, 8, out _));
        Assert.False(validator.Validate(Request with { Capabilities = null! }, validPlan, 8, out _));
        Assert.False(validator.Validate(Request with { Capabilities = [null!] }, validPlan, 8, out _));
        Assert.False(validator.Validate(Request with { Capabilities = [new(null!, ["activate"])] }, validPlan, 8, out _));
        Assert.False(validator.Validate(Request with { Capabilities = [new("todo.tab.completed", null!)] }, validPlan, 8, out _));
        Assert.False(validator.Validate(Request, new LiveUIActionPlan(Request.SchemaVersion, null!), 8, out _));
        Assert.False(validator.Validate(Request, new LiveUIActionPlan(Request.SchemaVersion, [null!]), 8, out _));
        Assert.False(validator.Validate(Request, new LiveUIActionPlan(Request.SchemaVersion, [new(null!, "todo.tab.completed")]), 8, out _));
        Assert.False(validator.Validate(Request, new LiveUIActionPlan(Request.SchemaVersion, [new("activate", null!)]), 8, out _));
    }

    [Fact]
    public void InspectionPlan_RequiresAuthorizedCapabilityAndMapsToInspection()
    {
        var request = new LiveUIInferenceRequest("Explain the current view", "session-1", "todo", "openconnect.liveui/v1",
            [new("todo.region", ["inspect"])], ManifestHash: "hash");
        var manifest = new LiveUIManifest(request.SchemaVersion, "build", "hash", [new LiveUIRoute("todo", "/", null,
            [new LiveUITarget("todo.region", LiveUITargetKind.Region, "Tasks", new HashSet<LiveUICapability> { LiveUICapability.Inspect })])]);
        var plan = new LiveUIActionPlan(request.SchemaVersion, [new("inspect", "todo.region")]);
        var validator = new LiveUIActionPlanValidator();

        Assert.True(validator.Validate(request, plan, 8, out _));
        Assert.Equal(LiveUIActionType.Inspect, Assert.Single(new LiveUIInferenceActionMapper(validator)
            .Map(request, plan, manifest, TimeSpan.FromSeconds(5))).Type);
        var denied = request with { Capabilities = [new("todo.region", ["highlight"])] };
        Assert.False(validator.Validate(denied, plan, 8, out var reason));
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

    [Theory]
    [InlineData(LiveUIInferenceMode.Automatic)]
    [InlineData(LiveUIInferenceMode.GatewayFirst)]
    public async Task AutomaticAndDefaultGatewayFirst_TryGatewayBeforeBrowser(LiveUIInferenceMode mode)
    {
        var browser = new FakeProvider(LiveUIInferenceProviderKind.Browser, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var gateway = new FakeProvider(LiveUIInferenceProviderKind.Gateway, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var settings = new LiveUIInferenceOptions { Mode = mode };
        settings.Browser.Enabled = true;
        settings.Gateway.Enabled = true;
        var router = new LiveUIInferenceRouter([browser, gateway], new LiveUIActionPlanValidator(), Options.Create(settings));

        var result = await router.GeneratePlanAsync(Request);

        Assert.Equal(LiveUIInferenceProviderKind.Gateway, result.Provider);
        Assert.Equal(1, gateway.GenerateCalls);
        Assert.Equal(0, browser.GetAvailabilityCalls);
    }

    [Fact]
    public async Task Router_RecordsProviderAvailabilityFallbackAndDurationWithoutUserContentTags()
    {
        var browser = new FakeProvider(LiveUIInferenceProviderKind.Browser, LiveUIInferenceAvailabilityStatus.Unavailable, null);
        var worker = new FakeProvider(LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var settings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserFirst };
        settings.Browser.Enabled = true;
        settings.Worker.Enabled = true;
        var router = new LiveUIInferenceRouter([browser, worker], new LiveUIActionPlanValidator(), Options.Create(settings));
        var measurements = new ConcurrentQueue<(string Name, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "OpenConnect.LiveUI.Inference")
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, tags.ToArray())));
        listener.Start();

        var result = await router.GeneratePlanAsync(Request);

        Assert.Equal(LiveUIInferenceProviderKind.LocalWorker, result.Provider);
        Assert.Contains(measurements, measurement => measurement.Name == "openconnect.liveui.inference.requests" &&
            measurement.Tags.Any(tag => tag.Key == "provider" && Equals(tag.Value, "local_worker")) &&
            measurement.Tags.Any(tag => tag.Key == "outcome" && Equals(tag.Value, "completed")));
        Assert.Contains(measurements, measurement => measurement.Name == "openconnect.liveui.inference.duration");
        Assert.Contains(measurements, measurement => measurement.Name == "openconnect.liveui.inference.fallbacks" &&
            measurement.Tags.Any(tag => tag.Key == "reason" && Equals(tag.Value, "inference_unavailable")));
        Assert.Contains(measurements, measurement => measurement.Name == "openconnect.liveui.inference.model_availability" &&
            measurement.Tags.Any(tag => tag.Key == "status" && Equals(tag.Value, "unavailable")));

        var serializedTags = string.Join(" ", measurements.SelectMany(measurement => measurement.Tags)
            .Select(tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain(Request.Intent, serializedTags, StringComparison.Ordinal);
        Assert.DoesNotContain("todo.tab.completed", serializedTags, StringComparison.Ordinal);
        Assert.DoesNotContain(Request.SessionId, serializedTags, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Router_RecordsInvalidPlanAndFailureClassification()
    {
        var browser = new FakeProvider(LiveUIInferenceProviderKind.Browser, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "invented.target")]));
        var browserSettings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserFirst };
        browserSettings.Browser.Enabled = true;
        var router = new LiveUIInferenceRouter([browser], new LiveUIActionPlanValidator(), Options.Create(browserSettings));
        var measurements = new ConcurrentQueue<(string Name, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "OpenConnect.LiveUI.Inference")
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, tags.ToArray())));
        listener.Start();

        var result = await router.GeneratePlanAsync(Request);

        Assert.Equal("invalid_plan", result.Outcome);
        Assert.Contains(measurements, measurement => measurement.Name == "openconnect.liveui.inference.invalid_plans");
        Assert.Contains(measurements, measurement => measurement.Name == "openconnect.liveui.inference.failures" &&
            measurement.Tags.Any(tag => tag.Key == "error.type" && Equals(tag.Value, "invalid_plan")));
    }

    [Fact]
    public async Task Router_RecordsCallerCancellationWithoutClassifyingItAsFailure()
    {
        var provider = new WaitingProvider();
        var browserSettings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserFirst };
        browserSettings.Browser.Enabled = true;
        var router = new LiveUIInferenceRouter([provider], new LiveUIActionPlanValidator(), Options.Create(browserSettings));
        var measurements = new ConcurrentQueue<(string Name, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "OpenConnect.LiveUI.Inference")
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, tags.ToArray())));
        listener.Start();
        using var cancellation = new CancellationTokenSource();

        var pending = router.GeneratePlanAsync(Request, cancellation.Token);
        await provider.AvailabilityRequested;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Contains(measurements, measurement => measurement.Name == "openconnect.liveui.inference.requests" &&
            measurement.Tags.Any(tag => tag.Key == "outcome" && Equals(tag.Value, "cancelled")));
        Assert.DoesNotContain(measurements, measurement => measurement.Name == "openconnect.liveui.inference.failures");
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

    [Theory]
    [InlineData(false, "completed")]
    [InlineData(true, "browser_response_mismatch")]
    public async Task BrowserProvider_RequiresResponseToMatchSessionExecutionAndManifest(
        bool mismatchRequestId, string expectedOutcome)
    {
        var request = Request with
        {
            ManifestHash = "manifest-hash",
            RequestId = "request-id",
            ExecutionId = "execution-id"
        };
        var provider = new BrowserPromptApiInferenceProvider(
            new BrowserInferenceJavaScriptRuntime(mismatchRequestId), Options.Create(new LiveUIInferenceOptions()));

        var result = await provider.GeneratePlanAsync(request);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedOutcome == "completed", result.Plan is not null);
        if (result.Plan is not null)
            Assert.Equal("todo.tab.completed", Assert.Single(result.Plan.Actions).TargetId);
    }

    [Fact]
    public async Task BrowserProvider_CancellationCancelsPendingPromptAndRethrows()
    {
        var request = Request with
        {
            ManifestHash = "manifest-hash",
            RequestId = "request-id",
            ExecutionId = "execution-id"
        };
        var runtime = new CancellationAwareBrowserInferenceRuntime();
        var provider = new BrowserPromptApiInferenceProvider(runtime, Options.Create(new LiveUIInferenceOptions()));
        using var cancellation = new CancellationTokenSource();

        var pending = provider.GeneratePlanAsync(request, cancellation.Token);
        await runtime.Module.GenerateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(runtime.Module.GenerateCancellationObserved);
        Assert.Equal(1, runtime.Module.CancelCalls);
    }

    [Fact]
    public async Task BrowserFirst_UsesLocalWorkerBeforeNormalGatewayInference()
    {
        var browser = new FakeProvider(LiveUIInferenceProviderKind.Browser, LiveUIInferenceAvailabilityStatus.Unavailable, null);
        var worker = new FakeProvider(LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var gateway = new FakeProvider(LiveUIInferenceProviderKind.Gateway, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var settings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserFirst };
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
    public async Task DisabledExternalFallbackStillAllowsEnabledLocalWorkerFallback()
    {
        var browser = new FakeProvider(LiveUIInferenceProviderKind.Browser, LiveUIInferenceAvailabilityStatus.Unavailable, null);
        var worker = new FakeProvider(LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var gateway = new FakeProvider(LiveUIInferenceProviderKind.Gateway, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var settings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserFirst };
        settings.Worker.Enabled = true;
        settings.Gateway.Enabled = true;
        settings.Gateway.FallbackPolicy = LiveUIFallbackPolicy.Disabled;
        var router = new LiveUIInferenceRouter([browser, worker, gateway], new LiveUIActionPlanValidator(), Options.Create(settings));

        var result = await router.GeneratePlanAsync(Request);

        Assert.Equal(LiveUIInferenceProviderKind.LocalWorker, result.Provider);
        Assert.Equal(1, worker.GenerateCalls);
        Assert.Equal(0, gateway.GetAvailabilityCalls);
        Assert.Equal(0, gateway.GenerateCalls);
    }

    [Fact]
    public async Task BrowserModelThatRequiresDownloadFallsBackToWorkerWithoutInvokingBrowserInference()
    {
        var browser = new FakeProvider(LiveUIInferenceProviderKind.Browser, LiveUIInferenceAvailabilityStatus.Downloadable, null);
        var worker = new FakeProvider(LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var gateway = new FakeProvider(LiveUIInferenceProviderKind.Gateway, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var settings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserFirst };
        settings.Worker.Enabled = true;
        settings.Gateway.Enabled = true;
        settings.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnUnavailable;
        var router = new LiveUIInferenceRouter([browser, worker, gateway], new LiveUIActionPlanValidator(), Options.Create(settings));

        var result = await router.GeneratePlanAsync(Request);

        Assert.Equal(LiveUIInferenceProviderKind.LocalWorker, result.Provider);
        Assert.Equal(0, browser.GenerateCalls);
        Assert.Equal(1, worker.GenerateCalls);
        Assert.Equal(0, gateway.GenerateCalls);
    }

    [Fact]
    public async Task BrowserFirst_UsesExplicitGatewayWhenBrowserAndWorkerAreUnavailable()
    {
        var browser = new FakeProvider(LiveUIInferenceProviderKind.Browser, LiveUIInferenceAvailabilityStatus.Unavailable, null);
        var worker = new FakeProvider(LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceAvailabilityStatus.Unavailable, null);
        var gateway = new FakeProvider(LiveUIInferenceProviderKind.Gateway, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var settings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserFirst };
        settings.Worker.Enabled = true;
        settings.Gateway.Enabled = true;
        var router = new LiveUIInferenceRouter([browser, worker, gateway], new LiveUIActionPlanValidator(), Options.Create(settings));

        var result = await router.GeneratePlanAsync(Request);

        Assert.Equal(LiveUIInferenceProviderKind.Gateway, result.Provider);
        Assert.Equal(1, worker.GetAvailabilityCalls);
        Assert.Equal(1, gateway.GenerateCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task WorkerAuthorizationFailureDuringReadinessStopsProviderFallback(HttpStatusCode statusCode)
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(statusCode));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://liveui-inference:8080/") };
        var worker = new LocalWorkerLiveUIInferenceProvider(client, Options.Create(new LiveUIWorkerClientOptions
        {
            Endpoint = new Uri("http://liveui-inference:8080"),
            BearerToken = "worker-secret"
        }));
        var browser = new FakeProvider(LiveUIInferenceProviderKind.Browser, LiveUIInferenceAvailabilityStatus.Unavailable, null);
        var gateway = new FakeProvider(LiveUIInferenceProviderKind.Gateway, LiveUIInferenceAvailabilityStatus.Available,
            new LiveUIActionPlan(Request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        var settings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserFirst };
        settings.Browser.Enabled = true;
        settings.Worker.Enabled = true;
        settings.Gateway.Enabled = true;
        settings.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnFailure;
        var router = new LiveUIInferenceRouter([browser, worker, gateway], new LiveUIActionPlanValidator(), Options.Create(settings));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.GeneratePlanAsync(Request));

        Assert.Equal(0, gateway.GetAvailabilityCalls);
        Assert.Equal(0, gateway.GenerateCalls);
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
        var inferenceRequest = Request with { RequestId = "session-request-id", ExecutionId = "execution-id" };
        var handler = new RecordingHttpMessageHandler(request =>
        {
            Assert.Equal("Bearer worker-secret", request.Headers.Authorization?.ToString());
            Assert.Equal("v1/liveui/inference/plan", request.RequestUri!.PathAndQuery.TrimStart('/'));
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var requestId = body.RootElement.GetProperty("requestId").GetString();
            Assert.Equal(inferenceRequest.RequestId, requestId);
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

        var result = await provider.GeneratePlanAsync(inferenceRequest);

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

    private sealed class WaitingProvider : ILiveUIInferenceProvider
    {
        private readonly TaskCompletionSource<bool> _availabilityRequested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AvailabilityRequested => _availabilityRequested.Task;
        public LiveUIInferenceProviderKind Kind => LiveUIInferenceProviderKind.Browser;

        public async Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
        {
            _availabilityRequested.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(Kind, LiveUIInferenceAvailabilityStatus.Unavailable);
        }

        public Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

    private sealed class BrowserInferenceJavaScriptRuntime(bool mismatchRequestId) : IJSRuntime
    {
        private readonly BrowserInferenceJavaScriptModule _module = new(mismatchRequestId);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult((TValue)(object)_module);
    }

    private sealed class BrowserInferenceJavaScriptModule(bool mismatchRequestId) : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            object value = identifier switch
            {
                "getAvailability" or "initialize" => new LiveUIInferenceAvailability(
                    LiveUIInferenceProviderKind.Browser, LiveUIInferenceAvailabilityStatus.Available),
                "generate" => CreateResponse((LiveUIBrowserInferenceRequest)args![0]!),
                _ => throw new InvalidOperationException($"Unexpected JavaScript call '{identifier}'.")
            };
            return ValueTask.FromResult((TValue)value);
        }

        private LiveUIBrowserInferenceResponse CreateResponse(LiveUIBrowserInferenceRequest request) => new(
            mismatchRequestId ? "stale-request-id" : request.RequestId,
            request.ExecutionId,
            request.SessionId,
            request.SchemaVersion,
            request.ManifestHash,
            new LiveUIActionPlan(request.SchemaVersion, [new("activate", "todo.tab.completed")]));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellationAwareBrowserInferenceRuntime : IJSRuntime
    {
        public CancellationAwareBrowserInferenceModule Module { get; } = new();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult((TValue)(object)Module);
    }

    private sealed class CancellationAwareBrowserInferenceModule : IJSObjectReference
    {
        private readonly TaskCompletionSource<bool> _generateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> GenerateStarted => _generateStarted;
        public bool GenerateCancellationObserved { get; private set; }
        public int CancelCalls { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier is "getAvailability" or "initialize")
                return (TValue)(object)new LiveUIInferenceAvailability(
                    LiveUIInferenceProviderKind.Browser, LiveUIInferenceAvailabilityStatus.Available);
            if (identifier == "cancel")
            {
                CancelCalls++;
                return default!;
            }
            if (identifier != "generate")
                throw new InvalidOperationException($"Unexpected JavaScript call '{identifier}'.");

            Assert.True(cancellationToken.CanBeCanceled);
            _generateStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The cancelled prompt unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                GenerateCancellationObserved = true;
                throw;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
