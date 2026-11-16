using System.Net.Http.Json;
using System.Text.Json;

namespace OpenConnect.LiveUI.Inference;

/// <summary>Calls the authenticated OpenConnect Gateway for its configured private worker fallback.</summary>
public sealed class GatewayWorkerLiveUIInferenceProvider(HttpClient httpClient) : ILiveUIInferenceProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public LiveUIInferenceProviderKind Kind => LiveUIInferenceProviderKind.LocalWorker;

    public async Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("liveui/inference/worker/availability", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException("The OpenConnect Gateway rejected LiveUI inference authorization.");
            if (!response.IsSuccessStatusCode)
                return new(Kind, LiveUIInferenceAvailabilityStatus.Unavailable, SupportsStructuredOutput: true,
                    SupportsCancellation: true, Reason: "gateway_worker_unavailable");

            var availability = await response.Content.ReadFromJsonAsync<LiveUIInferenceAvailability>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (availability is null || availability.Provider != Kind)
                return new(Kind, LiveUIInferenceAvailabilityStatus.Unavailable, Reason: "gateway_worker_response_invalid");
            return availability;
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            return new(Kind, LiveUIInferenceAvailabilityStatus.Unavailable, Reason: "gateway_unavailable");
        }
        catch (JsonException)
        {
            return new(Kind, LiveUIInferenceAvailabilityStatus.Unavailable, Reason: "gateway_worker_response_invalid");
        }
    }

    public async Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var envelope = new GatewayLiveUIInferenceRequest(request.RequestId, request.ExecutionId, request);
        try
        {
            using var response = await httpClient.PostAsJsonAsync("liveui/inference/plan", envelope, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException("The OpenConnect Gateway rejected LiveUI inference authorization.");
            if (!response.IsSuccessStatusCode)
                return new(Kind, null, "gateway_worker_inference_failed");

            var resultEnvelope = await response.Content.ReadFromJsonAsync<GatewayLiveUIInferenceResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (resultEnvelope?.Result is null || resultEnvelope.RequestId != request.RequestId ||
                resultEnvelope.ExecutionId != request.ExecutionId || resultEnvelope.Result.Provider != Kind)
                return new(Kind, null, "gateway_worker_response_mismatch");
            return resultEnvelope.Result;
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            return new(Kind, null, "gateway_unavailable");
        }
        catch (JsonException)
        {
            return new(Kind, null, "gateway_worker_response_invalid");
        }
    }
}
