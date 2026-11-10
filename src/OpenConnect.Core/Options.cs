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
    public DetectorOptions Rules { get; } = new() { Enabled = true };
    public DetectorOptions GlinerPii { get; } = new();
    public DetectorOptions DebertaInjection { get; } = new();
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
