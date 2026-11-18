using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class GatewayPlannerLiveUIInferenceProviderTests
{
    [Fact]
    public async Task PlannerUsesCorrelatedGatewayContract()
    {
        var request = new LiveUIInferenceRequest("Show completed tasks", "session", "route", "openconnect.liveui/v1",
            [new("completed", ["activate"])])
        { RequestId = "request", ExecutionId = "execution" };
        using var client = new HttpClient(new Handler(async message =>
        {
            if (message.Method == HttpMethod.Get)
            {
                Assert.EndsWith("/liveui/inference/planner/availability", message.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                    new LiveUIInferenceAvailability(LiveUIInferenceProviderKind.Gateway, LiveUIInferenceAvailabilityStatus.Available))
                };
            }
            Assert.EndsWith("/liveui/inference/planner/plan", message.RequestUri!.AbsolutePath);
            var envelope = await message.Content!.ReadFromJsonAsync<GatewayLiveUIInferenceRequest>();
            Assert.Equal(request.RequestId, envelope!.RequestId);
            Assert.Equal(request.ExecutionId, envelope.ExecutionId);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                new GatewayLiveUIInferenceResponse(envelope.RequestId, envelope.ExecutionId,
                    new LiveUIInferenceResult(LiveUIInferenceProviderKind.Gateway,
                        new LiveUIActionPlan(request.SchemaVersion, [new("activate", "completed")]), "completed")))
            };
        }))
        { BaseAddress = new Uri("https://gateway.example/openconnect/v1/") };
        var provider = new GatewayPlannerLiveUIInferenceProvider(client);

        Assert.Equal(LiveUIInferenceAvailabilityStatus.Available, (await provider.GetAvailabilityAsync()).Status);
        var result = await provider.GeneratePlanAsync(request);
        Assert.Equal(LiveUIInferenceProviderKind.Gateway, result.Provider);
        Assert.Equal("completed", result.Outcome);
        Assert.Equal("completed", Assert.Single(result.Plan!.Actions).TargetId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task PlannerDenialStopsExecution(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(status))))
        { BaseAddress = new Uri("https://gateway.example/openconnect/v1/") };
        var provider = new GatewayPlannerLiveUIInferenceProvider(client);
        var request = new LiveUIInferenceRequest("Show tasks", "session", "route", "openconnect.liveui/v1", []);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.GetAvailabilityAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.GeneratePlanAsync(request));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlannerRejectsMismatchedCorrelationOrProvider(bool wrongCorrelation)
    {
        var request = new LiveUIInferenceRequest("Show tasks", "session", "route", "openconnect.liveui/v1", [])
        { RequestId = "request", ExecutionId = "execution" };
        using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new GatewayLiveUIInferenceResponse(wrongCorrelation ? "other" : request.RequestId,
                request.ExecutionId, new LiveUIInferenceResult(wrongCorrelation ? LiveUIInferenceProviderKind.Gateway :
                    LiveUIInferenceProviderKind.LocalWorker, null, "completed")))
        })))
        { BaseAddress = new Uri("https://gateway.example/openconnect/v1/") };
        var provider = new GatewayPlannerLiveUIInferenceProvider(client);

        var result = await provider.GeneratePlanAsync(request);

        Assert.Null(result.Plan);
        Assert.Equal("gateway_planner_response_mismatch", result.Outcome);
    }

    [Fact]
    public async Task PlannerPreservesRateLimit()
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests))))
        { BaseAddress = new Uri("https://gateway.example/openconnect/v1/") };
        var provider = new GatewayPlannerLiveUIInferenceProvider(client);
        var request = new LiveUIInferenceRequest("Show tasks", "session", "route", "openconnect.liveui/v1", []);
        var availability = await provider.GetAvailabilityAsync();
        Assert.Equal(LiveUIInferenceAvailabilityStatus.Blocked, availability.Status);
        Assert.Equal("rate_limited", availability.Reason);
        Assert.Equal("rate_limited", (await provider.GeneratePlanAsync(request)).Outcome);
    }

    [Fact]
    public async Task UnavailableGatewayPlannerFallsBackToLocalWorkerWhenPermitted()
    {
        using var client = new HttpClient(new Handler(message => Task.FromResult(message.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new LiveUIInferenceAvailability(
                LiveUIInferenceProviderKind.Gateway, LiveUIInferenceAvailabilityStatus.Available))
            }
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))
        { BaseAddress = new Uri("https://gateway.example/openconnect/v1/") };
        var worker = new LocalFallback();
        var settings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.GatewayFirst };
        settings.Gateway.Enabled = true;
        settings.Worker.Enabled = true;
        settings.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnUnavailable;
        var router = new LiveUIInferenceRouter([new GatewayPlannerLiveUIInferenceProvider(client), worker],
            new LiveUIActionPlanValidator(), Options.Create(settings));
        var request = new LiveUIInferenceRequest("Show completed", "session", "route", "openconnect.liveui/v1",
            [new("completed", ["activate"])]);

        var result = await router.GeneratePlanAsync(request);

        Assert.Equal("completed", result.Outcome);
        Assert.Equal(LiveUIInferenceProviderKind.LocalWorker, result.Provider);
        Assert.Equal(1, worker.Calls);
    }

    [Theory]
    [InlineData("privacy_blocked")]
    [InlineData("authorization_denied")]
    public async Task DenialEnvelopeCannotReturnAPlanOrFallBack(string outcome)
    {
        using var client = new HttpClient(new Handler(async message =>
        {
            if (message.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new LiveUIInferenceAvailability(
                    LiveUIInferenceProviderKind.Gateway, LiveUIInferenceAvailabilityStatus.Available))
                };
            var envelope = await message.Content!.ReadFromJsonAsync<GatewayLiveUIInferenceRequest>();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new GatewayLiveUIInferenceResponse(
                envelope!.RequestId, envelope.ExecutionId, new LiveUIInferenceResult(LiveUIInferenceProviderKind.Gateway,
                    new LiveUIActionPlan(envelope.Request.SchemaVersion, [new("activate", "completed")]), outcome)))
            };
        }))
        { BaseAddress = new Uri("https://gateway.example/openconnect/v1/") };
        var worker = new LocalFallback();
        var settings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.GatewayFirst };
        settings.Gateway.Enabled = true;
        settings.Worker.Enabled = true;
        settings.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnFailure;
        var router = new LiveUIInferenceRouter([new GatewayPlannerLiveUIInferenceProvider(client), worker],
            new LiveUIActionPlanValidator(), Options.Create(settings));
        var request = new LiveUIInferenceRequest("Show completed", "session", "route", "openconnect.liveui/v1",
            [new("completed", ["activate"])]);

        if (outcome == "privacy_blocked")
            await Assert.ThrowsAsync<PrivacyBlockedException>(() => router.GeneratePlanAsync(request));
        else
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.GeneratePlanAsync(request));
        Assert.Equal(0, worker.Calls);
    }

    private sealed class LocalFallback : ILiveUIInferenceProvider
    {
        public int Calls { get; private set; }
        public LiveUIInferenceProviderKind Kind => LiveUIInferenceProviderKind.LocalWorker;
        public Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LiveUIInferenceAvailability(Kind, LiveUIInferenceAvailabilityStatus.Available));
        public Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new LiveUIInferenceResult(Kind,
                new LiveUIActionPlan(request.SchemaVersion, [new("activate", "completed")]), "completed"));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
