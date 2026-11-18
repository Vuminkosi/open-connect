using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>Encapsulates all 7 restriction attributes for a child context grant.</summary>
/// <param name="ChildAgentId">Target child identity.</param>
/// <param name="TenantScope">Target child tenant scope.</param>
/// <param name="DataClass">Target child data classification.</param>
/// <param name="PurposeScope">Target child purpose/authority scope.</param>
/// <param name="DataZone">Target child data zone.</param>
/// <param name="ModelRoute">Target child model route or tool authorization.</param>
/// <param name="DelegatedTokenBudget">Target child delegated token budget.</param>
public sealed record ChildContextRestrictionRequest(
    AgentId ChildAgentId,
    string TenantScope,
    string DataClass,
    string PurposeScope,
    string DataZone,
    string ModelRoute,
    long DelegatedTokenBudget);

/// <summary>Enforces per-attribute boundaries (identity, tenant, classification, purpose, data zone, model route, token budget) on child context.</summary>
public static class ChildContextRestrictionGate
{
    /// <summary>Validates all 7 restriction attributes against parent authorization context, failing closed on any violation.</summary>
    /// <param name="parentContext">Parent execution context.</param>
    /// <param name="parentDataZone">Parent data zone (e.g. "secure-zone-1").</param>
    /// <param name="parentModelRoute">Parent authorized model route or default route.</param>
    /// <param name="allowedDataClasses">Authorized data classifications for projection.</param>
    /// <param name="allowedChildAgents">Agents authorized to receive delegation.</param>
    /// <param name="request">The child restriction parameters.</param>
    /// <exception cref="ArgumentNullException">Thrown when required context is null.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when identity, tenant, classification, purpose, data zone, or route is unauthorized.</exception>
    /// <exception cref="InvalidOperationException">Thrown when token budget exceeds available parent budget.</exception>
    public static void ValidateChildRestriction(
        AgentExecutionContext parentContext,
        string parentDataZone,
        string parentModelRoute,
        IReadOnlySet<string> allowedDataClasses,
        IReadOnlySet<AgentId> allowedChildAgents,
        ChildContextRestrictionRequest request)
    {
        ArgumentNullException.ThrowIfNull(parentContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDataZone);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentModelRoute);
        ArgumentNullException.ThrowIfNull(allowedDataClasses);
        ArgumentNullException.ThrowIfNull(allowedChildAgents);
        ArgumentNullException.ThrowIfNull(request);

        parentContext.CancellationToken.ThrowIfCancellationRequested();

        // 1. Identity restriction
        if (!allowedChildAgents.Contains(request.ChildAgentId))
            throw new UnauthorizedAccessException($"Child agent '{request.ChildAgentId}' is not authorized for delegation.");

        // 2. Tenant restriction
        if (!string.Equals(parentContext.TenantScope, request.TenantScope, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Cross-tenant context delegation is denied. Tenant scope must match parent.");

        // 3. Classification restriction
        if (!allowedDataClasses.Contains(request.DataClass))
            throw new UnauthorizedAccessException($"Data classification '{request.DataClass}' is not authorized for child projection.");

        // 4. Purpose / authority scope restriction
        if (!string.Equals(parentContext.PrincipalOrWorkloadScope, request.PurposeScope, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Child context purpose/scope cannot widen parent principal or workload scope.");

        // 5. Data zone restriction
        if (!string.Equals(parentDataZone, request.DataZone, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Child data zone '{request.DataZone}' does not match authorized parent zone '{parentDataZone}'.");

        // 6. Model route restriction
        if (!string.Equals(parentModelRoute, request.ModelRoute, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Child model route '{request.ModelRoute}' exceeds parent authorized route '{parentModelRoute}'.");

        // 7. Delegated token budget restriction
        if (request.DelegatedTokenBudget < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Delegated token budget must be non-negative.");

        if (parentContext.RemainingBudgets.TryGetValue("tokens", out var remainingTokens) && request.DelegatedTokenBudget > remainingTokens)
            throw new InvalidOperationException($"Delegated token budget {request.DelegatedTokenBudget} exceeds parent remaining token budget {remainingTokens}.");
    }
}
