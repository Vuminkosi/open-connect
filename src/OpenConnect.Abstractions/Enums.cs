using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Abstractions;

/// <summary>Converts enum member names to strict kebab-case JSON strings.</summary>
public sealed class KebabCaseStringEnumConverter<TEnum> : JsonStringEnumConverter<TEnum> where TEnum : struct, Enum
{
    public KebabCaseStringEnumConverter() : base(KebabCaseNamingPolicy.Instance, allowIntegerValues: false) { }
}

internal sealed class KebabCaseNamingPolicy : JsonNamingPolicy
{
    public static KebabCaseNamingPolicy Instance { get; } = new();
    public override string ConvertName(string name)
    {
        var result = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]))) result.Append('-');
            result.Append(char.ToLowerInvariant(c));
        }
        return result.ToString();
    }
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<OpenConnectTransportMode>))]
public enum OpenConnectTransportMode { JetStream, InMemoryDevelopment }
[JsonConverter(typeof(KebabCaseStringEnumConverter<AgentExecutionMode>))]
public enum AgentExecutionMode { Interactive, Operational }
[JsonConverter(typeof(KebabCaseStringEnumConverter<AgentActivationMode>))]
public enum AgentActivationMode { OnDemand, Scheduled, EventTriggered, Continuous }
[JsonConverter(typeof(KebabCaseStringEnumConverter<AgentRunState>))]
public enum AgentRunState { Accepted, Preparing, Running, AwaitingUser, AwaitingApproval, Paused, Completed, Failed, CancelRequested, Cancelled, OutcomeUnknown }
// Legacy task-store state retained for existing persistence and hosting callers.
[JsonConverter(typeof(KebabCaseStringEnumConverter<RunState>))]
public enum RunState { Registered, Queued, Starting, Running, Waiting, Paused, Completed, Failed, Cancelled, Stale }
[JsonConverter(typeof(KebabCaseStringEnumConverter<AgentIdentityMode>))]
public enum AgentIdentityMode { ActiveUser, WorkloadIdentity }
[JsonConverter(typeof(KebabCaseStringEnumConverter<ToolImpact>))]
public enum ToolImpact { ReadOnly, Write, ExternalSideEffect }
[JsonConverter(typeof(KebabCaseStringEnumConverter<ModelExecutionLocation>))]
public enum ModelExecutionLocation { LocalOnly, SelfHosted, RemoteAllowed }
[JsonConverter(typeof(KebabCaseStringEnumConverter<FirewallMode>))]
public enum FirewallMode { StrictLocal, LocalOnly, PolicyControlled, Disabled }
[JsonConverter(typeof(KebabCaseStringEnumConverter<FirewallFailureAction>))]
public enum FirewallFailureAction { Deny, LocalOnlyIfVerified }
[JsonConverter(typeof(KebabCaseStringEnumConverter<SecurityDetectorKind>))]
public enum SecurityDetectorKind { Rules, GlinerPii, DebertaInjection, Qwen3Guard, PaddleOcr, YuNet }
[JsonConverter(typeof(KebabCaseStringEnumConverter<DetectorLoadPolicy>))]
public enum DetectorLoadPolicy { Startup, LazyOnFirstUse }
[JsonConverter(typeof(KebabCaseStringEnumConverter<DetectorDevice>))]
public enum DetectorDevice { Auto, Cpu, Cuda }
[JsonConverter(typeof(KebabCaseStringEnumConverter<DetectorState>))]
public enum DetectorState { Disabled, Loading, Ready, Degraded, Failed }
[JsonConverter(typeof(KebabCaseStringEnumConverter<SecurityCoverage>))]
public enum SecurityCoverage { StructuredPii, SemanticPii, PromptInjection, ContentSafety, ImageText, FaceDetection }
[JsonConverter(typeof(KebabCaseStringEnumConverter<SecurityPolicyAction>))]
public enum SecurityPolicyAction { Allow, Mask, Redact, Block, Review }
[JsonConverter(typeof(KebabCaseStringEnumConverter<SecurityCoverageUnavailableAction>))]
public enum SecurityCoverageUnavailableAction { Block, Review, LocalOnlyIfVerified }
[JsonConverter(typeof(KebabCaseStringEnumConverter<SchedulingMisfirePolicy>))]
public enum SchedulingMisfirePolicy { Skip, RunOnce }
[JsonConverter(typeof(KebabCaseStringEnumConverter<OperationalConcurrencyPolicy>))]
public enum OperationalConcurrencyPolicy { Forbid, Queue, ReplaceAfterFence }
[JsonConverter(typeof(KebabCaseStringEnumConverter<OperationalRecoveryPolicy>))]
public enum OperationalRecoveryPolicy { RetryWithBackoff, PauseForReview, Disable }

[JsonConverter(typeof(KebabCaseStringEnumConverter<AgentOrchestrationMode>))]
public enum AgentOrchestrationMode { Direct, Sequential, Parallel, Handoff, SupervisorDelegation }
[JsonConverter(typeof(KebabCaseStringEnumConverter<AgentContextSharingMode>))]
public enum AgentContextSharingMode { None, TaskSummary, RelevantHistory, ApprovedArtifacts, ScopedMemory }
[JsonConverter(typeof(KebabCaseStringEnumConverter<AgentMemoryMode>))]
public enum AgentMemoryMode { Disabled, SessionOnly, DurableScoped, SemanticOptional }
[JsonConverter(typeof(KebabCaseStringEnumConverter<SessionTurnConcurrency>))]
public enum SessionTurnConcurrency { Serialize, Queue, RejectConflict }
[JsonConverter(typeof(KebabCaseStringEnumConverter<AgentResultMergeMode>))]
public enum AgentResultMergeMode { LeadSynthesizes, OrderedTypedResults, ExplicitReducer }
[JsonConverter(typeof(KebabCaseStringEnumConverter<ToolIdempotency>))]
public enum ToolIdempotency { SafeToRetry, IdempotencyKeyRequired, NonIdempotent }
[JsonConverter(typeof(KebabCaseStringEnumConverter<ToolExecutionMode>))]
public enum ToolExecutionMode { Inline, BackgroundDurable }
[JsonConverter(typeof(KebabCaseStringEnumConverter<ToolAcknowledgementMode>))]
public enum ToolAcknowledgementMode { Automatic, Always, NeverForShortTools }
[JsonConverter(typeof(KebabCaseStringEnumConverter<ToolProgressMode>))]
public enum ToolProgressMode { None, Reported, HeartbeatOnly, ReportedAndHeartbeat }
[JsonConverter(typeof(KebabCaseStringEnumConverter<ToolApprovalMode>))]
public enum ToolApprovalMode { None, PerInvocation, PerInvocationOnSideEffect }
[JsonConverter(typeof(KebabCaseStringEnumConverter<ToolExecutionState>))]
public enum ToolExecutionState { Proposed, AwaitingApproval, Accepted, Queued, Starting, Running, Finalizing, Completed, Failed, CancelRequested, Cancelled, OutcomeUnknown }
[JsonConverter(typeof(KebabCaseStringEnumConverter<ToolProgressKind>))]
public enum ToolProgressKind { Phase, MeasuredProgress, Heartbeat, DiagnosticSummary }
[JsonConverter(typeof(KebabCaseStringEnumConverter<AgentHistoryPolicy>))]
public enum AgentHistoryPolicy { RecentTurns, BoundedSummary, FullWithinBudget, ProviderManaged }
[JsonConverter(typeof(KebabCaseStringEnumConverter<AgentMemoryWritePolicy>))]
public enum AgentMemoryWritePolicy { DenyByDefault, ExplicitApproved, ApplicationPolicy }
