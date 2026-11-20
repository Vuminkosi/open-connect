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

        group.MapGet("/tenants/{tenantId:guid}/usage/export", async (
            Guid tenantId, DateTimeOffset? from, DateTimeOffset? to, ClaimsPrincipal principal, HttpContext context,
            CloudUsageQueryService service, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            if (tenantId == Guid.Empty || from is null || to is null || from >= to || to.Value - from.Value > TimeSpan.FromDays(93))
                return Results.BadRequest();
            var page = await service.ExportTenantAsync(subject, tenantId, from.Value, to.Value, cancellationToken);
            if (page is null) return Results.NotFound();
            if (page.Next is not null) return Results.Problem(statusCode: 413, title: "Too many usage records; select a shorter period.");
            return Results.File(CloudUsageCsv.Encode(page.Entries), "text/csv; charset=utf-8", "openconnect-usage.csv");
        });
    }
}
