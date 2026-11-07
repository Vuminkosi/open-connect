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
