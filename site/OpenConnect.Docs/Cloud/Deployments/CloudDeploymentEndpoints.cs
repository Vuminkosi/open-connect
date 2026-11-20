using System.Security.Claims;

namespace OpenConnect.Docs.Cloud;

public static class CloudDeploymentEndpoints
{
    public static void MapCloudDeploymentEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/operator/deployments", async (string? capability, int? offset, int? limit,
            ClaimsPrincipal principal, HttpContext context, CloudOperatorAccess operators,
            CloudDeploymentRegistry registry, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!operators.IsAllowed(principal)) return Results.Forbid();
            var start = offset ?? 0;
            var size = limit ?? 50;
            if (!CloudDeploymentRegistry.ValidToken(capability, 80) || start is < 0 or > 1000000 || size is < 1 or > 100)
                return Results.BadRequest();
            var records = await registry.ListAsync(capability!, start, size + 1, cancellationToken);
            var hasMore = records.Count > size;
            return Results.Ok(new { deployments = records.Take(size), offset = start, nextOffset = hasMore ? (int?)(start + size) : null });
        });
    }
}
