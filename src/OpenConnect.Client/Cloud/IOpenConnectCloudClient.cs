namespace OpenConnect.Client.Cloud;

public interface IOpenConnectCloudClient
{
    Task<CloudReadinessResult> GetHealthReadyAsync(CancellationToken cancellationToken = default);
    Task<CloudCapabilitiesResult> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<CloudClassificationResponse> ClassifyAsync(CloudClassificationRequest request, CancellationToken cancellationToken = default);
    Task<CloudChatResponse> ChatAsync(CloudChatRequest request, CancellationToken cancellationToken = default);
    Task<CloudLiveUIPlanResponse> PlanLiveUIAsync(CloudLiveUIPlanRequest request, CancellationToken cancellationToken = default);
    Task<CloudSpeechSynthesisResponse> SynthesizeSpeechAsync(CloudSpeechSynthesisRequest request, CancellationToken cancellationToken = default);
}
