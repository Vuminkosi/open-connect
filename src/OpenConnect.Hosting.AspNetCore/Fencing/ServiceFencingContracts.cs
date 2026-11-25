using OpenConnect.Abstractions;

namespace OpenConnect.Hosting.AspNetCore.Fencing;

/// <summary>Trusted host-side execution context for a fenced outbound HTTP call.</summary>
public sealed record ServiceFencingExecutionContext(
    TenantId TenantId,
    PrincipalId PrincipalId,
    string ServiceId,
    string Purpose,
    string? ApplicationId = null,
    string? RequestId = null,
    string? TraceId = null);

/// <summary>Provides the current ambient execution context for fenced outbound HTTP calls.</summary>
public interface IServiceFencingContextAccessor
{
    ServiceFencingExecutionContext? Current { get; }
    IDisposable BeginScope(ServiceFencingExecutionContext context);
}

/// <summary>AsyncLocal-backed execution context accessor for fenced outbound HTTP calls.</summary>
public sealed class AsyncLocalServiceFencingContextAccessor : IServiceFencingContextAccessor
{
    private static readonly AsyncLocal<Holder?> CurrentHolder = new();

    public ServiceFencingExecutionContext? Current => CurrentHolder.Value?.Context;

    public IDisposable BeginScope(ServiceFencingExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var prior = CurrentHolder.Value;
        var next = new Holder(context);
        CurrentHolder.Value = next;
        return new Scope(next, prior);
    }

    private sealed class Holder(ServiceFencingExecutionContext context)
    {
        public ServiceFencingExecutionContext Context { get; } = context;
    }

    private sealed class Scope(Holder active, Holder? prior) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(CurrentHolder.Value, active))
                CurrentHolder.Value = prior;
        }
    }
}

/// <summary>Default-deny policy for one explicitly registered outbound service.</summary>
public sealed class FencedServicePolicy
{
    public string ServiceId { get; set; } = string.Empty;
    public string AllowedHost { get; set; } = string.Empty;
    public ISet<string> AllowedSchemes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Uri.UriSchemeHttps };
    public ISet<int> AllowedPorts { get; } = new HashSet<int> { 443 };
    public ISet<string> AllowedMethods { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { HttpMethod.Get.Method };
    public ISet<string> AllowedPurposes { get; } = new HashSet<string>(StringComparer.Ordinal);
    public ISet<TenantId> AllowedTenants { get; } = new HashSet<TenantId>();
    public ISet<PrincipalId> AllowedPrincipals { get; } = new HashSet<PrincipalId>();
    public ISet<string> AllowedApplications { get; } = new HashSet<string>(StringComparer.Ordinal);
    public bool AllowAnyAuthenticatedTenant { get; set; }
    public bool AllowPrivateNetwork { get; set; }
    public int MaxRedirectHops { get; set; }
    public string PolicyRevision { get; set; } = "1";
}

/// <summary>Host-configured registry of fenced outbound service policies.</summary>
public sealed class ServiceFencingOptions
{
    private readonly Dictionary<string, FencedServicePolicy> _services = new(StringComparer.Ordinal);

    public string PolicyRevision { get; set; } = "1";

    public IReadOnlyDictionary<string, FencedServicePolicy> Services => _services;

    public ServiceFencingOptions RegisterService(FencedServicePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.ServiceId))
            throw new ArgumentException("ServiceId is required.", nameof(policy));
        if (string.IsNullOrWhiteSpace(policy.AllowedHost))
            throw new ArgumentException("AllowedHost is required.", nameof(policy));
        if (policy.MaxRedirectHops is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(policy), "MaxRedirectHops must be between 0 and 5.");

        _services[policy.ServiceId] = policy;
        return this;
    }

    public bool TryGetService(string serviceId, out FencedServicePolicy? policy) =>
        _services.TryGetValue(serviceId, out policy);
}

/// <summary>Content-free structured evidence emitted for every fenced outbound HTTP evaluation.</summary>
public sealed record ServiceFencingEvidence(
    string SchemaVersion,
    Guid EventId,
    string RequestId,
    string? TraceId,
    DateTimeOffset OccurredAt,
    string Capability,
    string Boundary,
    string ServiceId,
    string PolicyRevision,
    TenantId? TenantId,
    PrincipalId? PrincipalId,
    string HttpMethod,
    string Decision,
    string EnforcementOutcome,
    string ReasonCode,
    long DurationMs);

/// <summary>Receives content-free service fencing evidence records.</summary>
public interface IServiceFencingEvidenceSink
{
    ValueTask RecordAsync(ServiceFencingEvidence evidence, CancellationToken cancellationToken = default);
}

/// <summary>No-op evidence sink used when no external sink is registered.</summary>
public sealed class NullServiceFencingEvidenceSink : IServiceFencingEvidenceSink
{
    public static NullServiceFencingEvidenceSink Instance { get; } = new();

    public ValueTask RecordAsync(ServiceFencingEvidence evidence, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

/// <summary>Bounded in-memory evidence sink for diagnostics and verification.</summary>
public sealed class InMemoryServiceFencingEvidenceSink : IServiceFencingEvidenceSink
{
    private readonly object _gate = new();
    private readonly Queue<ServiceFencingEvidence> _events = new();
    private readonly int _capacity;

    public InMemoryServiceFencingEvidenceSink(int capacity = 512)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public ValueTask RecordAsync(ServiceFencingEvidence evidence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        lock (_gate)
        {
            if (_events.Count >= _capacity)
                _events.Dequeue();
            _events.Enqueue(evidence);
        }
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<ServiceFencingEvidence> Snapshot()
    {
        lock (_gate)
            return _events.ToArray();
    }
}

/// <summary>Thrown when a fenced outbound HTTP call is denied by host policy before socket dispatch.</summary>
public sealed class ServiceFencingDeniedException : InvalidOperationException
{
    public ServiceFencingDeniedException(string serviceId, string reasonCode, Guid evidenceId, string message)
        : base(message)
    {
        ServiceId = serviceId;
        ReasonCode = reasonCode;
        EvidenceId = evidenceId;
    }

    public string ServiceId { get; }
    public string ReasonCode { get; }
    public Guid EvidenceId { get; }
}

/// <summary>Stable, content-free reason codes for service fencing decisions.</summary>
public static class ServiceFencingReasonCodes
{
    public const string Allowed = "allowed";
    public const string MissingExecutionContext = "missing_execution_context";
    public const string ServiceNotRegistered = "service_not_registered";
    public const string TenantMismatch = "tenant_mismatch";
    public const string PrincipalMismatch = "principal_mismatch";
    public const string ApplicationMismatch = "application_mismatch";
    public const string PurposeNotAllowed = "purpose_not_allowed";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string InvalidUri = "invalid_uri";
    public const string UserInfoRejected = "userinfo_rejected";
    public const string SchemeNotAllowed = "scheme_not_allowed";
    public const string HostNotAllowed = "host_not_allowed";
    public const string PortNotAllowed = "port_not_allowed";
    public const string MetadataIpBlocked = "metadata_ip_blocked";
    public const string PrivateIpBlocked = "private_ip_blocked";
    public const string DnsResolutionFailed = "dns_resolution_failed";
    public const string RedirectBlocked = "redirect_blocked";
    public const string RedirectLimitExceeded = "redirect_limit_exceeded";
}
