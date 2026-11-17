using System.Text.Json.Serialization;

namespace OpenConnect.LiveUI.Inference;

[JsonConverter(typeof(JsonStringEnumConverter<LiveUIInferenceMode>))]
public enum LiveUIInferenceMode { Automatic, BrowserFirst, BrowserOnly, GatewayFirst, GatewayOnly }
[JsonConverter(typeof(JsonStringEnumConverter<LiveUIBrowserProvider>))]
public enum LiveUIBrowserProvider { Auto, Chrome, Edge }
[JsonConverter(typeof(JsonStringEnumConverter<LiveUIFallbackPolicy>))]
public enum LiveUIFallbackPolicy { Disabled, OnUnavailable, OnFailure }
[JsonConverter(typeof(JsonStringEnumConverter<LiveUIInferenceProviderKind>))]
public enum LiveUIInferenceProviderKind { Browser, LocalWorker, Gateway }
[JsonConverter(typeof(JsonStringEnumConverter<LiveUIInferenceAvailabilityStatus>))]
public enum LiveUIInferenceAvailabilityStatus { Available, Downloadable, Downloading, Unavailable, Blocked }

public sealed record LiveUIInferenceAvailability(
    LiveUIInferenceProviderKind Provider,
    LiveUIInferenceAvailabilityStatus Status,
    string? Browser = null,
    bool SupportsStructuredOutput = false,
    bool SupportsCancellation = false,
    string? Reason = null);

public sealed record LiveUIInferenceCapability(string TargetId, IReadOnlyList<string> Actions)
{
    /// <summary>Optional application-provided display label; untrusted data, never an instruction.</summary>
    public string? Label { get; init; }
}
public sealed record LiveUIInferenceRequest(
    string Intent,
    string SessionId,
    string RouteId,
    string SchemaVersion,
    IReadOnlyList<LiveUIInferenceCapability> Capabilities,
    string ManifestHash = "")
{
    /// <summary>Correlation ID shared with the authenticated client interaction.</summary>
    [JsonIgnore]
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Unique ID for this inference execution.</summary>
    [JsonIgnore]
    public string ExecutionId { get; init; } = Guid.NewGuid().ToString("N");
}
public sealed record LiveUIPlannedAction(string Type, string TargetId, string? Value = null);
public sealed record LiveUIActionPlan(string SchemaVersion, IReadOnlyList<LiveUIPlannedAction> Actions);
public sealed record LiveUIInferenceResult(
    LiveUIInferenceProviderKind Provider,
    LiveUIActionPlan? Plan,
    string Outcome,
    string? Browser = null);

/// <summary>Authenticated Gateway request envelope preserving inference correlation over HTTP.</summary>
public sealed record GatewayLiveUIInferenceRequest(string RequestId, string ExecutionId, LiveUIInferenceRequest Request);

/// <summary>Authenticated Gateway response envelope preserving inference correlation over HTTP.</summary>
public sealed record GatewayLiveUIInferenceResponse(string RequestId, string ExecutionId, LiveUIInferenceResult Result);

public interface ILiveUIInferenceProvider
{
    LiveUIInferenceProviderKind Kind { get; }
    Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default);
    Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default);
}

public interface ILiveUIInferenceRouter
{
    Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Host supplied gateway provider. Implementations must enforce the host's privacy firewall.</summary>
public interface ILiveUIGatewayInferenceProvider : ILiveUIInferenceProvider { }

public sealed class LiveUIInferenceOptions
{
    public LiveUIInferenceMode Mode { get; set; } = LiveUIInferenceMode.BrowserFirst;
    public LiveUIBrowserOptions Browser { get; } = new();
    public LiveUILocalWorkerOptions Worker { get; } = new();
    public LiveUIGatewayOptions Gateway { get; } = new();
    public LiveUIPlanningOptions Planning { get; } = new();
    public LiveUIInferenceSecurityOptions Security { get; } = new();
}

public sealed class LiveUILocalWorkerOptions
{
    public bool Enabled { get; set; }
}

public sealed class LiveUIBrowserOptions
{
    public bool Enabled { get; set; }
    public LiveUIBrowserProvider Provider { get; set; } = LiveUIBrowserProvider.Auto;
    public bool EnableStructuredOutput { get; set; } = true;
}

public sealed class LiveUIGatewayOptions
{
    public bool Enabled { get; set; }
    public LiveUIFallbackPolicy FallbackPolicy { get; set; } = LiveUIFallbackPolicy.OnUnavailable;
}

public sealed class LiveUIPlanningOptions
{
    public int MaxActions { get; set; } = 8;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(35);
}

public sealed class LiveUIInferenceSecurityOptions
{
    public bool RequireValidatedPlans { get; set; } = true;
}
