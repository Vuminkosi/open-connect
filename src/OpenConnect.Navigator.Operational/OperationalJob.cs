namespace OpenConnect.Navigator.Operational;

/// <summary>A bounded operational run; identity is a host-resolved reference, never an interactive credential.</summary>
public sealed record OperationalJob(string TenantScope, string ServicePrincipalRef, string PlanId,
    IReadOnlySet<string> ApplicationAllowlist, string ExecutionId, DateTimeOffset Deadline,
    string IdempotencyKey, string PolicyRevision);

public enum OperationalRunStatus { Completed, Cancelled, Failed, Uncertain, LeaseLost, Rejected }

/// <summary>Sanitized result; implementations must not persist page content or credentials.</summary>
public sealed record OperationalReceipt(string ExecutionId, OperationalRunStatus Status, int CompletedSteps,
    string? SafeSummary, long FencingToken);

public sealed record OperationalLease(string LeaseId, string ExecutionId, long FencingToken, DateTimeOffset ExpiresAt);
