using Microsoft.Extensions.Options;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;
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
        options.Mode = LiveUIInferenceMode.BrowserFirst;
        options.Browser.Enabled = true;
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
        options.Mode = LiveUIInferenceMode.BrowserFirst;
        options.Browser.Enabled = true;
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

    [Theory]
    [InlineData("privacy_blocked", true)]
    [InlineData("authorization_denied", true)]
    [InlineData("privacy_blocked", false)]
    [InlineData("authorization_denied", false)]
    [InlineData("unauthorized", true)]
    [InlineData("unauthorized", false)]
    [InlineData("forbidden", true)]
    [InlineData("forbidden", false)]
    public async Task ProviderDenialNeverFallsBack(string denial, bool duringAvailability)
    {
        var plan = new LiveUIActionPlan("openconnect.liveui/v1", [new("activate", "todo.tab.completed")]);
        var browser = new StubProvider(LiveUIInferenceProviderKind.Browser,
            new LiveUIInferenceResult(LiveUIInferenceProviderKind.Browser, plan, denial),
            duringAvailability ? LiveUIInferenceAvailabilityStatus.Blocked : LiveUIInferenceAvailabilityStatus.Available,
            duringAvailability ? denial : null);
        var gateway = new StubProvider(LiveUIInferenceProviderKind.Gateway, SuccessfulResult());
        var options = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserFirst };
        options.Browser.Enabled = true;
        options.Gateway.Enabled = true;
        options.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnFailure;
        var router = new LiveUIInferenceRouter([browser, gateway], new LiveUIActionPlanValidator(), Options.Create(options));
        var request = new LiveUIInferenceRequest("Show completed tasks", "session", "todo", "openconnect.liveui/v1",
            [new("todo.tab.completed", ["activate"])]);

        if (denial == "privacy_blocked")
            await Assert.ThrowsAsync<PrivacyBlockedException>(() => router.GeneratePlanAsync(request));
        else
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.GeneratePlanAsync(request));
        Assert.Equal(0, gateway.GetAvailabilityCalls);
        Assert.Equal(0, gateway.GenerateCalls);
    }

    [Theory]
    [InlineData("rate_limited")]
    [InlineData("browser_response_replay_cache_full")]
    [InlineData("cancelled")]
    public async Task TerminalProviderOutcomeNeverReturnsAPlan(string outcome)
    {
        var plan = new LiveUIActionPlan("openconnect.liveui/v1", [new("activate", "todo.tab.completed")]);
        var browser = new StubProvider(LiveUIInferenceProviderKind.Browser,
            new LiveUIInferenceResult(LiveUIInferenceProviderKind.Browser, plan, outcome));
        var gateway = new StubProvider(LiveUIInferenceProviderKind.Gateway, SuccessfulResult());
        var options = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserFirst };
        options.Browser.Enabled = true;
        options.Gateway.Enabled = true;
        options.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnFailure;
        var router = new LiveUIInferenceRouter([browser, gateway], new LiveUIActionPlanValidator(), Options.Create(options));
        var request = new LiveUIInferenceRequest("Show completed tasks", "session", "todo", "openconnect.liveui/v1",
            [new("todo.tab.completed", ["activate"])]);

        var result = await router.GeneratePlanAsync(request);

        Assert.Equal(outcome, result.Outcome);
        Assert.Null(result.Plan);
        Assert.Equal(0, gateway.GetAvailabilityCalls);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("canceled")]
    public async Task CancelledAvailabilityStopsFallback(string reason)
    {
        var browser = new StubProvider(LiveUIInferenceProviderKind.Browser, SuccessfulResult(),
            LiveUIInferenceAvailabilityStatus.Unavailable, reason);
        var gateway = new StubProvider(LiveUIInferenceProviderKind.Gateway, SuccessfulResult());
        var options = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserFirst };
        options.Browser.Enabled = true;
        options.Gateway.Enabled = true;
        options.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnFailure;
        var router = new LiveUIInferenceRouter([browser, gateway], new LiveUIActionPlanValidator(), Options.Create(options));
        var request = new LiveUIInferenceRequest("Show completed tasks", "session", "todo", "openconnect.liveui/v1",
            [new("todo.tab.completed", ["activate"])]);

        var result = await router.GeneratePlanAsync(request);

        Assert.Equal("cancelled", result.Outcome);
        Assert.Null(result.Plan);
        Assert.Equal(0, browser.GenerateCalls);
        Assert.Equal(0, gateway.GetAvailabilityCalls);
    }

    [Fact]
    public async Task FailureOutcomeCannotSupplyAnExecutablePlan()
    {
        var plan = new LiveUIActionPlan("openconnect.liveui/v1", [new("activate", "todo.tab.completed")]);
        var browser = new StubProvider(LiveUIInferenceProviderKind.Browser,
            new LiveUIInferenceResult(LiveUIInferenceProviderKind.Browser, plan, "invalid_plan"));
        var options = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserOnly };
        options.Browser.Enabled = true;
        var router = new LiveUIInferenceRouter([browser], new LiveUIActionPlanValidator(), Options.Create(options));
        var request = new LiveUIInferenceRequest("Show completed tasks", "session", "todo", "openconnect.liveui/v1",
            [new("todo.tab.completed", ["activate"])]);

        var result = await router.GeneratePlanAsync(request);

        Assert.Equal("invalid_plan", result.Outcome);
        Assert.Null(result.Plan);
    }

    private static LiveUIInferenceResult SuccessfulResult() =>
        new(
            LiveUIInferenceProviderKind.Gateway,
            new LiveUIActionPlan("openconnect.liveui/v1", [new LiveUIPlannedAction("activate", "todo.tab.completed")]),
            "completed");

    private sealed class StubProvider(
        LiveUIInferenceProviderKind kind,
        LiveUIInferenceResult result,
        LiveUIInferenceAvailabilityStatus availabilityStatus = LiveUIInferenceAvailabilityStatus.Available,
        string? availabilityReason = null) : ILiveUIInferenceProvider
    {
        public LiveUIInferenceProviderKind Kind => kind;
        public int GetAvailabilityCalls { get; private set; }
        public int GenerateCalls { get; private set; }

        public Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
        {
            GetAvailabilityCalls++;
            return Task.FromResult(new LiveUIInferenceAvailability(kind, availabilityStatus, Reason: availabilityReason));
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
