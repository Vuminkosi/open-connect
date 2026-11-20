using System.Security.Claims;

namespace OpenConnect.Docs.Cloud;

public static class CloudQuotaEndpoints
{
    public static void MapCloudQuotaEndpoints(this RouteGroupBuilder group, string? identityIssuer)
    {
        group.MapGet("/tenants/{tenantId:guid}/quotas", async (
            Guid tenantId, int? offset, int? limit, ClaimsPrincipal principal, HttpContext context,
            CloudQuotaQueryService service, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            var pageOffset = offset ?? 0;
            var pageSize = limit ?? 50;
            if (pageOffset < 0 || pageOffset > 1000000 || pageSize is < 1 or > 100)
                return Results.BadRequest();
            var page = await service.GetTenantAsync(subject, tenantId, pageOffset, pageSize, cancellationToken);
            return page is null ? Results.NotFound() : Results.Ok(page);
        });
    }
}
