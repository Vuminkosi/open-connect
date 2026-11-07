using System.Collections.Concurrent;
using System.Security.Claims;
using OpenConnect.Navigator.Events;

namespace OpenConnect.Navigator.AspNetCore.Outbox;

/// <summary>Server-derived authenticated identity and route session scope.</summary>
public readonly struct NavigatorServerAuthContext
{
    private NavigatorServerAuthContext(string tenantId, string principalId, string sessionId) =>
        (TenantId, PrincipalId, SessionId) = (tenantId, principalId, sessionId);

    public string TenantId { get; }
    public string PrincipalId { get; }
    public string SessionId { get; }

    public static NavigatorServerAuthContext FromServerPrincipal(ClaimsPrincipal principal, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(sessionId))
            throw new UnauthorizedAccessException("Navigator session access denied.");
        var tenant = principal.FindFirst("tenant_id")?.Value;
        var subject = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(subject))
            throw new UnauthorizedAccessException("Navigator session access denied.");
        return new NavigatorServerAuthContext(tenant, subject, sessionId);
    }
}

/// <summary>Projects committed safe events for authorized socket consumers; progress is never durable/replayed.</summary>
public sealed class NavigatorWsProjector
{
    private readonly CommittedEventProjector _validator = new();
    private const int MaxDeliveredPerConsumerSession = 10_000;
    // Bounds memory even when clients vary consumer IDs or sessions. Oldest keys are evicted first.
    internal const int MaxConsumerSessionKeys = 1_000;
    private readonly ConcurrentDictionary<string, (HashSet<string> Set, Queue<string> Order)> _sent = new(StringComparer.Ordinal);
    private readonly Queue<string> _keyOrder = new();
    private readonly object _sentLock = new();

    public async Task<CommittedNavigatorEvent?> ProjectAsync(
        NavigatorEvent value,
        NavigatorServerAuthContext authorization,
        CancellationToken cancellationToken = default,
        string? consumerId = null)
    {
        if (string.IsNullOrWhiteSpace(authorization.TenantId) || string.IsNullOrWhiteSpace(authorization.PrincipalId) ||
            string.IsNullOrWhiteSpace(authorization.SessionId) || !IsValidConsumerId(consumerId))
            throw new UnauthorizedAccessException("Navigator session access denied.");

        var projected = await _validator.ProjectAsync(value, cancellationToken).ConfigureAwait(false);
        if (projected is not null && !StringComparer.Ordinal.Equals(projected.SessionId, authorization.SessionId))
            throw new UnauthorizedAccessException("Navigator session access denied.");
        if (projected is null) return null;
        var key = $"{consumerId}\u001f{projected.SessionId}";
        lock (_sentLock)
        {
            if (!_sent.TryGetValue(key, out var delivered))
            {
                while (_sent.Count >= MaxConsumerSessionKeys)
                    _sent.TryRemove(_keyOrder.Dequeue(), out _);
                delivered = (new HashSet<string>(StringComparer.Ordinal), new Queue<string>());
                _sent[key] = delivered;
                _keyOrder.Enqueue(key);
            }
            if (!delivered.Set.Add(projected.EventId)) return null;
            delivered.Order.Enqueue(projected.EventId);
            if (delivered.Order.Count > MaxDeliveredPerConsumerSession)
                delivered.Set.Remove(delivered.Order.Dequeue());
        }
        return projected;
    }

    private static bool IsValidConsumerId(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is ':' or '_' or '-');

    /// <summary>Returns ephemeral progress marked explicitly non-durable; callers must not replay it.</summary>
    public static object ProjectProgress(string sessionId, long sequence, string type, IReadOnlyDictionary<string, string> metadata) =>
        new { sessionId, sequence, type, metadata, durable = false, replayable = false };
}
