using OpenConnect.Navigator;

namespace OpenConnect.Navigator.Operational;

public interface IOperationalHandler
{
    Task<OperationalReceipt> HandleAsync(OperationalJob job, CancellationToken cancellationToken = default);
}

/// <summary>Host integration seam for the existing supervisor; this package does not create a scheduler.</summary>
public interface IOperationalLeaseStore
{
    bool IsDurable { get; }
    ValueTask<OperationalLease?> TryAcquireAsync(string executionId, TimeSpan duration, CancellationToken cancellationToken);
    ValueTask<OperationalLease?> RenewAsync(OperationalLease lease, TimeSpan duration, CancellationToken cancellationToken);
    ValueTask<bool> IsCurrentAsync(OperationalLease lease, CancellationToken cancellationToken);
    ValueTask ReleaseAsync(OperationalLease lease, CancellationToken cancellationToken);
}

public interface IOperationalWorkloadIdentityProvider
{
    ValueTask<IOperationalWorkloadIdentity?> ResolveAsync(string servicePrincipalRef, string tenantScope, CancellationToken cancellationToken);
}

public interface IOperationalWorkloadIdentity : IAsyncDisposable { string TenantScope { get; } }

public interface IOperationalPlanProvider
{
    ValueTask<NavigatorTourPlan?> GetPlanAsync(string planId, string tenantScope, string policyRevision, CancellationToken cancellationToken);
}

public interface IOperationalStepExecutor
{
    /// <summary>Executes only the approved read-only tool subset, never arbitrary MCP names or page scripts.</summary>
    ValueTask<OperationalStepResult> ExecuteReadOnlyAsync(NavigatorTourStep step, IOperationalWorkloadIdentity identity,
        IReadOnlySet<string> applicationAllowlist, CancellationToken cancellationToken);
}

public sealed record OperationalStepResult(bool Succeeded, bool Uncertain, string? SafeSummary);
