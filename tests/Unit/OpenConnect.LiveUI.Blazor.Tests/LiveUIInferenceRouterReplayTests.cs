using Microsoft.Extensions.Options;
using OpenConnect.LiveUI.Inference;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class LiveUIInferenceRouterReplayTests
{
    [Fact]
    public async Task GeneratePlanAsync_DoesNotFallbackWhenBrowserReplayCacheIsFull()
    {
        var browser = new StubProvider(
            LiveUIInferenceProviderKind.Browser,
            new LiveUIInferenceResult(LiveUIInferenceProviderKind.Browser, null, "browser_response_replay_cache_full"));
        var worker = new StubProvider(LiveUIInferenceProviderKind.LocalWorker, SuccessfulResult());
        var gateway = new StubProvider(LiveUIInferenceProviderKind.Gateway, SuccessfulResult());
        var options = new LiveUIInferenceOptions();
        options.Worker.Enabled = true;
        options.Gateway.Enabled = true;
        options.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnFailure;
        var router = new LiveUIInferenceRouter(
            [browser, worker, gateway],
            new LiveUIActionPlanValidator(),
            Options.Create(options));

        var result = await router.GeneratePlanAsync(new LiveUIInferenceRequest(
            "Show completed tasks",
            "session-1",
            "todo",
            "openconnect.liveui/v1",
            [new LiveUIInferenceCapability("todo.tab.completed", ["activate"])]));

        Assert.Equal("browser_response_replay_cache_full", result.Outcome);
        Assert.Null(result.Plan);
        Assert.Equal(1, browser.GenerateCalls);
        Assert.Equal(0, worker.GetAvailabilityCalls);
        Assert.Equal(0, worker.GenerateCalls);
        Assert.Equal(0, gateway.GetAvailabilityCalls);
        Assert.Equal(0, gateway.GenerateCalls);
    }

    [Fact]
    public async Task GeneratePlanAsync_UsesLocalWorkerWhenExternalFallbackIsDisabled()
    {
        var browser = new StubProvider(
            LiveUIInferenceProviderKind.Browser,
            new LiveUIInferenceResult(LiveUIInferenceProviderKind.Browser, null, "unavailable"),
            LiveUIInferenceAvailabilityStatus.Unavailable);
        var worker = new StubProvider(LiveUIInferenceProviderKind.LocalWorker, SuccessfulResult());
        var gateway = new StubProvider(LiveUIInferenceProviderKind.Gateway, SuccessfulResult());
        var options = new LiveUIInferenceOptions();
        options.Worker.Enabled = true;
        options.Gateway.Enabled = true;
        options.Gateway.FallbackPolicy = LiveUIFallbackPolicy.Disabled;
        var router = new LiveUIInferenceRouter(
            [browser, worker, gateway],
            new LiveUIActionPlanValidator(),
            Options.Create(options));

        var result = await router.GeneratePlanAsync(new LiveUIInferenceRequest(
            "Show completed tasks",
            "session-1",
            "todo",
            "openconnect.liveui/v1",
            [new LiveUIInferenceCapability("todo.tab.completed", ["activate"])]));

        Assert.Equal(LiveUIInferenceProviderKind.LocalWorker, result.Provider);
        Assert.Equal("completed", result.Outcome);
        Assert.NotNull(result.Plan);
        Assert.Equal(1, browser.GetAvailabilityCalls);
        Assert.Equal(0, browser.GenerateCalls);
        Assert.Equal(1, worker.GetAvailabilityCalls);
        Assert.Equal(1, worker.GenerateCalls);
        Assert.Equal(0, gateway.GetAvailabilityCalls);
        Assert.Equal(0, gateway.GenerateCalls);
    }

    private static LiveUIInferenceResult SuccessfulResult() =>
        new(
            LiveUIInferenceProviderKind.Gateway,
            new LiveUIActionPlan("openconnect.liveui/v1", [new LiveUIPlannedAction("activate", "todo.tab.completed")]),
            "completed");

    private sealed class StubProvider(
        LiveUIInferenceProviderKind kind,
        LiveUIInferenceResult result,
        LiveUIInferenceAvailabilityStatus availabilityStatus = LiveUIInferenceAvailabilityStatus.Available) : ILiveUIInferenceProvider
    {
        public LiveUIInferenceProviderKind Kind => kind;
        public int GetAvailabilityCalls { get; private set; }
        public int GenerateCalls { get; private set; }

        public Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
        {
            GetAvailabilityCalls++;
            return Task.FromResult(new LiveUIInferenceAvailability(kind, availabilityStatus));
        }

        public Task<LiveUIInferenceResult> GeneratePlanAsync(
            LiveUIInferenceRequest request,
            CancellationToken cancellationToken = default)
        {
            GenerateCalls++;
            return Task.FromResult(result with { Provider = kind });
        }
    }
}
