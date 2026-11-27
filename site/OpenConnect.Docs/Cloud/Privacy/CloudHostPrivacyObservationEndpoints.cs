using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

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
            if (subject is null)
                return Results.Unauthorized();
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
            if (subject is null)
                return Results.Unauthorized();
            if (tenantId == Guid.Empty || caseId == Guid.Empty)
                return Results.BadRequest();
            var result = await reader.GetAsync(subject, tenantId, caseId, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
        group.MapGet("/tenants/{tenantId:guid}/security/coverage", (
            Guid tenantId, ClaimsPrincipal principal, HttpContext context,
            IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider clock, CancellationToken cancellationToken) =>
            HandleCoverageAsync(
                tenantId,
                principal,
                identityIssuer,
                context,
                (subject, id, ct) => CloudSecurityCoverageReader.GetTenantAsync(factory, clock, subject, id, ct),
                cancellationToken));
    }

    internal static async Task<IResult> HandleCoverageAsync(
        Guid tenantId,
        ClaimsPrincipal principal,
        string? identityIssuer,
        HttpContext context,
        Func<CloudSubject, Guid, CancellationToken, Task<CloudSecurityCoverageLookupResult>> readCoverage,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
        if (subject is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "Authentication is required.", "unauthorized");
        }
        if (tenantId == Guid.Empty)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "A valid tenant identifier is required.", "invalid_request");
        }

        var result = await readCoverage(subject, tenantId, cancellationToken);
        return result.Status switch
        {
            "succeeded" when result.Coverage is not null => Results.Ok(result.Coverage),
            "forbidden" => Problem(context, StatusCodes.Status403Forbidden, "Security coverage access requires Owner, Administrator, or SecurityAnalyst role.", "forbidden"),
            _ => Problem(context, StatusCodes.Status404NotFound, "Organization was not found.", "not_found"),
        };
    }

    private static IResult Problem(HttpContext context, int statusCode, string title, string code) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = code,
                ["traceId"] = context.TraceIdentifier,
            });
}
