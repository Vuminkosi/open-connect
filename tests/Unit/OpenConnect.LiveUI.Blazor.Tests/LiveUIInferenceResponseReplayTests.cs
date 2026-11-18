using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using OpenConnect.LiveUI.Blazor;
using OpenConnect.LiveUI.Inference;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class LiveUIInferenceResponseReplayTests
{
    private const int CorrelationCapacity = 1024;

    [Fact]
    public async Task GeneratePlanAsync_RejectsAResponseReplayForAnAlreadyCompletedCorrelation()
    {
        var request = new LiveUIInferenceRequest(
            "Show completed tasks",
            "session-1",
            "todo",
            "openconnect.liveui/v1",
            [new LiveUIInferenceCapability("todo.tab.completed", ["activate"])],
            "manifest-hash")
        {
            RequestId = "request-1",
            ExecutionId = "execution-1"
        };
        var module = new ReplayingBrowserInferenceModule();
        var provider = new BrowserPromptApiInferenceProvider(
            new ReplayingBrowserInferenceRuntime(module),
            Options.Create(new LiveUIInferenceOptions()));

        var first = await provider.GeneratePlanAsync(request);
        var replay = await provider.GeneratePlanAsync(request);

        Assert.Equal("completed", first.Outcome);
        Assert.NotNull(first.Plan);
        Assert.NotEqual("completed", replay.Outcome);
        Assert.Null(replay.Plan);
        Assert.Equal(2, module.GenerateCalls);
    }

    [Fact]
    public async Task GeneratePlanAsync_RejectsNewCorrelationsWhenReplayCacheIsFullWithoutEvictingProtectedIds()
    {
        var module = new ReplayingBrowserInferenceModule();
        var provider = new BrowserPromptApiInferenceProvider(
            new ReplayingBrowserInferenceRuntime(module),
            Options.Create(new LiveUIInferenceOptions()));

        var firstRequest = CreateRequest("request-0");
        var first = await provider.GeneratePlanAsync(firstRequest);
        for (var index = 1; index < CorrelationCapacity; index++)
            Assert.Equal("completed", (await provider.GeneratePlanAsync(CreateRequest($"request-{index}"))).Outcome);

        var saturated = await provider.GeneratePlanAsync(CreateRequest("request-new"));
        var replay = await provider.GeneratePlanAsync(firstRequest);

        Assert.Equal("completed", first.Outcome);
        Assert.Equal("browser_response_replay_cache_full", saturated.Outcome);
        Assert.Null(saturated.Plan);
        Assert.Equal("browser_response_replay", replay.Outcome);
        Assert.Null(replay.Plan);
    }

    private static LiveUIInferenceRequest CreateRequest(string requestId) =>
        new(
            "Show completed tasks",
            "session-1",
            "todo",
            "openconnect.liveui/v1",
            [new LiveUIInferenceCapability("todo.tab.completed", ["activate"])],
            "manifest-hash")
        {
            RequestId = requestId,
            ExecutionId = $"execution-{requestId}"
        };

    private sealed class ReplayingBrowserInferenceRuntime(ReplayingBrowserInferenceModule module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            ValueTask.FromResult((TValue)(object)module);
    }

    private sealed class ReplayingBrowserInferenceModule : IJSObjectReference
    {
        public int GenerateCalls { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            object value = identifier switch
            {
                "initialize" => new LiveUIInferenceAvailability(
                    LiveUIInferenceProviderKind.Browser,
                    LiveUIInferenceAvailabilityStatus.Available),
                "generate" => ReplayResponse((LiveUIBrowserInferenceRequest)args![0]!),
                _ => throw new InvalidOperationException($"Unexpected JavaScript call '{identifier}'.")
            };
            return ValueTask.FromResult((TValue)value);
        }

        private LiveUIBrowserInferenceResponse ReplayResponse(LiveUIBrowserInferenceRequest request)
        {
            GenerateCalls++;
            return new LiveUIBrowserInferenceResponse(
                request.RequestId,
                request.ExecutionId,
                request.SessionId,
                request.SchemaVersion,
                request.ManifestHash,
                new LiveUIActionPlan(request.SchemaVersion, [new("activate", "todo.tab.completed")]));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
