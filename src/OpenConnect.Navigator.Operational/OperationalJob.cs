namespace OpenConnect.Navigator.Operational;

/// <summary>A bounded operational run; identity is a host-resolved reference, never an interactive credential.</summary>
public sealed record OperationalJob(string TenantScope, string ServicePrincipalRef, string PlanId,
    IReadOnlySet<string> ApplicationAllowlist, string ExecutionId, DateTimeOffset Deadline,
    string IdempotencyKey, string PolicyRevision);

public enum OperationalRunStatus { Completed, Cancelled, Failed, Uncertain, LeaseLost, Rejected }

/// <summary>Sanitized result; implementations must not persist page content or credentials.</summary>
/// <param name="ExecutionId">Execution that produced this receipt.</param>
/// <param name="Status">Final operational outcome.</param>
/// <param name="CompletedSteps">Number of successfully completed steps.</param>
/// <param name="SafeSummary">Sanitized summary, if available.</param>
/// <param name="FencingToken">Lease fencing token for the original execution.</param>
/// <param name="IsReplay">True when returned from durable storage. A replay preserves the original execution ID and fencing token as provenance, not as a new execution.</param>
public sealed record OperationalReceipt(string ExecutionId, OperationalRunStatus Status, int CompletedSteps,
    string? SafeSummary, long FencingToken, bool IsReplay = false);

public sealed record OperationalLease(string LeaseId, string ExecutionId, long FencingToken, DateTimeOffset ExpiresAt);
