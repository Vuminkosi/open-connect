using OpenConnect.Abstractions;

namespace OpenConnect.Core.Configuration;

public sealed class OpenConnectOptions
{
    public const string SectionName = "OpenConnect";
    /// <summary>Transport configuration.</summary>
    public TransportOptions Transport { get; } = new();
    /// <summary>Runtime configuration.</summary>
    public RuntimeOptions Runtime { get; } = new();
    public AgentRegistryOptions Agents { get; } = new();
    public PrivacyOptions Privacy { get; } = new();
}

/// <summary>Configures the message transport.</summary>
public sealed class TransportOptions
{
    /// <summary>Gets or sets the transport mode.</summary>
    public OpenConnectTransportMode Mode { get; set; } = OpenConnectTransportMode.InMemoryDevelopment;
}

/// <summary>Configures shared runtime resources.</summary>
public sealed class RuntimeOptions
{
    /// <summary>Gets or sets the optional instructions file path.</summary>
    public string? InstructionsPath { get; set; }
}

public sealed class AgentRegistryOptions
{
    public AgentExecutionMode DefaultExecutionMode { get; set; } = AgentExecutionMode.Interactive;
    public bool RegisterLeadAgent { get; set; } = true;
    /// <summary>Gets or sets the execution identity mode.</summary>
    public AgentIdentityMode IdentityMode { get; set; } = AgentIdentityMode.ActiveUser;
    public int MaxDelegationDepth { get; set; } = 2;
    public int MaxConcurrentSubagents { get; set; } = 3;
    public int MaxTotalSubagentsPerRun { get; set; } = 8;
    public int MaxTotalToolCalls { get; set; } = 20;
    public int MaxModelCalls { get; set; } = 12;
    /// <summary>Gets or sets the maximum token usage per run, measured as the ledger UTF-8 byte-count estimate. <see cref="long.MaxValue"/> means unlimited.</summary>
    public long MaxTokens { get; set; } = long.MaxValue;
    public int MaxQueuedAgents { get; set; } = int.MaxValue;
    public long MaxCostMicrocents { get; set; } = long.MaxValue;
    /// <summary>Gets or sets the metered cost rate in microcents per thousand tokens applied to
    /// model-call token estimates as <c>ceil(tokens * rate / 1000)</c>. Zero (the default) meters
    /// zero cost, so the cost dimension never denies on its own until a nonzero rate is configured.</summary>
    public long MicrocentsPerThousandTokens { get; set; } = 0;
    public TimeSpan MaxTurnDuration { get; set; } = TimeSpan.FromSeconds(120);
    public TimeSpan MaxToolDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan LongRunningThreshold { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan ProgressCoalescingInterval { get; set; } = TimeSpan.FromSeconds(1);
    public AgentOrchestrationMode OrchestrationMode { get; set; } = AgentOrchestrationMode.Direct;
    public AgentContextSharingMode ContextSharing { get; set; } = AgentContextSharingMode.TaskSummary;
    public AgentHistoryPolicy SessionHistory { get; set; } = AgentHistoryPolicy.RecentTurns;
    public AgentMemoryMode MemoryMode { get; set; } = AgentMemoryMode.SessionOnly;
    public SessionTurnConcurrency Concurrency { get; set; } = SessionTurnConcurrency.Serialize;
}

public sealed class PrivacyOptions
{
    public bool Enabled { get; set; } = true;
    public FirewallMode Mode { get; set; } = FirewallMode.StrictLocal;
    public FirewallFailureAction OnFailure { get; set; } = FirewallFailureAction.Deny;
    public bool RequireEgressValidation { get; set; } = true;
    public bool EnableTokenization { get; set; } = true;
    public bool EnableOutputInspection { get; set; } = true;
    public DetectorOptions Rules { get; } = new() { Enabled = true };
    public DetectorOptions GlinerPii { get; } = new();
    public DetectorOptions Gliner => GlinerPii;
    public DetectorOptions DebertaInjection { get; } = new();
    public DetectorOptions Deberta => DebertaInjection;
    public DetectorOptions Qwen3Guard { get; } = new();
    public DetectorOptions PaddleOcr { get; } = new();
    public DetectorOptions YuNet { get; } = new();
    /// <summary>Gets the security coverage policies.</summary>
    public SecurityPolicyOptions Policies { get; } = new();
}

/// <summary>Actions to apply when security coverage detects a condition.</summary>
public sealed class SecurityPolicyOptions
{
    /// <summary>Gets the action for structured PII.</summary>
    public SecurityPolicyAction StructuredPii { get; set; } = SecurityPolicyAction.Mask;
    /// <summary>Gets the optional action for semantic PII.</summary>
    public SecurityPolicyAction? SemanticPii { get; set; }
    /// <summary>Gets the optional action for prompt injection.</summary>
    public SecurityPolicyAction? PromptInjection { get; set; }
    /// <summary>Gets the optional action for content safety.</summary>
    public SecurityPolicyAction? ContentSafety { get; set; }
    /// <summary>Gets the optional action for image text.</summary>
    public SecurityPolicyAction? ImageText { get; set; }
    /// <summary>Gets the optional action for face detection.</summary>
    public SecurityPolicyAction? FaceDetection { get; set; }
    /// <summary>Gets or sets the action when required coverage is unavailable.</summary>
    public SecurityCoverageUnavailableAction UnsupportedRequiredCoverage { get; set; } = SecurityCoverageUnavailableAction.Block;
}

public sealed class DetectorOptions
{
    public bool Enabled { get; set; }
    public string? ModelId { get; set; }
    public string? Revision { get; set; }
    public string? ModelDirectory { get; set; }
    public DetectorDevice Device { get; set; } = DetectorDevice.Cpu;
    public DetectorLoadPolicy LoadPolicy { get; set; } = DetectorLoadPolicy.Startup;
    public int MaxConcurrentRequests { get; set; } = 1;
    public int? MemoryBudgetMb { get; set; }
    public double? Threshold { get; set; }
}

public sealed class OpenConnectObservabilityOptions
{
    public bool EnableTracing { get; set; } = true;
    public bool EnableMetrics { get; set; } = true;
    public bool EnableModelUsageMetering { get; set; } = true;
    public bool RecordSensitiveContent { get; set; } = false;
}

public sealed class OpenConnectModelProviderOptions
{
    public string DefaultProvider { get; set; } = string.Empty;
    public string DefaultModel { get; set; } = string.Empty;
    public bool RequireVerifiedCapabilities { get; set; } = true;
    public bool AllowUnapprovedFallback { get; set; } = false;
}

public sealed class OpenConnectExecutionProfileOptions
{
    public bool EnableDirectAgent { get; set; } = true;
    public bool EnableHarnessAgent { get; set; } = true;
    public bool EnableWorkflow { get; set; } = true;
    public bool EnableDurableWorkflow { get; set; } = false;
}

public sealed class OpenConnectNatsOptions
{
    public string Url { get; set; } = string.Empty;
    public bool EnableJetStream { get; set; } = true;
    public bool EnableOutboxIntegration { get; set; } = true;
    public int MaxMessageBytes { get; set; } = 64 * 1024;
}

public sealed class OpenConnectMcpOptions
{
    public bool Enabled { get; set; } = true;
    public McpToolAccess DefaultToolAccess { get; set; } = McpToolAccess.Deny;
    public McpAuthorizationMode Authorization { get; set; } = McpAuthorizationMode.PerInvocation;
}

public sealed class OpenConnectMcpServiceOptions
{
    public string Name { get; set; } = string.Empty;
    public Uri? Endpoint { get; set; }
    public ISet<string> AllowedTools { get; } = new HashSet<string>(StringComparer.Ordinal);
}

public sealed class VoiceVadOptions
{
    public OpenConnect.Core.VadLocation Location { get; set; } = OpenConnect.Core.VadLocation.Hybrid;
    public OpenConnect.Core.VadEngine ServerEngine { get; set; } = OpenConnect.Core.VadEngine.SileroOnnx;
}

public sealed class OpenConnectVoiceOptions
{
    public bool EnableLiveTranscription { get; set; } = true;
    public bool EnableRecordedTranscription { get; set; } = true;
    public bool EnableSpeechOutput { get; set; } = true;
    public VoiceVadOptions Vad { get; } = new();
}

public sealed class ServerVadSileroOptions
{
    public string ModelPath { get; set; } = "/models/silero/silero_vad_16k_op15.onnx";
    public bool VerifyModelChecksum { get; set; } = true;
    public string? ExpectedSha256 { get; set; }
    public int SampleRate { get; set; } = 16_000;
    public int FrameSamples { get; set; } = 512;
    public float SpeechThreshold { get; set; } = 0.5f;
}

public sealed class CloudTranscriptionOptions
{
    public string Provider { get; set; } = string.Empty;
    public CloudTranscriptionMode Mode { get; set; } = CloudTranscriptionMode.Endpointing;
    public bool RequireApprovedAudioEgress { get; set; } = true;
    public bool ApprovedAudioEgressPolicy { get; set; } = false;
    public bool ProviderCapabilitiesVerified { get; set; } = false;
}

public sealed class SecurityWorkerOptions
{
    public Uri? Endpoint { get; set; }
    public bool Required { get; set; } = true;
    public bool RequireAuthenticatedTransport { get; set; } = true;
    public WorkerEngine Engine { get; set; } = WorkerEngine.Real;
    public string Environment { get; set; } = "Production";
    public DetectorOptions Rules { get; } = new() { Enabled = true };
    public DetectorOptions Gliner { get; } = new();
    public DetectorOptions GlinerPii => Gliner;
    public DetectorOptions Deberta { get; } = new();
    public DetectorOptions DebertaInjection => Deberta;
    public DetectorOptions Qwen3Guard { get; } = new();
    public DetectorOptions PaddleOcr { get; } = new();
    public DetectorOptions YuNet { get; } = new();
}

public sealed class ClassificationWorkerOptions
{
    public Uri? Endpoint { get; set; }
    public bool Required { get; set; } = false;
    public bool RequireAuthenticatedTransport { get; set; } = true;
    public WorkerEngine Engine { get; set; } = WorkerEngine.Real;
    public string Environment { get; set; } = "Production";
    public string? ModelId { get; set; }
    public string? ModelRevision { get; set; }
}

public sealed class TranslationWorkerOptions
{
    public Uri? Endpoint { get; set; }
    public bool Required { get; set; } = false;
    public bool RequireAuthenticatedTransport { get; set; } = true;
    public WorkerEngine Engine { get; set; } = WorkerEngine.Real;
    public string Environment { get; set; } = "Production";
    public string? ModelId { get; set; }
    public string? ModelRevision { get; set; }
    public bool LicenseApproved { get; set; } = true;
    public ISet<string> AllowedLanguages { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class InferenceWorkerOptions
{
    public Uri? Endpoint { get; set; }
    public bool Required { get; set; } = false;
    public bool UseAsDefaultModelProvider { get; set; } = false;
    public bool RequireAuthenticatedTransport { get; set; } = true;
    public WorkerEngine Engine { get; set; } = WorkerEngine.Real;
    public string Environment { get; set; } = "Production";
    public string? ModelId { get; set; }
    public string? ModelRevision { get; set; }
}

public sealed class TranscriptionWorkerOptions
{
    public Uri? Endpoint { get; set; }
    public bool Required { get; set; } = false;
    public bool UseAsDefaultTranscriber { get; set; } = false;
    public bool RequireAuthenticatedTransport { get; set; } = true;
    public WorkerEngine Engine { get; set; } = WorkerEngine.Real;
    public string Environment { get; set; } = "Production";
    public string? ModelId { get; set; }
    public string? ModelRevision { get; set; }
}

public sealed class RealtimeVoiceWorkerOptions
{
    public Uri? Endpoint { get; set; }
    public bool Required { get; set; } = false;
    public bool RequireAuthenticatedTransport { get; set; } = true;
    public WorkerEngine Engine { get; set; } = WorkerEngine.Real;
    public string Environment { get; set; } = "Production";
    public string? ModelId { get; set; }
    public string? ModelRevision { get; set; }
}

public sealed class KokoroWorkerOptions
{
    public Uri? Endpoint { get; set; }
    public string DefaultVoice { get; set; } = "af_heart";
    public string DefaultModel { get; set; } = "hexgrad/Kokoro-82M";
    public bool Required { get; set; } = false;
    public bool RequireAuthenticatedTransport { get; set; } = true;
    public WorkerEngine Engine { get; set; } = WorkerEngine.Real;
    public string Environment { get; set; } = "Production";
    public string? ModelRevision { get; set; }
}

public sealed class MmsTtsWorkerOptions
{
    public Uri? Endpoint { get; set; }
    public bool Required { get; set; } = false;
    public bool RequireAuthenticatedTransport { get; set; } = true;
    public bool LicenseApproved { get; set; } = true;
    public WorkerEngine Engine { get; set; } = WorkerEngine.Real;
    public string Environment { get; set; } = "Production";
    public string? ModelId { get; set; }
    public string? ModelRevision { get; set; }
    public ISet<string> Languages { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class PlaywrightWorkerOptions
{
    public Uri? Endpoint { get; set; }
    public bool Enabled { get; set; } = true;
    public bool RequireApprovedActions { get; set; } = true;
    public bool Required { get; set; } = false;
    public bool RequireAuthenticatedTransport { get; set; } = true;
}

public sealed class OperationalAgentsWorkerOptions
{
    public WorkerHostingMode HostingMode { get; set; } = WorkerHostingMode.Embedded;
    public bool EnableScheduling { get; set; } = true;
    public bool EnableDurableRecovery { get; set; } = true;
    public bool StandaloneDeployed { get; set; } = false;
    public string HostId { get; set; } = "operational-agents";
}

public sealed class OpenConnectOutboxOptions
{
    public WorkerHostingMode HostingMode { get; set; } = WorkerHostingMode.Embedded;
    public bool EnableJetStreamPublishing { get; set; } = true;
    public bool EnableFencedClaims { get; set; } = true;
    public bool StandaloneDeployed { get; set; } = false;
    public string HostId { get; set; } = "outbox";
}
