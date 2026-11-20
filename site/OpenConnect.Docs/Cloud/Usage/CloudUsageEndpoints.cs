using System.Security.Claims;

namespace OpenConnect.Docs.Cloud;

public static class CloudUsageEndpoints
{
    public static void MapCloudUsageEndpoints(this RouteGroupBuilder group, string? identityIssuer)
    {
        group.MapGet("/tenants/{tenantId:guid}/usage", async (
            Guid tenantId, DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset? beforeAt, Guid? beforeId, int? limit,
            ClaimsPrincipal principal, HttpContext context, CloudUsageQueryService service, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            if (tenantId == Guid.Empty || (beforeAt is null) != (beforeId is null) || beforeId == Guid.Empty || from >= to || (limit ?? 50) is < 1 or > 100)
                return Results.BadRequest();
            var cursor = beforeAt is not null && beforeId is not null ? new CloudUsageCursor(beforeAt.Value, beforeId.Value) : null;
            var page = await service.GetTenantAsync(subject, tenantId, from, to, cursor, limit ?? 50, cancellationToken);
            return page is null ? Results.NotFound() : Results.Ok(page);
        });
    }
}
