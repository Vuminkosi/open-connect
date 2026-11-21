using System.Security.Claims;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudLiveUIUnknownUsage(Guid ReservationId, Guid TenantId, Guid ApplicationId, Guid RequestId,
    Guid DeploymentId, DateTimeOffset ExpiresAt, long ReservedTokens);

public sealed record CloudLiveUIReconciliationCursor(DateTimeOffset ExpiresAt, Guid ReservationId);
public sealed record CloudLiveUIReconciliationPage(string Status, IReadOnlyList<CloudLiveUIUnknownUsage>? Cases = null,
    CloudLiveUIReconciliationCursor? Next = null);

public interface ICloudLiveUIReconciliationReader
{
    Task<IReadOnlyList<CloudLiveUIUnknownUsage>> ReadUnknownAsync(CloudLiveUIReconciliationCursor? after, int count,
        DateTimeOffset cutoff, CancellationToken cancellationToken);
}

/// <summary>Authorizes operator access to unresolved, unmeasured consumed LiveUI reservations.</summary>
public sealed class CloudLiveUIReconciliation(CloudOperatorAccess access, ICloudLiveUIReconciliationReader reader, TimeProvider clock)
{
    public async Task<CloudLiveUIReconciliationPage> ListUnknownAsync(ClaimsPrincipal principal, CloudLiveUIReconciliationCursor? after,
        int limit, CancellationToken cancellationToken)
    {
        if (!access.IsAllowed(principal)) return new("forbidden");
        if (limit is < 1 or > 100 || after is { ReservationId: var id } && id == Guid.Empty)
            return new("invalid_request");
        var cursor = after is null ? null : after with { ExpiresAt = after.ExpiresAt.ToUniversalTime() };
        var rows = await reader.ReadUnknownAsync(cursor, limit + 1, clock.GetUtcNow().AddMinutes(-5), cancellationToken);
        var page = rows.Take(limit).ToArray();
        var next = rows.Count > limit ? new CloudLiveUIReconciliationCursor(page[^1].ExpiresAt, page[^1].ReservationId) : null;
        return new("succeeded", page, next);
    }
}
