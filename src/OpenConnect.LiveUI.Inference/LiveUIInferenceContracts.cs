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

public sealed record LiveUIInferenceCapability(string TargetId, IReadOnlyList<string> Actions);
public sealed record LiveUIInferenceRequest(
    string Intent,
    string SessionId,
    string RouteId,
    string SchemaVersion,
    IReadOnlyList<LiveUIInferenceCapability> Capabilities,
    string ManifestHash = "");
public sealed record LiveUIPlannedAction(string Type, string TargetId, string? Value = null);
public sealed record LiveUIActionPlan(string SchemaVersion, IReadOnlyList<LiveUIPlannedAction> Actions);
public sealed record LiveUIInferenceResult(
    LiveUIInferenceProviderKind Provider,
    LiveUIActionPlan? Plan,
    string Outcome,
    string? Browser = null);

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
    public bool Enabled { get; set; } = true;
    public LiveUIBrowserProvider Provider { get; set; } = LiveUIBrowserProvider.Auto;
    public bool EnableStructuredOutput { get; set; } = true;
}

public sealed class LiveUIGatewayOptions
{
    public bool Enabled { get; set; } = true;
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
