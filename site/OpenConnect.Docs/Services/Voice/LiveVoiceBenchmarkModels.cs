namespace OpenConnect.Docs.Services.Voice;

/// <summary>
/// Represents a single latency or correctness metric comparing an engineering target
/// against measured telemetry. Never stores raw transcripts or raw audio.
/// </summary>
public sealed record LiveVoiceMetricRow(
    string MetricName,
    string TargetP50,
    string TargetP95,
    string MeasuredP50,
    string MeasuredP95,
    string Notes);

/// <summary>
/// Represents a single stage in the end-to-end voice turn waterfall.
/// </summary>
public sealed record LiveVoiceStageRow(
    string StageName,
    string IllustrativeBudget,
    string MeasuredLatency,
    string Detail);

/// <summary>
/// Represents sanitized readiness status for a voice pipeline component.
/// </summary>
public sealed record LiveVoiceProviderStatus(
    string Role,
    string Provider,
    string Mode,
    string State,
    string Detail);

/// <summary>
/// Sanitized snapshot rendered by the Live Voice Benchmark Dashboard.
/// Separates engineering targets from measured results and never includes raw transcripts or audio.
/// </summary>
public sealed record LiveVoiceBenchmarkSnapshot(
    bool HasMeasuredRun,
    bool IsSyntheticHarness,
    int SampleCount,
    DateTimeOffset? MeasuredAtUtc,
    string DeploymentMode,
    string HardwareProfile,
    string RunClassificationLabel,
    int? UnderrunCount,
    int? QueuePressureCount,
    IReadOnlyList<LiveVoiceMetricRow> Metrics,
    IReadOnlyList<LiveVoiceStageRow> Stages,
    IReadOnlyList<LiveVoiceProviderStatus> Providers);
