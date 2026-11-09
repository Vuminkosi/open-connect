using Microsoft.Agents.AI;
using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>Result and lineage metadata from a child execution.</summary>
/// <param name="AgentId">Identifier of the child agent.</param>
/// <param name="ExecutionId">Identifier of the child execution.</param>
/// <param name="Response">Response returned by the child.</param>
/// <param name="Order">Original position of the child in the request.</param>
/// <param name="ParentExecutionId">Parent execution identifier from the canonical grant.</param>
/// <param name="SourceSequence">Monotonic source position used for deterministic merging.</param>
/// <param name="TenantScope">Tenant authority scope of the child.</param>
/// <param name="EffectiveScopes">Effective grant scopes of the child.</param>
public sealed record ChildResult(AgentId AgentId, ExecutionId ExecutionId, AgentResponse Response, int Order,
    Guid ParentExecutionId, long SourceSequence, string TenantScope, IReadOnlySet<string> EffectiveScopes);

/// <summary>Deterministically ordered merge output including rejected child results.</summary>
/// <param name="Results">Accepted results.</param>
/// <param name="Quarantined">Rejected results and reasons.</param>
/// <param name="RequiresLeadSynthesis">Whether a lead agent must synthesize accepted results.</param>
/// <param name="Audit">Deterministic merge audit description.</param>
public sealed record MergedAgentResult(IReadOnlyList<ChildResult> Results,
    IReadOnlyList<QuarantinedChildResult> Quarantined, bool RequiresLeadSynthesis, MergeAudit Audit);

/// <summary>A child result excluded from the merge.</summary>
/// <param name="Result">Rejected result.</param>
/// <param name="Reason">Safe validation category.</param>
public sealed record QuarantinedChildResult(ChildResult Result, string Reason);

/// <summary>Audit metadata for a result merge.</summary>
/// <param name="Mode">Selected merge strategy.</param>
/// <param name="InputCount">Number of candidate results.</param>
/// <param name="AcceptedCount">Number of accepted results.</param>
/// <param name="ParentExecutionId">Expected parent lineage.</param>
/// <param name="SourceSequences">Accepted source sequence in merge order.</param>
public sealed record MergeAudit(AgentResultMergeMode Mode, int InputCount, int AcceptedCount,
    Guid ParentExecutionId, IReadOnlyList<long> SourceSequences);
