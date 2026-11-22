namespace OpenConnect.Client.Cloud;

public sealed record CloudReadinessResult(
    string Status,
    IReadOnlyList<string> Capabilities);

public sealed record CloudCapabilityDescriptor(
    string Capability,
    string Status,
    bool StreamingSupported);

public sealed record CloudCapabilitiesResult(
    IReadOnlyList<CloudCapabilityDescriptor> Capabilities,
    string? TraceId = null);

public sealed record CloudClassificationInput(
    string? Text = null,
    string? InputRef = null);

public sealed record CloudClassificationConfiguration(
    string? LabelSetVersion = null,
    string? LocaleHint = null,
    double? Threshold = null,
    int? MaxLabels = null);

public sealed record CloudClassificationRequest(
    CloudClassificationInput Input,
    CloudClassificationConfiguration? Configuration = null,
    string? RequestId = null,
    DateTimeOffset? ExpiresAtUtc = null);

public sealed record CloudClassificationResult(
    IReadOnlyList<string> Labels,
    IReadOnlyDictionary<string, double> Scores,
    bool Abstained,
    string ModelRevision,
    string LabelSetVersion);

public sealed record CloudClassificationResponse(
    string SchemaVersion,
    string RequestId,
    string TraceId,
    CloudClassificationResult Result);

public sealed record CloudChatMessage(
    string Role,
    string Content);

public sealed record CloudChatInput(
    IReadOnlyList<CloudChatMessage> Messages,
    int MaxTokens = 1024,
    bool Stream = false);

public sealed record CloudChatRequest(
    string ModelId,
    CloudChatInput Input,
    string? RequestId = null,
    DateTimeOffset? ExpiresAtUtc = null);

public sealed record CloudTokenUsage(
    int InputTokens,
    int OutputTokens);

public sealed record CloudChatResponse(
    string SchemaVersion,
    string RequestId,
    string ModelId,
    string TraceId,
    string Output,
    CloudTokenUsage Usage);

public sealed record CloudLiveUIAvailableAction(
    string Target,
    string Action,
    string? Label = null);

public sealed record CloudLiveUIContext(
    string Route,
    IReadOnlyList<CloudLiveUIAvailableAction> AvailableActions);

public sealed record CloudLiveUIGenerationOptions(
    int MaxActions = 8,
    int MaxOutputTokens = 256);

public sealed record CloudLiveUIPlanInput(
    string SchemaVersion,
    string Intent,
    CloudLiveUIContext Context,
    CloudLiveUIGenerationOptions Generation);

public sealed record CloudLiveUIPlanRequest(
    CloudLiveUIPlanInput Input,
    string? RequestId = null,
    DateTimeOffset? ExpiresAtUtc = null);

public sealed record CloudLiveUIPlannedAction(
    string Type,
    string Target,
    string? Value = null);

public sealed record CloudLiveUIPlan(
    string SchemaVersion,
    IReadOnlyList<CloudLiveUIPlannedAction> Actions);

public sealed record CloudLiveUIPlanResponse(
    string SchemaVersion,
    string RequestId,
    string TraceId,
    string Provider,
    CloudLiveUIPlan Plan);

public sealed record CloudSpeechSynthesisInput(
    string Text,
    string VoiceId = "af_heart",
    string Locale = "en-US",
    string Format = "wav");

public sealed record CloudSpeechSynthesisRequest(
    CloudSpeechSynthesisInput Input,
    string? RequestId = null,
    DateTimeOffset? ExpiresAtUtc = null);

public sealed record CloudSpeechSynthesisResult(
    string Format,
    int SampleRateHz,
    int Channels,
    string VoiceId,
    string Locale,
    string AudioBase64)
{
    private const int MaxBase64Length = 7_680_060;

    public byte[] GetAudioBytes()
    {
        if (string.IsNullOrWhiteSpace(AudioBase64) || AudioBase64.Length > MaxBase64Length)
        {
            throw new InvalidDataException("Audio payload is empty or exceeds the maximum supported size.");
        }

        return Convert.FromBase64String(AudioBase64);
    }
}

public sealed record CloudSpeechSynthesisResponse(
    string SchemaVersion,
    string RequestId,
    string TraceId,
    CloudSpeechSynthesisResult Result);
