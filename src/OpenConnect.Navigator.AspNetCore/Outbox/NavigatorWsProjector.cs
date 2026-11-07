using System.Collections.Concurrent;
using OpenConnect.Navigator.Events;

namespace OpenConnect.Navigator.AspNetCore.Outbox;

/// <summary>Identity and session authorization required before emitting a Navigator event to a socket.</summary>
public sealed record NavigatorWsAuthorization(string TenantId, string PrincipalId, string SessionTenantId, string SessionPrincipalId, string SessionId);

/// <summary>Projects committed safe events for authorized socket consumers; progress is never durable/replayed.</summary>
public sealed class NavigatorWsProjector
{
    private readonly CommittedEventProjector _validator = new();
    private readonly ConcurrentDictionary<string, byte> _sent = new(StringComparer.Ordinal);

    public async Task<CommittedNavigatorEvent?> ProjectAsync(
        NavigatorEvent value,
        NavigatorWsAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (string.IsNullOrWhiteSpace(authorization.TenantId) || string.IsNullOrWhiteSpace(authorization.PrincipalId) ||
            string.IsNullOrWhiteSpace(authorization.SessionId) ||
            !StringComparer.Ordinal.Equals(authorization.TenantId, authorization.SessionTenantId) ||
            !StringComparer.Ordinal.Equals(authorization.PrincipalId, authorization.SessionPrincipalId))
            throw new UnauthorizedAccessException("Navigator session access denied.");

        var projected = await _validator.ProjectAsync(value, cancellationToken).ConfigureAwait(false);
        if (projected is not null && !StringComparer.Ordinal.Equals(projected.SessionId, authorization.SessionId))
            throw new UnauthorizedAccessException("Navigator session access denied.");
        if (projected is null || !_sent.TryAdd(projected.EventId, 0)) return null;
        return projected;
    }

    /// <summary>Returns ephemeral progress marked explicitly non-durable; callers must not replay it.</summary>
    public static object ProjectProgress(string sessionId, long sequence, string type, IReadOnlyDictionary<string, string> metadata) =>
        new { sessionId, sequence, type, metadata, durable = false, replayable = false };
}
