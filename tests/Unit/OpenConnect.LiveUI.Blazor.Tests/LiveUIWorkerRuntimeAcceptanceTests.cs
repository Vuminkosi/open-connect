using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenConnect.LiveUI.Inference;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class LiveUIWorkerRuntimeAcceptanceTests
{
    [LiveWorkerFact]
    [Trait("Category", "LiveInference")]
    public async Task Configured_worker_accepts_and_returns_a_server_requested_plan()
    {
        var endpointText = Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_WORKER_ENDPOINT");
        var bearerToken = Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_WORKER_TOKEN");
        if (string.IsNullOrWhiteSpace(endpointText) || string.IsNullOrWhiteSpace(bearerToken))
            throw new InvalidOperationException("Live worker acceptance test was not configured at discovery time.");
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException("OPENCONNECT_LIVEUI_WORKER_ENDPOINT must be an absolute URI.");

        using var httpClient = new HttpClient { BaseAddress = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/") };

        var modelRequest = new
        {
            requestId = Guid.NewGuid().ToString("N"),
            schemaVersion = "openconnect.liveui/v1",
            intent = "Select the Completed tab.",
            context = new
            {
                route = "todo.home",
                availableActions = new[]
                {
                    new { target = "todo.tab.all", action = "activate" },
                    new { target = "todo.tab.active", action = "activate" },
                    new { target = "todo.tab.completed", action = "activate" },
                },
            },
            generation = new { maxActions = 8, maxOutputTokens = 128 },
        };
        using var modelRequestMessage = new HttpRequestMessage(HttpMethod.Post, "v1/liveui/inference/plan")
        {
            Content = JsonContent.Create(modelRequest),
        };
        modelRequestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var modelResponse = await httpClient.SendAsync(modelRequestMessage);
        Assert.Equal(HttpStatusCode.OK, modelResponse.StatusCode);
        await using var modelResponseStream = await modelResponse.Content.ReadAsStreamAsync();
        using var modelResult = await JsonDocument.ParseAsync(modelResponseStream);
        Assert.Equal("qwen3-0.6b", modelResult.RootElement.GetProperty("provider").GetString());
        var modelActions = modelResult.RootElement.GetProperty("plan").GetProperty("actions");
        var modelAction = Assert.Single(modelActions.EnumerateArray());
        Assert.Equal("activate", modelAction.GetProperty("type").GetString());
        Assert.Equal("todo.tab.completed", modelAction.GetProperty("target").GetString());

        var provider = new LocalWorkerLiveUIInferenceProvider(
            httpClient,
            Options.Create(new LiveUIWorkerClientOptions { Endpoint = endpoint, BearerToken = bearerToken }));
        var request = new LiveUIInferenceRequest(
            "Select completed tasks tab",
            "acceptance-session",
            "todo.home",
            "openconnect.liveui/v1",
            [new("todo.completed", ["activate"])]);

        var availability = await provider.GetAvailabilityAsync();
        Assert.Equal(LiveUIInferenceAvailabilityStatus.Available, availability.Status);
        var result = await provider.GeneratePlanAsync(request);

        Assert.Equal(LiveUIInferenceProviderKind.LocalWorker, result.Provider);
        Assert.Equal("completed", result.Outcome);
        Assert.NotNull(result.Plan);
        Assert.True(new LiveUIActionPlanValidator().Validate(request, result.Plan!, 8, out var reason), reason);
        var action = Assert.Single(result.Plan!.Actions);
        Assert.Equal("activate", action.Type);
        Assert.Equal("todo.completed", action.TargetId);
    }
}

public sealed class LiveWorkerFactAttribute : FactAttribute
{
    public LiveWorkerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_WORKER_ENDPOINT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_WORKER_TOKEN")))
            Skip = "Set OPENCONNECT_LIVEUI_WORKER_ENDPOINT and OPENCONNECT_LIVEUI_WORKER_TOKEN to run live worker acceptance.";
    }
}
