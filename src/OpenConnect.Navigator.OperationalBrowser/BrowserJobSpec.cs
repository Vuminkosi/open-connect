namespace OpenConnect.Navigator.OperationalBrowser;

public sealed record BrowserResourceBudget(int MaxMs, int MaxSteps, long MaxBytes);

public sealed record BrowserJobSpec(string ExecutionId, string TenantScope, string ServicePrincipalRef,
    IReadOnlyList<Uri> AllowedOrigins, string PolicyRevision, DateTimeOffset Deadline,
    string IdempotencyKey, BrowserResourceBudget ResourceBudget);

public sealed record IsolatedBrowserSession(string SessionId, string ProfileDir, string WorkloadIdentityRef);

public sealed record BrowserAuditReceipt(string ExecutionId, string SessionId, DateTimeOffset CompletedAt,
    bool ProfileDeleted, bool CleanupRequiresOperatorAlert = false);

/// <summary>The resolved address the transport must connect to without performing another DNS lookup.</summary>
public sealed record ValidatedEgressTarget(Uri Destination, IReadOnlyList<System.Net.IPAddress> Addresses)
{
    /// <summary>
    /// Transport contract: connect to one of <see cref="Addresses"/> directly by IP, while preserving
    /// Destination's hostname as HTTP Host and TLS SNI. Automatic redirects must be disabled.
    /// Re-resolve and validate each redirect hop before dispatch; never let the socket layer resolve the host.
    /// </summary>
    public string TransportContract => "Connect by validated IP; preserve destination hostname for Host and TLS SNI; disable automatic redirects.";
}
