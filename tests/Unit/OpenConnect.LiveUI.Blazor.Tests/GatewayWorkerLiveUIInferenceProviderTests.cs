using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Client;
using OpenConnect.LiveUI.Blazor;
using OpenConnect.LiveUI.Inference;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class GatewayWorkerLiveUIInferenceProviderTests
{
    [Fact]
    public async Task Router_PrefersExplicitWorkerOverGatewayWorkerFallback()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("Fallback must not be used.")))
        {
            BaseAddress = new Uri("https://gateway.example/openconnect/v1/")
        };
        var worker = new ExplicitWorker();
        var settings = new LiveUIInferenceOptions();
        settings.Worker.Enabled = true;
        var router = new LiveUIInferenceRouter([new GatewayWorkerLiveUIInferenceProvider(client), worker],
            new LiveUIActionPlanValidator(), Options.Create(settings));
        var request = new LiveUIInferenceRequest("Show completed tasks", "session", "todo", "openconnect.liveui/v1",
            [new("completed-tab", ["activate"])]);

        var result = await router.GeneratePlanAsync(request);

        Assert.Equal("completed", result.Outcome);
        Assert.Equal(1, worker.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BlazorRegistration_ReusesGatewayClientTransportAndCredentials(bool registerClientFirst)
    {
        var requests = 0;
        var services = new ServiceCollection();
        if (!registerClientFirst)
            services.AddOpenConnectLiveUIBlazor();
        services.AddOpenConnectClient(options =>
        {
            options.ApiBaseAddress = new Uri("https://gateway.example/openconnect/v1/");
            options.WebSocketHeaders["Authorization"] = "Bearer test-token";
            options.WebSocketHeaders["X-OpenConnect-Test-Tenant"] = "test-tenant";
        });
        services.AddHttpClient(nameof(IOpenConnectClient)).ConfigurePrimaryHttpMessageHandler(() => new StubHandler(request =>
        {
            requests++;
            Assert.Equal("https://gateway.example/openconnect/v1/liveui/inference/worker/availability", request.RequestUri?.AbsoluteUri);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("test-tenant", Assert.Single(request.Headers.GetValues("X-OpenConnect-Test-Tenant")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new LiveUIInferenceAvailability(
                    LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceAvailabilityStatus.Available))
            });
        }));
        if (registerClientFirst)
            services.AddOpenConnectLiveUIBlazor();
        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        var worker = scope.ServiceProvider.GetRequiredService<GatewayWorkerLiveUIInferenceProvider>();
        var result = await worker.GetAvailabilityAsync();

        Assert.Equal(LiveUIInferenceAvailabilityStatus.Available, result.Status);
        Assert.Equal(1, requests);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(GatewayWorkerLiveUIInferenceProvider));
    }

    [Fact]
    public void BlazorRegistration_DoesNotRegisterGatewayWorkerTwice()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectLiveUIBlazor();
        services.AddOpenConnectLiveUIBlazor();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(GatewayWorkerLiveUIInferenceProvider));
    }

    [Fact]
    public async Task BlazorRegistration_WithoutGatewayClientReportsWorkerUnavailable()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectLiveUIBlazor();
        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        var worker = scope.ServiceProvider.GetRequiredService<GatewayWorkerLiveUIInferenceProvider>();
        var result = await worker.GetAvailabilityAsync();

        Assert.Equal(LiveUIInferenceAvailabilityStatus.Unavailable, result.Status);
        Assert.Equal("gateway_worker_unavailable", result.Reason);
    }

    [Fact]
    public async Task GeneratePlanAsync_PreservesRequestAndExecutionCorrelationAcrossGateway()
    {
        GatewayLiveUIInferenceRequest? sentEnvelope = null;
        var plan = new LiveUIActionPlan("openconnect.liveui/v1", [new("activate", "completed-tab")]);
        using var client = new HttpClient(new StubHandler(async request =>
        {
            Assert.Equal("liveui/inference/plan", request.RequestUri?.AbsolutePath.TrimStart('/').Replace("openconnect/v1/", string.Empty));
            sentEnvelope = await request.Content!.ReadFromJsonAsync<GatewayLiveUIInferenceRequest>();
            var response = new GatewayLiveUIInferenceResponse(sentEnvelope!.RequestId, sentEnvelope.ExecutionId,
                new LiveUIInferenceResult(LiveUIInferenceProviderKind.LocalWorker, plan, "completed"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }))
        {
            BaseAddress = new Uri("https://gateway.example/openconnect/v1/")
        };
        var provider = new GatewayWorkerLiveUIInferenceProvider(client);
        var inferenceRequest = new LiveUIInferenceRequest("Show completed tasks", "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            "todo", "openconnect.liveui/v1", [new("completed-tab", ["activate"])])
        {
            RequestId = Guid.NewGuid().ToString("D"),
            ExecutionId = Guid.NewGuid().ToString("N")
        };

        var result = await provider.GeneratePlanAsync(inferenceRequest);

        Assert.Equal(LiveUIInferenceProviderKind.LocalWorker, provider.Kind);
        Assert.Equal(LiveUIInferenceProviderKind.LocalWorker, result.Provider);
        Assert.NotNull(sentEnvelope);
        Assert.Equal(inferenceRequest.RequestId, sentEnvelope.RequestId);
        Assert.Equal(inferenceRequest.ExecutionId, sentEnvelope.ExecutionId);
        Assert.Equal("Show completed tasks", sentEnvelope.Request.Intent);
        Assert.Equal(plan.SchemaVersion, result.Plan?.SchemaVersion);
        Assert.Equal(plan.Actions, result.Plan?.Actions);
        Assert.Equal("completed", result.Outcome);
    }

    [Fact]
    public async Task Router_UsesAuthenticatedGatewayForTheLocalWorker()
    {
        var plan = new LiveUIActionPlan("openconnect.liveui/v1", [new("activate", "completed-tab")]);
        var availabilityRequests = 0;
        using var client = new HttpClient(new StubHandler(async request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/liveui/inference/worker/availability", StringComparison.Ordinal))
            {
                availabilityRequests++;
                Assert.Equal("/openconnect/v1/liveui/inference/worker/availability", path);
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new LiveUIInferenceAvailability(
                        LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceAvailabilityStatus.Available))
                };
            }

            Assert.EndsWith("/liveui/inference/plan", path, StringComparison.Ordinal);
            Assert.Equal(HttpMethod.Post, request.Method);
            var envelope = await request.Content!.ReadFromJsonAsync<GatewayLiveUIInferenceRequest>();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new GatewayLiveUIInferenceResponse(
                    envelope!.RequestId,
                    envelope.ExecutionId,
                    new LiveUIInferenceResult(LiveUIInferenceProviderKind.LocalWorker, plan, "completed")))
            };
        }))
        {
            BaseAddress = new Uri("https://gateway.example/openconnect/v1/")
        };
        client.DefaultRequestHeaders.Authorization = new("Bearer", "test-token");
        var provider = new GatewayWorkerLiveUIInferenceProvider(client);
        var options = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.GatewayFirst };
        options.Gateway.Enabled = false;
        options.Browser.Enabled = false;
        options.Worker.Enabled = true;
        var router = new LiveUIInferenceRouter([provider], new LiveUIActionPlanValidator(), Options.Create(options));
        var inferenceRequest = new LiveUIInferenceRequest("Show completed tasks", "session", "todo",
            "openconnect.liveui/v1", [new("completed-tab", ["activate"])]);

        var result = await router.GeneratePlanAsync(inferenceRequest);

        Assert.Equal(1, availabilityRequests);
        Assert.Equal(LiveUIInferenceProviderKind.LocalWorker, result.Provider);
        Assert.Equal(plan.Actions, result.Plan?.Actions);
    }

    [Fact]
    public async Task GetAvailabilityAsync_PropagatesGatewayAuthorizationFailure()
    {
        using var client = new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))))
        {
            BaseAddress = new Uri("https://gateway.example/openconnect/v1/")
        };
        var provider = new GatewayWorkerLiveUIInferenceProvider(client);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.GetAvailabilityAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RateLimit_StopsFallbackAndPreservesOutcome(bool limitAvailability)
    {
        using var client = new HttpClient(new StubHandler(request =>
        {
            if (!limitAvailability && request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new LiveUIInferenceAvailability(
                        LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceAvailabilityStatus.Available))
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }))
        { BaseAddress = new Uri("https://gateway.example/openconnect/v1/") };
        var fallback = new CountingGateway();
        var settings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserFirst };
        settings.Worker.Enabled = true;
        settings.Gateway.Enabled = true;
        settings.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnFailure;
        var router = new LiveUIInferenceRouter([new GatewayWorkerLiveUIInferenceProvider(client), fallback],
            new LiveUIActionPlanValidator(), Options.Create(settings));
        var request = new LiveUIInferenceRequest("Show completed tasks", "session", "todo", "openconnect.liveui/v1",
            [new("completed-tab", ["activate"])]);

        var result = await router.GeneratePlanAsync(request);

        Assert.Equal("rate_limited", result.Outcome);
        Assert.Null(result.Plan);
        Assert.Equal(0, fallback.Calls);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.ServiceUnavailable)]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    [InlineData(false, HttpStatusCode.BadGateway)]
    public async Task GatewayWorkerUnavailableDuringGeneration_UsesUnavailableFallback(
        bool networkFailure, HttpStatusCode failureStatus)
    {
        using var client = new HttpClient(new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new LiveUIInferenceAvailability(
                        LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceAvailabilityStatus.Available))
                });
            if (networkFailure)
                throw new HttpRequestException("connection failed");
            return Task.FromResult(new HttpResponseMessage(failureStatus));
        }))
        { BaseAddress = new Uri("https://gateway.example/openconnect/v1/") };
        var fallback = new SuccessfulGateway();
        var settings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserFirst };
        settings.Worker.Enabled = true;
        settings.Gateway.Enabled = true;
        settings.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnUnavailable;
        var router = new LiveUIInferenceRouter([new GatewayWorkerLiveUIInferenceProvider(client), fallback],
            new LiveUIActionPlanValidator(), Options.Create(settings));
        var request = new LiveUIInferenceRequest("Show completed tasks", "session", "todo", "openconnect.liveui/v1",
            [new("completed-tab", ["activate"])]);

        var result = await router.GeneratePlanAsync(request);

        Assert.Equal("completed", result.Outcome);
        Assert.Equal(1, fallback.Calls);
    }

    private sealed class ExplicitWorker : ILiveUIInferenceProvider
    {
        public int Calls { get; private set; }
        public LiveUIInferenceProviderKind Kind => LiveUIInferenceProviderKind.LocalWorker;
        public Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LiveUIInferenceAvailability(Kind, LiveUIInferenceAvailabilityStatus.Available));
        public Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new LiveUIInferenceResult(Kind,
                new LiveUIActionPlan(request.SchemaVersion, [new("activate", "completed-tab")]), "completed"));
        }
    }

    private sealed class SuccessfulGateway : ILiveUIInferenceProvider
    {
        public int Calls { get; private set; }
        public LiveUIInferenceProviderKind Kind => LiveUIInferenceProviderKind.Gateway;
        public Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LiveUIInferenceAvailability(Kind, LiveUIInferenceAvailabilityStatus.Available));
        public Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new LiveUIInferenceResult(Kind,
                new LiveUIActionPlan(request.SchemaVersion, [new("activate", "completed-tab")]), "completed"));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkerRateLimitEnvelope_StopsFallback(bool limitAvailability)
    {
        using var client = new HttpClient(new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new LiveUIInferenceAvailability(
                        LiveUIInferenceProviderKind.LocalWorker,
                        limitAvailability ? LiveUIInferenceAvailabilityStatus.Blocked : LiveUIInferenceAvailabilityStatus.Available,
                        Reason: limitAvailability ? "rate_limited" : null))
                };
            var envelope = await request.Content!.ReadFromJsonAsync<GatewayLiveUIInferenceRequest>();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new GatewayLiveUIInferenceResponse(envelope!.RequestId, envelope.ExecutionId,
                    new LiveUIInferenceResult(LiveUIInferenceProviderKind.LocalWorker, null, "rate_limited")))
            };
        }))
        { BaseAddress = new Uri("https://gateway.example/openconnect/v1/") };
        var fallback = new CountingGateway();
        var settings = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserFirst };
        settings.Worker.Enabled = true;
        settings.Gateway.Enabled = true;
        settings.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnFailure;
        var router = new LiveUIInferenceRouter([new GatewayWorkerLiveUIInferenceProvider(client), fallback],
            new LiveUIActionPlanValidator(), Options.Create(settings));
        var request = new LiveUIInferenceRequest("Show completed tasks", "session", "todo", "openconnect.liveui/v1",
            [new("completed-tab", ["activate"])]);

        var result = await router.GeneratePlanAsync(request);

        Assert.Equal("rate_limited", result.Outcome);
        Assert.Null(result.Plan);
        Assert.Equal(0, fallback.Calls);
    }

    private sealed class CountingGateway : ILiveUIInferenceProvider
    {
        public int Calls { get; private set; }
        public LiveUIInferenceProviderKind Kind => LiveUIInferenceProviderKind.Gateway;
        public Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new LiveUIInferenceAvailability(Kind, LiveUIInferenceAvailabilityStatus.Available));
        }
        public Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Rate-limited requests must not fall back.");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
