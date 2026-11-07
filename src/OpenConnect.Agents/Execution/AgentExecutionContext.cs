using System.Collections.Immutable;
using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>Host-only authority and resource limits for one agent execution. Never serialize this as an authority token.</summary>
public sealed record AgentExecutionContext : IDisposable
{
    private CancellationTokenSource? _linkedCancellation;
    /// <summary>Gets the tenant authority scope for this execution.</summary>
    public required string TenantScope { get; init; }
    /// <summary>Gets the principal or workload authority scope.</summary>
    public required string PrincipalOrWorkloadScope { get; init; }
    /// <summary>Gets the identity kind used for authorization.</summary>
    public required AgentIdentityMode IdentityKind { get; init; }
    /// <summary>Gets the session identifier.</summary>
    public required SessionId SessionId { get; init; }
    /// <summary>Gets the task identifier.</summary>
    public required TaskId TaskId { get; init; }
    /// <summary>Gets the identifier of this agent execution.</summary>
    public required ExecutionId AgentExecutionId { get; init; }
    /// <summary>Gets the parent execution identifier, when this is a child.</summary>
    public ExecutionId? ParentExecutionId { get; init; }
    /// <summary>Gets the current delegation depth.</summary>
    public required int Depth { get; init; }
    /// <summary>Gets the catalog revision used to authorize this execution.</summary>
    public required long CatalogRevision { get; init; }
    /// <summary>Gets the policy revision used to authorize this execution.</summary>
    public required long PolicyRevision { get; init; }
    /// <summary>Gets the authorization revision used for this execution.</summary>
    public required long AuthRevision { get; init; }
    /// <summary>Gets the execution deadline.</summary>
    public required DateTimeOffset Deadline { get; init; }
    /// <summary>Gets the remaining named resource budgets.</summary>
    public required ImmutableDictionary<string, long> RemainingBudgets { get; init; }
    /// <summary>Gets the cancellation token for this execution.</summary>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>Creates a more restricted child. Scope equality is the only supported scope intersection absent a catalog scope algebra.</summary>
    /// <param name="tenantScope">The requested child tenant scope.</param>
    /// <param name="principalOrWorkloadScope">The requested child principal or workload scope.</param>
    /// <param name="requestedBudgets">Requested child budgets, capped by the remaining parent budgets.</param>
    /// <param name="cancellationToken">Additional cancellation for the child.</param>
    /// <returns>A child context with narrower authority and linked cancellation.</returns>
    public AgentExecutionContext CreateChild(string tenantScope, string principalOrWorkloadScope,
        IReadOnlyDictionary<string, long> requestedBudgets, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalOrWorkloadScope);
        ArgumentNullException.ThrowIfNull(requestedBudgets);
        CancellationToken.ThrowIfCancellationRequested();
        if (tenantScope != TenantScope || principalOrWorkloadScope != PrincipalOrWorkloadScope)
            throw new UnauthorizedAccessException("A child execution cannot widen or change its authority scope.");
        var budgets = ImmutableDictionary.CreateBuilder<string, long>(StringComparer.Ordinal);
        foreach (var (key, requested) in requestedBudgets)
        {
            if (requested < 0 || !RemainingBudgets.TryGetValue(key, out var available))
                throw new InvalidOperationException("Child execution requested an unavailable budget.");
            budgets[key] = Math.Min(requested, available);
        }
        var linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, cancellationToken);
        return this with
        {
            AgentExecutionId = new ExecutionId(Ulid.NewUlid()), ParentExecutionId = AgentExecutionId,
            Depth = checked(Depth + 1), RemainingBudgets = budgets.ToImmutable(),
            Deadline = Deadline, CancellationToken = linked.Token, _linkedCancellation = linked
        };
    }

    /// <summary>Releases the linked cancellation source owned by this context.</summary>
    public void Dispose() => _linkedCancellation?.Dispose();
}
