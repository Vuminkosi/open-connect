namespace OpenConnect.Abstractions;

/// <summary>Well-known rate-limit modes used in <see cref="RateLimitKey"/>.</summary>
public static class RateLimitModes
{
    /// <summary>Interactive agent execution (orchestration adapters and conversation runners).</summary>
    public const string Interactive = "Interactive";
    /// <summary>Operational navigator execution.</summary>
    public const string Operational = "Operational";
}

/// <summary>Identifies one tenant/application/mode rate-limit bucket.</summary>
/// <remarks>String comparison is ordinal throughout. Instances are immutable and safe for concurrent use.</remarks>
public sealed record RateLimitKey
{
    /// <summary>Creates a rate-limit bucket identifier.</summary>
    /// <exception cref="ArgumentNullException">Any field is null.</exception>
    /// <exception cref="ArgumentException">Any field is empty or whitespace.</exception>
    public RateLimitKey(string tenant, string application, string mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(application);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        Tenant = tenant;
        Application = application;
        Mode = mode;
    }

    /// <summary>Gets the tenant authority scope.</summary>
    public string Tenant { get; }

    /// <summary>Gets the application identifier (agent or allowlisted application).</summary>
    public string Application { get; }

    /// <summary>Gets the execution mode (<see cref="RateLimitModes"/>).</summary>
    public string Mode { get; }
}

/// <summary>Ordinal equality comparer for <see cref="RateLimitKey"/>.</summary>
public sealed class RateLimitKeyComparer : IEqualityComparer<RateLimitKey>
{
    /// <summary>Gets the shared comparer instance.</summary>
    public static RateLimitKeyComparer Instance { get; } = new();

    private RateLimitKeyComparer() { }

    /// <inheritdoc/>
    public bool Equals(RateLimitKey? left, RateLimitKey? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        return StringComparer.Ordinal.Equals(left.Tenant, right.Tenant) &&
            StringComparer.Ordinal.Equals(left.Application, right.Application) &&
            StringComparer.Ordinal.Equals(left.Mode, right.Mode);
    }

    /// <inheritdoc/>
    public int GetHashCode(RateLimitKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(key.Tenant),
            StringComparer.Ordinal.GetHashCode(key.Application),
            StringComparer.Ordinal.GetHashCode(key.Mode));
    }
}

/// <summary>Outcome of one rate-limit acquisition attempt.</summary>
/// <param name="Allowed">True when the attempt was admitted.</param>
/// <param name="LimitName">Name of the exhausted window on denial ("burst" or "sustained"); null when allowed.</param>
/// <param name="Audit">Signed audit record on denial; null when allowed or when signing failed closed.</param>
public sealed record RateLimitDecision(bool Allowed, string? LimitName, RateLimitAuditRecord? Audit);

/// <summary>Signed audit record for a denied rate-limit acquisition.</summary>
/// <param name="Tenant">Tenant authority scope.</param>
/// <param name="Application">Application identifier.</param>
/// <param name="Mode">Execution mode.</param>
/// <param name="Decision">Gate decision ("deny").</param>
/// <param name="LimitName">Exhausted window name.</param>
/// <param name="ObservedCount">Would-be window count including the denied attempt.</param>
/// <param name="Limit">Configured window limit.</param>
/// <param name="Timestamp">Denial timestamp.</param>
/// <param name="ExecutionId">Attempted execution identifier.</param>
/// <param name="KeyId">Signing key identifier.</param>
/// <param name="Signature">Signature in "keyId:hex(HMAC-SHA256)" form.</param>
public sealed record RateLimitAuditRecord(
    string Tenant,
    string Application,
    string Mode,
    string Decision,
    string LimitName,
    long ObservedCount,
    long Limit,
    DateTimeOffset Timestamp,
    string ExecutionId,
    string KeyId,
    string Signature);

/// <summary>Enforces per-tenant, per-application rate limits and verifies signed denial audits.</summary>
public interface IRateLimitGate
{
    /// <summary>Attempts to admit one execution under the bucket identified by <paramref name="key"/>.</summary>
    /// <param name="key">Tenant/application/mode bucket.</param>
    /// <param name="executionId">Attempted execution identifier, recorded in denial audits.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Allowed outcome, or a denied outcome carrying the signed audit.</returns>
    RateLimitDecision TryAcquire(RateLimitKey key, string executionId, CancellationToken cancellationToken = default);

    /// <summary>Verifies the HMAC signature of a denial audit record.</summary>
    /// <param name="record">Audit record to verify.</param>
    /// <returns>True when the signature is valid; false otherwise. Never throws.</returns>
    bool Verify(RateLimitAuditRecord record);
}

/// <summary>Supplies HMAC signing keys for rate-limit denial audits.</summary>
/// <remarks>No production implementation is registered in this slice; tests supply a deterministic key.</remarks>
public interface IRateLimitAuditKeyProvider
{
    /// <summary>Gets the signing key identifier embedded in audit signatures.</summary>
    string KeyId { get; }

    /// <summary>Gets the signing key for a tenant.</summary>
    /// <param name="tenant">Tenant authority scope.</param>
    /// <returns>Raw HMAC key bytes.</returns>
    ReadOnlyMemory<byte> GetKey(string tenant);
}
