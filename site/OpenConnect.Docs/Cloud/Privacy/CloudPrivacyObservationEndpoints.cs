using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public static class CloudPrivacyObservationEndpoints
{
    public static void MapCloudPrivacyObservationEndpoints(this RouteGroupBuilder group, string? identityIssuer)
    {
        group.MapGet("/tenants/{tenantId:guid}/security/observations", async (
            Guid tenantId, DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset? beforeAt, Guid? beforeId, int? limit,
            ClaimsPrincipal principal, HttpContext context, CloudPrivacyObservationService service, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null)
                return Results.Unauthorized();
            if (tenantId == Guid.Empty || (beforeAt is null) != (beforeId is null) || beforeId == Guid.Empty || from >= to || (limit ?? 50) is < 1 or > 100)
                return Results.BadRequest();
            var cursor = beforeAt is not null && beforeId is not null ? new CloudPrivacyObservationCursor(beforeAt.Value, beforeId.Value) : null;
            var page = await service.GetTenantAsync(subject, tenantId, from, to, cursor, limit ?? 50, cancellationToken);
            return page is null ? Results.NotFound() : Results.Ok(page);
        });
        group.MapGet("/tenants/{tenantId:guid}/security/cases/{caseId:guid}", async (
            Guid tenantId, Guid caseId, ClaimsPrincipal principal, HttpContext context,
            IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null)
                return Results.Unauthorized();
            if (tenantId == Guid.Empty || caseId == Guid.Empty)
                return Results.BadRequest();
            var result = await CloudPrivacyCaseReader.GetAsync(factory, clock, subject, tenantId, caseId, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

    }
}
