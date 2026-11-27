using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Context;

namespace OpenConnect.Persistence.EFCore.Fencing;

/// <summary>Trusted host-side operation context for an explicitly fenced application DbContext command.</summary>
public sealed record DatabaseFencingOperationContext(
    TenantId TenantId,
    PrincipalId PrincipalId,
    string OperationId,
    string? ApplicationId = null,
    string? RequestId = null,
    string? TraceId = null);

/// <summary>Provides the current ambient database fencing operation context.</summary>
public interface IDatabaseFencingContextAccessor
{
    DatabaseFencingOperationContext? Current { get; }
    IDisposable BeginScope(DatabaseFencingOperationContext context);
}

/// <summary>AsyncLocal-backed operation context accessor for fenced application DbContext execution.</summary>
public sealed class AsyncLocalDatabaseFencingContextAccessor : IDatabaseFencingContextAccessor
{
    private static readonly AsyncLocal<Holder?> CurrentHolder = new();

    public DatabaseFencingOperationContext? Current => CurrentHolder.Value?.Context;

    public IDisposable BeginScope(DatabaseFencingOperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var prior = CurrentHolder.Value;
        var next = new Holder(context);
        CurrentHolder.Value = next;
        return new Scope(next, prior);
    }

    private sealed class Holder(DatabaseFencingOperationContext context)
    {
        public DatabaseFencingOperationContext Context { get; } = context;
    }

    private sealed class Scope(Holder active, Holder? prior) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (ReferenceEquals(CurrentHolder.Value, active))
                CurrentHolder.Value = prior;
        }
    }
}

/// <summary>Policy for one explicitly registered read/write business database operation.</summary>
public sealed class FencedDatabaseOperationPolicy
{
    public string OperationId { get; set; } = string.Empty;
    public bool ReadOnly { get; set; } = true;
    public int MaxCommandTimeoutSeconds { get; set; } = 15;
    public int MaxRows { get; set; } = 500;
    public ISet<TenantId> AllowedTenants { get; } = new HashSet<TenantId>();
    public ISet<PrincipalId> AllowedPrincipals { get; } = new HashSet<PrincipalId>();
    public bool AllowAnyAuthenticatedTenant { get; set; }
    public string PolicyRevision { get; set; } = "1";
}

/// <summary>Opt-in configuration for application DbContext fencing (FW-080). Never applies to OpenConnectDbContext.</summary>
public sealed class OpenConnectDatabaseFencingOptions
{
    private readonly HashSet<Type> _fencedContextTypes = new();
    private readonly Dictionary<string, FencedDatabaseOperationPolicy> _operations = new(StringComparer.Ordinal);

    public string PolicyRevision { get; set; } = "1";
    public int DefaultMaxCommandTimeoutSeconds { get; set; } = 15;
    public int DefaultMaxRows { get; set; } = 500;

    public IReadOnlySet<Type> FencedContextTypes => _fencedContextTypes;
    public IReadOnlyDictionary<string, FencedDatabaseOperationPolicy> Operations => _operations;

    /// <summary>Explicitly registers an application DbContext type for fencing. OpenConnectDbContext is rejected.</summary>
    public OpenConnectDatabaseFencingOptions RegisterFencedContext<TContext>() where TContext : DbContext
    {
        if (typeof(TContext) == typeof(OpenConnectDbContext))
            throw new ArgumentException("OpenConnect control-plane DbContext must never be attached to application database fencing.", nameof(TContext));
        _fencedContextTypes.Add(typeof(TContext));
        return this;
    }

    public OpenConnectDatabaseFencingOptions RegisterOperation(FencedDatabaseOperationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.OperationId))
            throw new ArgumentException("OperationId is required.", nameof(policy));
        if (policy.MaxCommandTimeoutSeconds is <= 0 or > 120)
            throw new ArgumentOutOfRangeException(nameof(policy), "MaxCommandTimeoutSeconds must be between 1 and 120.");
        if (policy.MaxRows is <= 0 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(policy), "MaxRows must be between 1 and 10000.");

        _operations[policy.OperationId] = policy;
        return this;
    }

    public bool TryGetOperation(string operationId, out FencedDatabaseOperationPolicy? policy) =>
        _operations.TryGetValue(operationId, out policy);
}

/// <summary>
/// Content-free evidence for fenced database command interception.
/// Never contains SQL text, parameter values, credentials, or row contents.
/// </summary>
public sealed record DatabaseFencingEvidence(
    string SchemaVersion,
    Guid EventId,
    string RequestId,
    string? TraceId,
    DateTimeOffset OccurredAt,
    string Capability,
    string Boundary,
    string OperationId,
    string CommandKind,
    string PolicyRevision,
    TenantId? TenantId,
    PrincipalId? PrincipalId,
    int AppliedTimeoutSeconds,
    int MaxRows,
    string Decision,
    string EnforcementOutcome,
    string ReasonCode,
    long DurationMs);

/// <summary>Receives content-free database fencing evidence records.</summary>
public interface IDatabaseFencingEvidenceSink
{
    ValueTask RecordAsync(DatabaseFencingEvidence evidence, CancellationToken cancellationToken = default);
}

/// <summary>No-op database fencing evidence sink.</summary>
public sealed class NullDatabaseFencingEvidenceSink : IDatabaseFencingEvidenceSink
{
    public static NullDatabaseFencingEvidenceSink Instance { get; } = new();

    public ValueTask RecordAsync(DatabaseFencingEvidence evidence, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

/// <summary>Bounded in-memory database fencing evidence sink for diagnostics and tests.</summary>
public sealed class InMemoryDatabaseFencingEvidenceSink : IDatabaseFencingEvidenceSink
{
    private readonly object _gate = new();
    private readonly Queue<DatabaseFencingEvidence> _events = new();
    private readonly int _capacity;

    public InMemoryDatabaseFencingEvidenceSink(int capacity = 512)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public ValueTask RecordAsync(DatabaseFencingEvidence evidence, CancellationToken cancellationToken = default)
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

    public IReadOnlyList<DatabaseFencingEvidence> Snapshot()
    {
        lock (_gate)
            return _events.ToArray();
    }
}

/// <summary>Thrown when a fenced application DbContext command is denied by host policy before execution.</summary>
public sealed class DatabaseFencingDeniedException : InvalidOperationException
{
    public DatabaseFencingDeniedException(string operationId, string reasonCode, Guid evidenceId, string message)
        : base(message)
    {
        OperationId = operationId;
        ReasonCode = reasonCode;
        EvidenceId = evidenceId;
    }

    public string OperationId { get; }
    public string ReasonCode { get; }
    public Guid EvidenceId { get; }
}

/// <summary>Stable content-free reason codes for database fencing decisions.</summary>
public static class DatabaseFencingReasonCodes
{
    public const string Allowed = "allowed";
    public const string MissingOperationContext = "missing_operation_context";
    public const string OperationNotRegistered = "operation_not_registered";
    public const string TenantMismatch = "tenant_mismatch";
    public const string PrincipalMismatch = "principal_mismatch";
    public const string WriteNotAllowed = "write_not_allowed";
}
