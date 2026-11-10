using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>Configures <see cref="TenantApplicationRateLimiter"/> fixed windows.</summary>
public sealed class RateLimitOptions
{
    /// <summary>Gets or sets the burst window limit per minute. Defaults to 60.</summary>
    public int BurstPerMinute { get; set; } = 60;

    /// <summary>Gets or sets the sustained window limit per day. Defaults to 1000.</summary>
    public int SustainedPerDay { get; set; } = 1000;
}

/// <summary>Dependency injection helpers for tenant/application rate limiting.</summary>
public static class RateLimitServiceCollectionExtensions
{
    /// <summary>Registers the in-process <see cref="IRateLimitGate"/>. The host must also register an <see cref="IRateLimitAuditKeyProvider"/>; without one the gate cannot be resolved.</summary>
    /// <param name="services">Service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTenantApplicationRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IRateLimitGate, TenantApplicationRateLimiter>();
        return services;
    }
}

/// <summary>In-process per-tenant, per-application rate limiter with signed denial audits.</summary>
/// <remarks>
/// Scope is intentionally single-process: counters live in memory and are not shared across
/// processes or restarts, and no durable counters are maintained. Distributed counting is out of scope.
/// Thread-safe and singleton-safe; each bucket carries its own lock and buckets never enumerate each other,
/// so tenants are structurally isolated.
/// </remarks>
public sealed class TenantApplicationRateLimiter : IRateLimitGate
{
    private static readonly TimeSpan BurstWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SustainedWindow = TimeSpan.FromDays(1);
    private const string BurstLimitName = "burst";
    private const string SustainedLimitName = "sustained";
    private const string DenyDecision = "deny";

    private readonly IRateLimitAuditKeyProvider _keys;
    private readonly TimeProvider _clock;
    private readonly int _burstPerMinute;
    private readonly int _sustainedPerDay;
    private readonly ConcurrentDictionary<RateLimitKey, Bucket> _buckets = new(RateLimitKeyComparer.Instance);

    /// <summary>Creates a rate limiter.</summary>
    /// <param name="keys">Audit signing key provider.</param>
    /// <param name="clock">Clock used for window advancement; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="options">Window limits; defaults to 60/minute burst and 1000/day sustained.</param>
    /// <exception cref="ArgumentOutOfRangeException">A configured limit is negative.</exception>
    public TenantApplicationRateLimiter(IRateLimitAuditKeyProvider keys, TimeProvider? clock = null, RateLimitOptions? options = null)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _clock = clock ?? TimeProvider.System;
        var resolved = options ?? new RateLimitOptions();
        if (resolved.BurstPerMinute < 0) throw new ArgumentOutOfRangeException(nameof(options), "Burst limit cannot be negative.");
        if (resolved.SustainedPerDay < 0) throw new ArgumentOutOfRangeException(nameof(options), "Sustained limit cannot be negative.");
        _burstPerMinute = resolved.BurstPerMinute;
        _sustainedPerDay = resolved.SustainedPerDay;
    }

    /// <inheritdoc/>
    public RateLimitDecision TryAcquire(RateLimitKey key, string executionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        cancellationToken.ThrowIfCancellationRequested();

        var bucket = _buckets.GetOrAdd(key, static _ => new Bucket());
        var now = _clock.GetUtcNow();
        string limitName;
        long observed;
        long limit;
        lock (bucket.Sync)
        {
            Advance(bucket, now);
            if (bucket.BurstCount >= _burstPerMinute)
            {
                limitName = BurstLimitName;
                observed = bucket.BurstCount + 1;
                limit = _burstPerMinute;
            }
            else if (bucket.SustainedCount >= _sustainedPerDay)
            {
                limitName = SustainedLimitName;
                observed = bucket.SustainedCount + 1;
                limit = _sustainedPerDay;
            }
            else
            {
                bucket.BurstCount++;
                bucket.SustainedCount++;
                return new RateLimitDecision(true, null, null);
            }
        }

        // Sign outside the bucket lock; a signing failure still denies (fail closed) without an audit.
        try
        {
            return new RateLimitDecision(false, limitName, Sign(key, limitName, observed, limit, now, executionId));
        }
        catch
        {
            return new RateLimitDecision(false, limitName, null);
        }
    }

    /// <inheritdoc/>
    public bool Verify(RateLimitAuditRecord record)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(record);
            var expected = SignatureFor(record.Tenant, record.Application, record.Mode, record.Decision,
                record.LimitName, record.ObservedCount, record.Limit, record.Timestamp, record.ExecutionId,
                _keys.GetKey(record.Tenant).Span, _keys.KeyId);
            var actualBytes = Encoding.UTF8.GetBytes(record.Signature);
            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            return actualBytes.Length == expectedBytes.Length &&
                CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
        }
        catch
        {
            return false;
        }
    }

    private static void Advance(Bucket bucket, DateTimeOffset now)
    {
        if (now - bucket.BurstStart >= BurstWindow)
        {
            bucket.BurstStart = now;
            bucket.BurstCount = 0;
        }
        if (now - bucket.SustainedStart >= SustainedWindow)
        {
            bucket.SustainedStart = now;
            bucket.SustainedCount = 0;
        }
    }

    private RateLimitAuditRecord Sign(RateLimitKey key, string limitName, long observed, long limit,
        DateTimeOffset timestamp, string executionId)
    {
        var keyId = _keys.KeyId;
        var signature = SignatureFor(key.Tenant, key.Application, key.Mode, DenyDecision, limitName,
            observed, limit, timestamp, executionId, _keys.GetKey(key.Tenant).Span, keyId);
        return new RateLimitAuditRecord(key.Tenant, key.Application, key.Mode, DenyDecision, limitName,
            observed, limit, timestamp, executionId, keyId, signature);
    }

    private static string SignatureFor(string tenant, string application, string mode, string decision,
        string limitName, long observed, long limit, DateTimeOffset timestamp, string executionId,
        ReadOnlySpan<byte> key, string keyId)
    {
        var payload = string.Join('\0', tenant, application, mode, decision, limitName,
            observed.ToString(CultureInfo.InvariantCulture), limit.ToString(CultureInfo.InvariantCulture),
            timestamp.ToString("o", CultureInfo.InvariantCulture), executionId);
        using var hmac = new HMACSHA256(key.ToArray());
        var tag = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return $"{keyId}:{Convert.ToHexString(tag).ToLowerInvariant()}";
    }

    private sealed class Bucket
    {
        public readonly object Sync = new();
        public DateTimeOffset BurstStart = DateTimeOffset.MinValue;
        public long BurstCount;
        public DateTimeOffset SustainedStart = DateTimeOffset.MinValue;
        public long SustainedCount;
    }
}
