using System.Collections.Immutable;
using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>An immutable, bounded piece of context made available to a child agent.</summary>
/// <param name="Id">Stable identifier for this item.</param>
/// <param name="DataClass">Classification assigned to the item.</param>
/// <param name="SummaryOrRef">Safe summary or reference, never a mutable session object.</param>
/// <param name="Citation">Optional provenance citation.</param>
/// <param name="TokenEstimate">Estimated token cost.</param>
/// <param name="ContainsSecret">Whether the source is known to contain secret material.</param>
/// <param name="ToolControl">Whether the item contains tool-control instructions or payload.</param>
public sealed record ContextItem(string Id, string DataClass, string SummaryOrRef, string? Citation,
    int TokenEstimate, bool ContainsSecret = false, bool ToolControl = false);

/// <summary>A narrowed, immutable context grant for one child execution.</summary>
/// <param name="ContextId">Unique projection identifier.</param>
/// <param name="SessionId">Owning session identifier.</param>
/// <param name="ParentExecutionId">Execution that authorized the projection.</param>
/// <param name="Version">Append-only projection revision.</param>
/// <param name="Mode">Approved context sharing mode.</param>
/// <param name="DataClass">Maximum permitted data classification.</param>
/// <param name="Items">Immutable approved context items.</param>
/// <param name="AllowedAgentIds">Agents permitted to receive the projection.</param>
/// <param name="ExpiresAt">UTC expiry time.</param>
/// <param name="TenantScope">Tenant scope inherited from the parent.</param>
/// <param name="PolicyRevision">Policy revision inherited from the parent.</param>
/// <param name="SourceLineage">Execution lineage for the projection.</param>
/// <param name="AllowedToolIds">Tools permitted by the child grant.</param>
/// <param name="AllowedScopes">Narrowed authority scopes for the child.</param>
/// <param name="TokenBudget">Maximum estimated token count.</param>
public sealed record AgentContextProjection(Ulid ContextId, SessionId SessionId, ExecutionId ParentExecutionId,
    int Version, AgentContextSharingMode Mode, string DataClass, ImmutableArray<ContextItem> Items,
    ImmutableArray<AgentId> AllowedAgentIds, DateTimeOffset ExpiresAt, string TenantScope, long PolicyRevision,
    ImmutableArray<ExecutionId> SourceLineage, ImmutableArray<ToolId> AllowedToolIds,
    ImmutableArray<string> AllowedScopes, int TokenBudget);
