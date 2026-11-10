using System.Text.Json.Serialization;

namespace OpenConnect.Abstractions;

[JsonConverter(typeof(KebabCaseStringEnumConverter<CapabilityKind>))]
public enum CapabilityKind { Security, Model, Tool, Transport }
public sealed record AgentDefinition(AgentId Id, string Name)
{
    public string Version { get; init; } = "1";
    public AgentExecutionMode ExecutionMode { get; init; } = AgentExecutionMode.Interactive;
    public AgentExecutionProfile ExecutionProfile { get; init; } = AgentExecutionProfile.Direct;
    public AgentIdentityMode IdentityMode { get; init; } = AgentIdentityMode.ActiveUser;
    public string InstructionsRevision { get; init; } = "1";
    public string? InstructionsPath { get; init; }
    public bool EnablePlanning { get; init; }
    public bool EnableTodoTracking { get; init; }
    public bool EnableContextCompaction { get; init; }
    public ModelRequirement? ModelRequirement { get; init; }
    public OrchestrationPolicy Orchestration { get; init; } = new();
    public AgentToolPolicy ToolPolicy { get; init; } = new();
    public AgentContextPolicy ContextPolicy { get; init; } = new();
    public AgentMemoryPolicy MemoryPolicy { get; init; } = new();
    public AgentBudget Budget { get; init; } = new();
    public string PolicyRevision { get; init; } = "1";
    /// <summary>Activation and workload identity policy for operational agents; null for interactive definitions.</summary>
    public OperationalPolicy? Operational { get; init; }
}

/// <summary>Server-controlled activation and execution limits for an operational agent.</summary>
/// <param name="Activation">The event or schedule that may start the agent.</param>
/// <param name="WorkloadIdentityRef">Reference to a preconfigured workload identity.</param>
/// <param name="Cron">Cron schedule, required for scheduled activation.</param>
/// <param name="TimeZoneId">Time zone identifier for the schedule, required for scheduled activation.</param>
/// <param name="Misfire">Behavior when a scheduled occurrence is missed.</param>
/// <param name="Concurrency">Behavior when an activation overlaps an existing run.</param>
/// <param name="MaxParallelRuns">Maximum number of concurrent runs.</param>
/// <param name="MaxRunDuration">Optional maximum duration of one run.</param>
public sealed record OperationalPolicy(
    AgentActivationMode Activation = AgentActivationMode.OnDemand,
    string? WorkloadIdentityRef = null,
    string? Cron = null,
    string? TimeZoneId = null,
    SchedulingMisfirePolicy Misfire = SchedulingMisfirePolicy.Skip,
    OperationalConcurrencyPolicy Concurrency = OperationalConcurrencyPolicy.Forbid,
    int MaxParallelRuns = 1,
    TimeSpan? MaxRunDuration = null);

/// <summary>Requirements imposed on the model selected for an agent.</summary>
public sealed record ModelRequirement(string? ModelId = null, ModelExecutionLocation? Location = null);

/// <summary>Server-controlled delegation and execution graph policy.</summary>
public sealed record OrchestrationPolicy(
    AgentOrchestrationMode Mode = AgentOrchestrationMode.Direct,
    int MaxDepth = 2,
    int MaxChildren = 0,
    IReadOnlyList<AgentId>? AllowedChildAgents = null,
    AgentResultMergeMode ResultMergeMode = AgentResultMergeMode.LeadSynthesizes);

/// <summary>Tool authority granted to an agent definition.</summary>
public sealed record AgentToolPolicy(
    IReadOnlyList<ToolId>? AllowedToolIds = null,
    IReadOnlyList<string>? AllowedScopes = null);

/// <summary>Context visibility and size policy for an agent.</summary>
public sealed record AgentContextPolicy(
    AgentContextSharingMode SharingMode = AgentContextSharingMode.TaskSummary,
    int MaxInputTokens = 0,
    int MaxOutputTokens = 0);

/// <summary>Memory capabilities granted to an agent.</summary>
public sealed record AgentMemoryPolicy(
    AgentMemoryMode Mode = AgentMemoryMode.SessionOnly,
    AgentMemoryWritePolicy WritePolicy = AgentMemoryWritePolicy.DenyByDefault);

/// <summary>Immutable upper bounds for one agent run; zero means no allowance.</summary>
public sealed record AgentBudget(
    int MaxToolCalls = 0,
    int MaxModelCalls = 0,
    long MaxTokens = 0,
    TimeSpan? MaxRuntime = null,
    int MaxConcurrentAgents = 0,
    int MaxQueuedAgents = 0,
    int MaxTotalAgents = 0,
    long MaxCostMicrocents = 0);

/// <summary>Authority and residual run budget for one parent-to-child delegation.</summary>
/// <remarks>The child's typed <c>ChildResult</c> is the delegation return contract.</remarks>
/// <param name="ParentAgentId">The parent agent identity.</param>
/// <param name="ChildAgentId">The authorized child agent identity.</param>
/// <param name="ChildDepth">The child's depth in the execution graph.</param>
/// <param name="EffectiveToolIds">Tools authorized for the child.</param>
/// <param name="EffectiveScopes">Scopes authorized for the child.</param>
/// <param name="RemainingBudget">The residual budget granted to the child.</param>
/// <param name="ParentExecutionId">The parent execution initiating delegation.</param>
/// <param name="PolicyRevision">The policy revision used for authorization.</param>
/// <param name="Reason">A sanitized short purpose or category; never a secret or prompt.</param>
/// <param name="ChildDefinitionRevision">The child agent definition version at authorization time.</param>
public sealed record DelegationGrant(
    AgentId ParentAgentId,
    AgentId ChildAgentId,
    int ChildDepth,
    IReadOnlySet<ToolId> EffectiveToolIds,
    IReadOnlySet<string> EffectiveScopes,
    AgentBudget RemainingBudget,
    Guid ParentExecutionId,
    string PolicyRevision,
    string Reason = "",
    string ChildDefinitionRevision = "1");
public sealed record ToolDescriptor(ToolId Id, string Name, AgentId AgentId);
public sealed record CapabilityDescriptor(CapabilityId Id, CapabilityKind Kind, DetectorState State);
public sealed record TaskAcceptance(TaskId TaskId, TenantId TenantId, PrincipalId PrincipalId, string IdempotencyKey);
// AgentRunState is the canonical public enum name used by the abstractions contract.
public sealed record TaskRecord(TaskId Id, TenantId TenantId, PrincipalId PrincipalId, AgentRunState State);
public sealed record EventRecord(SessionId SessionId, long Sequence, string Type, string Data);
public sealed record OutboxEntry(Guid Id, string Type, string Payload);

/// <summary>Stable, server-defined policy associated with a tool.</summary>
public sealed record ToolPolicy(ToolId ToolId, ToolImpact Impact, ToolIdempotency Idempotency,
    ToolExecutionMode ExecutionMode, ToolAcknowledgementMode AcknowledgementMode,
    ToolProgressMode ProgressMode, ToolApprovalMode ApprovalMode);

/// <summary>Canonical capability descriptor for one of the 11 OpenConnect worker kinds.</summary>
public sealed record WorkerCapabilityDescriptor(
    OpenConnectWorkerKind Kind,
    string ContractVersion,
    string ExecutionLocation,
    string EndpointOrHostId,
    WorkerReadiness Readiness,
    IReadOnlySet<string> CapabilityIds,
    IReadOnlySet<string> Modalities,
    IReadOnlySet<string> Languages,
    string? ModelId,
    string? ModelRevision,
    DateTimeOffset ExpiresAt);
