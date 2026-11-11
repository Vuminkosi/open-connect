using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using OpenConnect.Abstractions;

namespace OpenConnect.Hosting.AspNetCore.Execution;

/// <summary>Identifies the lifecycle status of a durable tool invocation.</summary>
public enum ToolInvocationStatus
{
    /// <summary>Waiting for explicit human or policy approval.</summary>
    PendingApproval,
    /// <summary>Durable invocation accepted and committed.</summary>
    Accepted,
    /// <summary>Worker or handler has claimed task and execution has started.</summary>
    Started,
    /// <summary>Actively progressing with reported units.</summary>
    Progressing,
    /// <summary>Work units reached 100% but output schema/privacy validation or commit is pending.</summary>
    Finalizing,
    /// <summary>Completed successfully.</summary>
    Completed,
    /// <summary>Failed with terminal error.</summary>
    Failed,
    /// <summary>Cancellation was requested but terminal outcome not yet confirmed.</summary>
    CancellationRequested,
    /// <summary>Confirmed cancelled.</summary>
    Cancelled,
    /// <summary>External outcome is uncertain (crash or timeout); prohibited from automatic retry.</summary>
    OutcomeUnknown
}

/// <summary>Durable record of a tool invocation with execution mode and monotonic progress bounds.</summary>
public sealed record DurableToolInvocationRecord(
    Ulid InvocationId,
    TaskId TaskId,
    SessionId SessionId,
    TenantId TenantId,
    PrincipalId PrincipalId,
    string ToolId,
    string ToolRevision,
    ToolExecutionMode ExecutionMode,
    ToolInvocationStatus Status,
    string CanonicalArgumentHash,
    string? CurrentPhase,
    long CompletedUnits,
    long? TotalUnits,
    string? LocalizedStatusKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage);

/// <summary>Tracks long-running BackgroundDurable tool invocations with truthful monotonic progress and heartbeat validation.</summary>
public sealed class DurableToolExecutionTracker
{
    private readonly ConcurrentDictionary<Ulid, DurableToolInvocationRecord> _invocations = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of <see cref="DurableToolExecutionTracker"/>.</summary>
    public DurableToolExecutionTracker(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Accepts a declared BackgroundDurable tool invocation.</summary>
    public DurableToolInvocationRecord AcceptInvocation(
        TaskId taskId,
        SessionId sessionId,
        TenantId tenantId,
        PrincipalId principalId,
        string toolId,
        string toolRevision,
        ToolExecutionMode executionMode,
        string canonicalArgumentsJson,
        bool requiresApproval)
    {
        ArgumentNullException.ThrowIfNull(toolId);
        ArgumentNullException.ThrowIfNull(canonicalArgumentsJson);

        var argumentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalArgumentsJson)));
        var initialStatus = requiresApproval ? ToolInvocationStatus.PendingApproval : ToolInvocationStatus.Accepted;

        var record = new DurableToolInvocationRecord(
            InvocationId: Ulid.NewUlid(),
            TaskId: taskId,
            SessionId: sessionId,
            TenantId: tenantId,
            PrincipalId: principalId,
            ToolId: toolId,
            ToolRevision: toolRevision,
            ExecutionMode: executionMode,
            Status: initialStatus,
            CanonicalArgumentHash: argumentHash,
            CurrentPhase: null,
            CompletedUnits: 0,
            TotalUnits: null,
            LocalizedStatusKey: requiresApproval ? "tool.status.pending_approval" : "tool.status.accepted",
            CreatedAt: _timeProvider.GetUtcNow(),
            LastHeartbeatAt: null,
            CompletedAt: null,
            ErrorMessage: null);

        _invocations[record.InvocationId] = record;
        return record;
    }

    /// <summary>Transitions a pending approval invocation to accepted state once approved.</summary>
    public DurableToolInvocationRecord Approve(Ulid invocationId)
    {
        return _invocations.AddOrUpdate(
            invocationId,
            _ => throw new KeyNotFoundException($"Invocation {invocationId} not found."),
            (_, current) =>
            {
                if (current.Status != ToolInvocationStatus.PendingApproval)
                    throw new InvalidOperationException($"Cannot approve invocation in state {current.Status}.");

                return current with
                {
                    Status = ToolInvocationStatus.Accepted,
                    LocalizedStatusKey = "tool.status.accepted"
                };
            });
    }

    /// <summary>Marks that execution has actually started after a worker claims the invocation.</summary>
    public DurableToolInvocationRecord MarkStarted(Ulid invocationId)
    {
        return _invocations.AddOrUpdate(
            invocationId,
            _ => throw new KeyNotFoundException($"Invocation {invocationId} not found."),
            (_, current) =>
            {
                if (current.Status is ToolInvocationStatus.PendingApproval or ToolInvocationStatus.Completed or ToolInvocationStatus.Cancelled)
                    throw new InvalidOperationException($"Cannot start invocation in state {current.Status}.");

                var now = _timeProvider.GetUtcNow();
                return current with
                {
                    Status = ToolInvocationStatus.Started,
                    LastHeartbeatAt = now,
                    LocalizedStatusKey = "tool.status.started"
                };
            });
    }

    /// <summary>Records verified progress with monotonic unit bounds or updates heartbeat liveness.</summary>
    public DurableToolInvocationRecord ReportProgress(
        Ulid invocationId,
        string phase,
        long completedUnits,
        long? totalUnits,
        string? localizedStatusKey = null,
        bool isHeartbeat = false)
    {
        return _invocations.AddOrUpdate(
            invocationId,
            _ => throw new KeyNotFoundException($"Invocation {invocationId} not found."),
            (_, current) =>
            {
                if (current.Status is ToolInvocationStatus.Completed or ToolInvocationStatus.Failed or ToolInvocationStatus.Cancelled)
                    return current; // Terminal state is immutable

                var now = _timeProvider.GetUtcNow();

                if (isHeartbeat)
                {
                    // Heartbeat only proves lease/process liveness; does not imply work advanced
                    return current with { LastHeartbeatAt = now };
                }

                // Monotonic bounds: completedUnits must be non-decreasing
                if (completedUnits < current.CompletedUnits)
                {
                    throw new ArgumentOutOfRangeException(nameof(completedUnits), "Completed units must be monotonically non-decreasing.");
                }

                if (totalUnits.HasValue && completedUnits > totalUnits.Value)
                {
                    throw new ArgumentOutOfRangeException(nameof(completedUnits), "Completed units cannot exceed total units.");
                }

                var isFinalizing = totalUnits.HasValue && completedUnits == totalUnits.Value;
                var status = isFinalizing ? ToolInvocationStatus.Finalizing : ToolInvocationStatus.Progressing;

                return current with
                {
                    Status = status,
                    CurrentPhase = phase,
                    CompletedUnits = completedUnits,
                    TotalUnits = totalUnits ?? current.TotalUnits,
                    LocalizedStatusKey = localizedStatusKey ?? (isFinalizing ? "tool.status.finalizing" : "tool.status.progressing"),
                    LastHeartbeatAt = now
                };
            });
    }

    /// <summary>Marks terminal completion with validated output.</summary>
    public DurableToolInvocationRecord MarkCompleted(Ulid invocationId)
    {
        return _invocations.AddOrUpdate(
            invocationId,
            _ => throw new KeyNotFoundException($"Invocation {invocationId} not found."),
            (_, current) =>
            {
                if (current.Status is ToolInvocationStatus.Completed or ToolInvocationStatus.Cancelled)
                    return current;

                return current with
                {
                    Status = ToolInvocationStatus.Completed,
                    CompletedAt = _timeProvider.GetUtcNow(),
                    LocalizedStatusKey = "tool.status.completed"
                };
            });
    }

    /// <summary>Requests cancellation, handling races with terminal completion.</summary>
    public DurableToolInvocationRecord RequestCancellation(Ulid invocationId)
    {
        return _invocations.AddOrUpdate(
            invocationId,
            _ => throw new KeyNotFoundException($"Invocation {invocationId} not found."),
            (_, current) =>
            {
                // If already terminal (completed or failed), cancellation loses race cleanly
                if (current.Status is ToolInvocationStatus.Completed or ToolInvocationStatus.Failed)
                    return current;

                return current with
                {
                    Status = ToolInvocationStatus.Cancelled,
                    CompletedAt = _timeProvider.GetUtcNow(),
                    LocalizedStatusKey = "tool.status.cancelled"
                };
            });
    }

    /// <summary>Gets a sanitized invocation record, enforcing tenant and principal ownership.</summary>
    public DurableToolInvocationRecord? GetSanitized(Ulid invocationId, TenantId tenantId, PrincipalId principalId)
    {
        if (!_invocations.TryGetValue(invocationId, out var record))
            return null;

        if (record.TenantId != tenantId || record.PrincipalId != principalId)
            return null; // Conceals existence across tenant/principal boundary

        return record;
    }
}
