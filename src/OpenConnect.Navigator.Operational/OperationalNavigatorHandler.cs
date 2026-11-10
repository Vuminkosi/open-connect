using OpenConnect.Abstractions;
using OpenConnect.Navigator;
using OpenConnect.Navigator.Operational.Durable;
using System.Collections.Concurrent;

namespace OpenConnect.Navigator.Operational;

/// <summary>Runs operational tours under a host-provided workload identity and fenced lease.</summary>
public sealed class OperationalNavigatorHandler(
    IOperationalLeaseStore leases,
    IOperationalWorkloadIdentityProvider identities,
    IOperationalPlanProvider plans,
    IOperationalStepExecutor executor,
    DurableIdempotencyStore? receipts = null,
    IRateLimitGate? rateLimitGate = null) : IOperationalHandler
{
    // The durable completed-receipt lookup is authoritative across processes; this reservation
    // remains a fast path for concurrent duplicate requests within this process.
    private readonly ConcurrentDictionary<string, Lazy<Task<OperationalReceipt>>> _results = new(StringComparer.Ordinal);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private const int MaxSteps = 25;

    public async Task<OperationalReceipt> HandleAsync(OperationalJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrWhiteSpace(job.TenantScope) || string.IsNullOrWhiteSpace(job.ServicePrincipalRef)
            || string.IsNullOrWhiteSpace(job.PlanId) || string.IsNullOrWhiteSpace(job.ExecutionId)
            || string.IsNullOrWhiteSpace(job.IdempotencyKey) || string.IsNullOrWhiteSpace(job.PolicyRevision)
                || job.ApplicationAllowlist is null || job.ApplicationAllowlist.Count == 0 || job.Deadline <= DateTimeOffset.UtcNow)
            return new(job.ExecutionId ?? string.Empty, OperationalRunStatus.Rejected, 0, "Invalid or expired operational job.", 0);

        if (IsRateLimited(job, cancellationToken))
            return new(job.ExecutionId, OperationalRunStatus.Rejected, 0, "Operational rate limit exceeded.", 0);

        if (receipts is not null)
        {
            var prior = await receipts.GetAsync(job.TenantScope, job.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            if (prior?.Status == OperationalRunStatus.Completed) return prior with { IsReplay = true };
        }

        var idempotencyId = $"{job.TenantScope}\0{job.IdempotencyKey}";
        var reservation = new Lazy<Task<OperationalReceipt>>(
            () => ExecuteAsync(job, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication);
        var selected = _results.GetOrAdd(idempotencyId, reservation);
        var task = selected.Value;
        try
        {
            var receipt = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (receipt.Status == OperationalRunStatus.Completed && receipts is not null)
            {
                // Only confirmed completion is safe to replay. Uncertain, failed, cancelled,
                // rejected, and lease-lost outcomes must never become durable receipts.
                var saved = await receipts.TryCompleteAsync(job.TenantScope, job.IdempotencyKey,
                    receipt, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                if (!saved)
                {
                    var stored = await receipts.GetAsync(job.TenantScope, job.IdempotencyKey, cancellationToken)
                        .ConfigureAwait(false);
                    receipt = stored is null ? receipt : stored with { IsReplay = true };
                }
            }
            if (receipt.Status != OperationalRunStatus.Completed)
                RemoveReservation(idempotencyId, selected);
            return receipt;
        }
        catch
        {
            if (task.IsCompleted) RemoveReservation(idempotencyId, selected);
            throw;
        }
    }

    private void RemoveReservation(string key, Lazy<Task<OperationalReceipt>> reservation) =>
        ((ICollection<KeyValuePair<string, Lazy<Task<OperationalReceipt>>>>)_results)
            .Remove(new(key, reservation));

    /// <summary>Enforces the tenant/application rate gate after validation and before durable-receipt lookup.</summary>
    /// <remarks>Gate exceptions deny (fail closed) using the existing Rejected shape. Cancellation is never
    /// misclassified as a rate-limit denial: <see cref="OperationCanceledException"/> propagates to the caller,
    /// matching MafConversationRunner.ThrowIfRateLimited and the pre-work state (no lease, executor, or durable
    /// effects exist yet, so no Cancelled receipt is fabricated).</remarks>
    private bool IsRateLimited(OperationalJob job, CancellationToken cancellationToken)
    {
        if (rateLimitGate is null) return false;
        try
        {
            var application = job.ApplicationAllowlist.OrderBy(name => name, StringComparer.Ordinal).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(application)) return true;
            var decision = rateLimitGate.TryAcquire(new RateLimitKey(job.TenantScope, application, RateLimitModes.Operational),
                job.ExecutionId, cancellationToken);
            return !decision.Allowed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return true;
        }
    }

    private async Task<OperationalReceipt> ExecuteAsync(OperationalJob job, CancellationToken cancellationToken)
    {

        var lease = await leases.TryAcquireAsync(job.ExecutionId, LeaseDuration, cancellationToken).ConfigureAwait(false);
        if (lease is null) return new(job.ExecutionId, OperationalRunStatus.Rejected, 0, "Execution is already leased.", 0);
        try
        {
            await using var identity = await identities.ResolveAsync(job.ServicePrincipalRef, job.TenantScope, cancellationToken).ConfigureAwait(false);
            if (identity is null || !StringComparer.Ordinal.Equals(identity.TenantScope, job.TenantScope))
                return new(job.ExecutionId, OperationalRunStatus.Rejected, 0, "Workload identity is unavailable or out of scope.", lease.FencingToken);

            var plan = await plans.GetPlanAsync(job.PlanId, job.TenantScope, job.PolicyRevision, cancellationToken).ConfigureAwait(false);
            if (plan is null) return new(job.ExecutionId, OperationalRunStatus.Rejected, 0, "Approved plan is unavailable.", lease.FencingToken);
            try { new NavigatorTourValidator().Validate(plan); }
            catch (ArgumentException) { return new(job.ExecutionId, OperationalRunStatus.Rejected, 0, "Plan exceeds operational safety limits.", lease.FencingToken); }
            if (plan.Steps.Count > MaxSteps || plan.Steps.Any(s => s.Type is not (NavigatorTourStepType.Explain or NavigatorTourStepType.Navigate or NavigatorTourStepType.Locate or NavigatorTourStepType.Highlight or NavigatorTourStepType.Focus or NavigatorTourStepType.WaitFor or NavigatorTourStepType.Complete)))
                return new(job.ExecutionId, OperationalRunStatus.Rejected, 0, "Plan contains a disallowed operational action.", lease.FencingToken);

            var completed = 0;
            foreach (var step in plan.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= job.Deadline) return new(job.ExecutionId, OperationalRunStatus.Cancelled, completed, "Execution deadline reached.", lease.FencingToken);
                if (!await leases.IsCurrentAsync(lease, cancellationToken).ConfigureAwait(false))
                    return new(job.ExecutionId, OperationalRunStatus.LeaseLost, completed, "Execution lease lost.", lease.FencingToken);
                var renewed = await leases.RenewAsync(lease, LeaseDuration, cancellationToken).ConfigureAwait(false);
                if (renewed is null) return new(job.ExecutionId, OperationalRunStatus.LeaseLost, completed, "Execution lease lost.", lease.FencingToken);
                lease = renewed;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var remaining = job.Deadline - DateTimeOffset.UtcNow;
                deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                var result = await executor.ExecuteReadOnlyAsync(step, identity, job.ApplicationAllowlist, deadline.Token).ConfigureAwait(false);
                if (!await leases.IsCurrentAsync(lease, CancellationToken.None).ConfigureAwait(false))
                    return new(job.ExecutionId, OperationalRunStatus.LeaseLost, completed, "Execution lease lost.", lease.FencingToken);
                if (result.Uncertain) return new(job.ExecutionId, OperationalRunStatus.Uncertain, completed, "Step outcome is uncertain; it will not be replayed.", lease.FencingToken);
                if (!result.Succeeded) return new(job.ExecutionId, OperationalRunStatus.Failed, completed, "Operational step failed.", lease.FencingToken);
                completed++;
            }
            return new(job.ExecutionId, OperationalRunStatus.Completed, completed, "Operational tour completed.", lease.FencingToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(job.ExecutionId, OperationalRunStatus.Cancelled, 0, "Operational execution was cancelled or expired.", lease.FencingToken);
        }
        catch (OperationCanceledException)
        {
            return new(job.ExecutionId, OperationalRunStatus.Cancelled, 0, "Operational execution was cancelled.", lease.FencingToken);
        }
        catch (Exception)
        {
            return new(job.ExecutionId, OperationalRunStatus.Failed, 0, "Operational execution failed.", lease.FencingToken);
        }
        finally { await leases.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false); }
    }
}
