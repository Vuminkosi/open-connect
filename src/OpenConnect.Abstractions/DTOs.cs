using System.Text.Json.Serialization;

namespace OpenConnect.Abstractions;

[JsonConverter(typeof(KebabCaseStringEnumConverter<CapabilityKind>))]
public enum CapabilityKind { Security, Model, Tool, Transport }
public sealed record AgentDefinition(AgentId Id, string Name)
{
    public string Version { get; init; } = "1";
    public AgentExecutionMode ExecutionMode { get; init; } = AgentExecutionMode.Interactive;
    public AgentIdentityMode IdentityMode { get; init; } = AgentIdentityMode.ActiveUser;
    public string InstructionsRevision { get; init; } = "1";
    public ModelRequirement? ModelRequirement { get; init; }
    public OrchestrationPolicy Orchestration { get; init; } = new();
    public AgentToolPolicy ToolPolicy { get; init; } = new();
    public AgentContextPolicy ContextPolicy { get; init; } = new();
    public AgentMemoryPolicy MemoryPolicy { get; init; } = new();
    public AgentBudget Budget { get; init; } = new();
    public string PolicyRevision { get; init; } = "1";
}

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
    int MaxTotalAgents = 0);

/// <summary>Authority and residual run budget for one parent-to-child delegation.</summary>
public sealed record DelegationGrant(
    AgentId ParentAgentId,
    AgentId ChildAgentId,
    int ChildDepth,
    IReadOnlySet<ToolId> EffectiveToolIds,
    IReadOnlySet<string> EffectiveScopes,
    AgentBudget RemainingBudget,
    Guid ParentExecutionId,
    string PolicyRevision);
public sealed record ToolDescriptor(ToolId Id, string Name, AgentId AgentId);
public sealed record CapabilityDescriptor(CapabilityId Id, CapabilityKind Kind, DetectorState State);
public sealed record TaskAcceptance(TaskId TaskId, TenantId TenantId, PrincipalId PrincipalId, string IdempotencyKey);
public sealed record TaskRecord(TaskId Id, TenantId TenantId, PrincipalId PrincipalId, RunState State);
public sealed record EventRecord(SessionId SessionId, long Sequence, string Type, string Data);
public sealed record OutboxEntry(Guid Id, string Type, string Payload);

/// <summary>Stable, server-defined policy associated with a tool.</summary>
public sealed record ToolPolicy(ToolId ToolId, ToolImpact Impact, ToolIdempotency Idempotency,
    ToolExecutionMode ExecutionMode, ToolAcknowledgementMode AcknowledgementMode,
    ToolProgressMode ProgressMode, ToolApprovalMode ApprovalMode);
