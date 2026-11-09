using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Catalog;

/// <summary>Authenticated server-side execution facts used to authorize a child grant.</summary>
/// <param name="AgentId">Registered parent agent identifier.</param>
/// <param name="ExecutionId">Identifier of the current execution.</param>
/// <param name="Depth">Current delegation depth.</param>
/// <param name="Ancestors">Agents already on the execution path.</param>
/// <param name="EffectiveToolIds">Tools currently authorized for the parent.</param>
/// <param name="EffectiveScopes">Scopes currently authorized for the parent.</param>
/// <param name="RemainingBudget">Remaining execution budget.</param>
/// <param name="ExecutionMode">Current execution mode.</param>
/// <param name="ChildCount">Number of children already requested.</param>
/// <param name="ActiveChildCount">Number of active children.</param>
/// <param name="QueuedChildCount">Number of queued children.</param>
public sealed record DelegationParentContext(
    AgentId AgentId,
    Guid ExecutionId,
    int Depth,
    IReadOnlyList<AgentId> Ancestors,
    IReadOnlySet<ToolId> EffectiveToolIds,
    IReadOnlySet<string> EffectiveScopes,
    AgentBudget RemainingBudget,
    AgentExecutionMode ExecutionMode,
    int ChildCount = 0,
    int ActiveChildCount = 0,
    int QueuedChildCount = 0);

/// <summary>Requested child capabilities and execution-mode transition.</summary>
/// <param name="ToolIds">Requested tool identifiers.</param>
/// <param name="Scopes">Requested authorization scopes.</param>
/// <param name="EnqueueCrossMode">Whether cross-mode delegation is explicitly enqueued.</param>
/// <param name="Reason">Optional short purpose or category, not a prompt or secret.</param>
public sealed record DelegationRequest(
    IReadOnlySet<ToolId> ToolIds,
    IReadOnlySet<string> Scopes,
    bool EnqueueCrossMode = false,
    string? Reason = null);

/// <summary>Creates a capability- and budget-restricted grant for an authorized child.</summary>
public sealed class DelegationGate
{
    private readonly IAgentDefinitionRegistry _catalog;
    private readonly int _configuredMaxDepth;

    /// <summary>Creates a delegation gate using the supplied agent registry and depth limit.</summary>
    /// <param name="catalog">Registry of agent definitions.</param>
    /// <param name="configuredMaxDepth">Configured maximum depth, capped by framework support.</param>
    public DelegationGate(IAgentDefinitionRegistry catalog, int configuredMaxDepth = AgentCatalog.FrameworkMaxDepth)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        if (configuredMaxDepth < 0) throw new ArgumentOutOfRangeException(nameof(configuredMaxDepth));
        _configuredMaxDepth = Math.Min(configuredMaxDepth, AgentCatalog.FrameworkMaxDepth);
    }

    /// <summary>Authorizes one child invocation, failing closed on graph, capability, or budget violations.</summary>
    /// <param name="parentCtx">Authenticated context of the parent execution.</param>
    /// <param name="childAgentId">Identifier of the requested child agent.</param>
    /// <param name="request">Requested capabilities and execution transition.</param>
    /// <returns>A grant containing only authorized child capabilities and budget.</returns>
    public DelegationGrant AuthorizeDelegation(
        DelegationParentContext parentCtx, AgentId childAgentId, DelegationRequest request)
    {
        ArgumentNullException.ThrowIfNull(parentCtx);
        ArgumentNullException.ThrowIfNull(request);
        var reason = SanitizeReason(request.Reason);
        var parent = _catalog.Get(parentCtx.AgentId)
            ?? throw new AgentCatalogException($"Parent agent '{parentCtx.AgentId}' is not registered.");
        var child = _catalog.Get(childAgentId)
            ?? throw new AgentCatalogException($"Child agent '{childAgentId}' is not registered.");
        var nextDepth = checked(parentCtx.Depth + 1);
        var maxDepth = Math.Min(_configuredMaxDepth, parent.Orchestration.MaxDepth);
        if (nextDepth > maxDepth) throw new DelegationDepthExceededException(nextDepth, maxDepth);
        if (parentCtx.Ancestors.Contains(childAgentId) || parentCtx.AgentId == childAgentId)
            throw new AgentCatalogException("Delegation cannot re-enter an agent already on the execution path.");
        if (parent.Orchestration.AllowedChildAgents?.Contains(childAgentId) != true)
            throw new AgentCatalogException("The parent agent is not authorized to delegate to this child.");
        if (parent.Orchestration.MaxChildren > 0 && parentCtx.ChildCount >= parent.Orchestration.MaxChildren)
            throw new AgentCatalogException("The parent agent has reached its child-count limit.");
        if (parentCtx.ActiveChildCount >= parentCtx.RemainingBudget.MaxConcurrentAgents)
            throw new AgentCatalogException("The run has reached its concurrent-agent limit.");
        if (parentCtx.QueuedChildCount >= parentCtx.RemainingBudget.MaxQueuedAgents)
            throw new AgentCatalogException("The run has reached its queued-agent limit.");
        if (parentCtx.RemainingBudget.MaxTotalAgents <= 0)
            throw new AgentCatalogException("The remaining run budget does not allow another child agent.");
        if (parentCtx.ExecutionMode != child.ExecutionMode && !request.EnqueueCrossMode)
            throw new AgentCatalogException("Cross-mode delegation requires an explicit enqueue request.");

        var parentTools = Intersect(parentCtx.EffectiveToolIds, parent.ToolPolicy.AllowedToolIds);
        var childTools = Intersect(parentTools, child.ToolPolicy.AllowedToolIds);
        var parentScopes = Intersect(parentCtx.EffectiveScopes, parent.ToolPolicy.AllowedScopes);
        var childScopes = Intersect(parentScopes, child.ToolPolicy.AllowedScopes);
        if (!request.ToolIds.IsSubsetOf(childTools) || !request.Scopes.IsSubsetOf(childScopes))
            throw new AgentCatalogException("Requested child capabilities exceed the parent's effective grant.");
        var effectiveTools = new HashSet<ToolId>(request.ToolIds);
        var effectiveScopes = new HashSet<string>(request.Scopes, StringComparer.Ordinal);
        var remaining = NarrowBudget(parentCtx.RemainingBudget, child.Budget);
        return new DelegationGrant(parent.Id, child.Id, nextDepth, effectiveTools, effectiveScopes,
            remaining, parentCtx.ExecutionId, child.PolicyRevision, reason, child.Version);
    }

    private static string SanitizeReason(string? reason)
    {
        var sanitized = reason?.Trim() ?? string.Empty;
        if (sanitized.Length > 500 || sanitized.Any(char.IsControl))
            throw new AgentCatalogException("Delegation reason must be at most 500 characters and contain no control characters.");
        return sanitized;
    }

    private static HashSet<T> Intersect<T>(IReadOnlySet<T> current, IReadOnlyList<T>? policy) where T : notnull
    {
        var allowed = policy is null ? current : new HashSet<T>(policy);
        return current.Where(allowed.Contains).ToHashSet();
    }

    private static AgentBudget NarrowBudget(AgentBudget current, AgentBudget child) => new(
        Math.Min(current.MaxToolCalls, child.MaxToolCalls),
        Math.Min(current.MaxModelCalls, child.MaxModelCalls),
        Math.Min(current.MaxTokens, child.MaxTokens),
        Min(current.MaxRuntime, child.MaxRuntime),
        Math.Min(current.MaxConcurrentAgents, child.MaxConcurrentAgents),
        Math.Min(current.MaxQueuedAgents, child.MaxQueuedAgents),
        Math.Min(Math.Max(0, current.MaxTotalAgents - 1), child.MaxTotalAgents));

    private static TimeSpan? Min(TimeSpan? left, TimeSpan? right) => (left, right) switch
    {
        (null, null) => null,
        (null, var r) => r,
        (var l, null) => l,
        _ => left < right ? left : right
    };
}
