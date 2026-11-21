using System.Security.Claims;

namespace OpenConnect.Docs.Cloud;

public static class CloudHostPrivacyObservationEndpoints
{
    public static void MapCloudHostPrivacyObservationEndpoints(this RouteGroupBuilder group, string? identityIssuer)
    {
        group.MapGet("/tenants/{tenantId:guid}/security/host-observations", async (
            Guid tenantId, DateTimeOffset? beforeAt, Guid? beforeId, int? limit, ClaimsPrincipal principal,
            HttpContext context, ICloudHostPrivacyObservationReader reader, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            if (tenantId == Guid.Empty || (beforeAt is null) != (beforeId is null) || beforeId == Guid.Empty || (limit ?? 50) is < 1 or > 100)
                return Results.BadRequest();
            var cursor = beforeAt is not null && beforeId is not null ? new CloudPrivacyObservationCursor(beforeAt.Value, beforeId.Value) : null;
            var page = await reader.GetTenantAsync(subject, tenantId, cursor, limit ?? 50, cancellationToken);
            return page is null ? Results.NotFound() : Results.Ok(page);
        });
        group.MapGet("/tenants/{tenantId:guid}/security/host-cases/{caseId:guid}", async (
            Guid tenantId, Guid caseId, ClaimsPrincipal principal, HttpContext context,
            ICloudHostPrivacyCaseReader reader, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            if (tenantId == Guid.Empty || caseId == Guid.Empty) return Results.BadRequest();
            var result = await reader.GetAsync(subject, tenantId, caseId, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
    }
}
