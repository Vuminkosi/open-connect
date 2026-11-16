using System.Net;
using System.Net.Http.Json;
using OpenConnect.LiveUI.Inference;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class GatewayWorkerLiveUIInferenceProviderTests
{
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

        Assert.NotNull(sentEnvelope);
        Assert.Equal(inferenceRequest.RequestId, sentEnvelope.RequestId);
        Assert.Equal(inferenceRequest.ExecutionId, sentEnvelope.ExecutionId);
        Assert.Equal("Show completed tasks", sentEnvelope.Request.Intent);
        Assert.Equal(plan.SchemaVersion, result.Plan?.SchemaVersion);
        Assert.Equal(plan.Actions, result.Plan?.Actions);
        Assert.Equal("completed", result.Outcome);
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

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
