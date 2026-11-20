using System.Security.Claims;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Antiforgery;

namespace OpenConnect.Docs.Cloud;

public static class CloudControlPlaneEndpoints
{
    public static void MapCloudControlPlaneEndpoints(this WebApplication app, string? identityIssuer, bool databaseConfigured)
    {
        if (!databaseConfigured)
        {
            app.MapGet("/cloud/health/ready", () => Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Cloud control plane is not configured."));
        }
        else
        {
            app.MapGet("/cloud/health/ready", async (
                IDbContextFactory<CloudControlPlaneDbContext> contextFactory,
                ILoggerFactory loggerFactory,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
                    var connected = await db.Database.CanConnectAsync(cancellationToken);
                    IEnumerable<string> pending = connected
                        ? await db.Database.GetPendingMigrationsAsync(cancellationToken)
                        : Array.Empty<string>();
                    if (!connected || pending.Any())
                    {
                        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                            title: "Cloud control plane is not ready.");
                    }
                    return Results.Ok(new { status = "ready" });
                }
                catch (Exception exception) when (IsDatabaseFailure(exception))
                {
                    loggerFactory.CreateLogger("OpenConnect.Cloud.Readiness")
                        .LogWarning("Cloud control-plane readiness check failed.");
                    return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Cloud control plane is not ready.");
                }
            });
        }

        var group = app.MapGroup("/cloud/api/v1").RequireAuthorization("CloudApi");
        if (!databaseConfigured)
        {
            group.MapMethods("/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"], () => Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Cloud control plane is not configured."));
            return;
        }

        group.MapGet("/me", async (ClaimsPrincipal principal, CloudControlPlaneService service, CancellationToken cancellationToken) =>
        {
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            return subject is null
                ? Results.Unauthorized()
                : Results.Ok(new { tenants = await service.GetTenantsAsync(subject, cancellationToken) });
        });

        group.MapGet("/capabilities", async (CloudCapabilityService service, CancellationToken cancellationToken) =>
            Results.Ok(new { capabilities = await service.GetCatalogAsync(cancellationToken) }));

        group.MapGet("/tenants/{tenantId:guid}/capabilities", async (
            Guid tenantId, ClaimsPrincipal principal, CloudCapabilityService service, CancellationToken cancellationToken) =>
        {
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            var capabilities = await service.GetTenantCapabilitiesAsync(subject, tenantId, cancellationToken);
            return capabilities is null ? Results.NotFound() : Results.Ok(new { capabilities });
        });

        group.MapGet("/tenants/{tenantId:guid}/applications/{applicationId:guid}/capabilities", async (
            Guid tenantId, Guid applicationId, ClaimsPrincipal principal, CloudCapabilityService service, CancellationToken cancellationToken) =>
        {
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            var capabilities = await service.GetApplicationCapabilitiesAsync(subject, tenantId, applicationId, cancellationToken);
            return capabilities is null ? Results.NotFound() : Results.Ok(new { capabilities });
        });

        group.MapPut("/tenants/{tenantId:guid}/capabilities/{capabilityId}", async (
            Guid tenantId,
            string capabilityId,
            CloudCapabilitySelectionRequest request,
            ClaimsPrincipal principal,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            CloudCapabilityService service,
            CancellationToken cancellationToken) =>
        {
            if (!await HasValidAntiforgeryTokenAsync(httpContext, antiforgery)) return Results.BadRequest();
            if (!TryGetExpectedRevision(httpContext, out var expectedRevision))
            {
                return Results.Problem(statusCode: 428, title: "If-Match is required.", extensions: new Dictionary<string, object?> { ["code"] = "precondition_required" });
            }
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            var result = await service.SetTenantSelectionAsync(subject, tenantId, capabilityId, request.Enabled, expectedRevision, cancellationToken);
            return CapabilitySelectionResponse(httpContext, result);
        });

        group.MapPut("/tenants/{tenantId:guid}/applications/{applicationId:guid}/capabilities/{capabilityId}", async (
            Guid tenantId,
            Guid applicationId,
            string capabilityId,
            CloudCapabilitySelectionRequest request,
            ClaimsPrincipal principal,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            CloudCapabilityService service,
            CancellationToken cancellationToken) =>
        {
            if (!await HasValidAntiforgeryTokenAsync(httpContext, antiforgery)) return Results.BadRequest();
            if (!TryGetExpectedRevision(httpContext, out var expectedRevision))
            {
                return Results.Problem(statusCode: 428, title: "If-Match is required.", extensions: new Dictionary<string, object?> { ["code"] = "precondition_required" });
            }
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            var result = await service.SetApplicationSelectionAsync(
                subject, tenantId, applicationId, capabilityId, request.Enabled, expectedRevision, cancellationToken);
            return CapabilitySelectionResponse(httpContext, result);
        });

        group.MapPost("/tenants", async (
            CreateCloudTenantRequest request,
            ClaimsPrincipal principal,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            CloudControlPlaneService service,
            CancellationToken cancellationToken) =>
        {
            if (!await HasValidAntiforgeryTokenAsync(httpContext, antiforgery))
            {
                return Results.BadRequest();
            }
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Name must contain 1 to 200 characters."] });
            }

            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null)
            {
                return Results.Unauthorized();
            }

            var tenant = await service.CreateTenantAsync(subject, request.Name, cancellationToken);
            return Results.Created($"/cloud/api/v1/tenants/{tenant.Id:D}", tenant);
        });

        group.MapGet("/tenants/{tenantId:guid}/applications", async (
            Guid tenantId, ClaimsPrincipal principal, CloudControlPlaneService service, CancellationToken cancellationToken) =>
        {
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null)
            {
                return Results.Unauthorized();
            }

            var applications = await service.GetApplicationsAsync(subject, tenantId, cancellationToken);
            return applications is null ? Results.NotFound() : Results.Ok(new { applications });
        });

        group.MapPost("/tenants/{tenantId:guid}/applications", async (
            Guid tenantId,
            CreateCloudApplicationRequest request,
            ClaimsPrincipal principal,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            CloudControlPlaneService service,
            CancellationToken cancellationToken) =>
        {
            if (!await HasValidAntiforgeryTokenAsync(httpContext, antiforgery))
            {
                return Results.BadRequest();
            }
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Name must contain 1 to 200 characters."] });
            }

            if (request.Environment is not ("Development" or "Staging" or "Production"))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["environment"] = ["Environment must be Development, Staging, or Production."],
                });
            }

            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null)
            {
                return Results.Unauthorized();
            }

            var result = await service.CreateApplicationAsync(subject, tenantId, request.Name, request.Environment, cancellationToken);
            return result.Application is null
                ? Results.NotFound()
                : Results.Created($"/cloud/api/v1/tenants/{tenantId:D}/applications/{result.Application.Id:D}", result.Application);
        });

        group.MapGet("/tenants/{tenantId:guid}/applications/{applicationId:guid}/credentials", async (
            Guid tenantId,
            Guid applicationId,
            ClaimsPrincipal principal,
            CloudCredentialService service,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            var (credentials, _) = await service.ListAsync(subject, tenantId, applicationId, cancellationToken);
            if (credentials is null) return Results.NotFound();
            httpContext.Response.Headers["Cache-Control"] = "no-store";
            return Results.Ok(new { credentials });
        });

        group.MapPost("/tenants/{tenantId:guid}/applications/{applicationId:guid}/credentials", async (
            Guid tenantId,
            Guid applicationId,
            CreateCloudCredentialRequest request,
            ClaimsPrincipal principal,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            CloudCredentialService service,
            CancellationToken cancellationToken) =>
        {
            if (!await HasValidAntiforgeryTokenAsync(httpContext, antiforgery)) return Results.BadRequest();
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            var result = await service.CreateAsync(subject, tenantId, applicationId, request.Scopes, request.ExpiresAt, cancellationToken);
            return CredentialIssueResponse(httpContext, tenantId, applicationId, result);
        });

        group.MapPost("/tenants/{tenantId:guid}/applications/{applicationId:guid}/credentials/{credentialId:guid}/rotate", async (
            Guid tenantId,
            Guid applicationId,
            Guid credentialId,
            RotateCloudCredentialRequest request,
            ClaimsPrincipal principal,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            CloudCredentialService service,
            CancellationToken cancellationToken) =>
        {
            if (!await HasValidAntiforgeryTokenAsync(httpContext, antiforgery)) return Results.BadRequest();
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            var result = await service.RotateAsync(subject, tenantId, applicationId, credentialId, request.ExpiresAt, cancellationToken);
            return CredentialIssueResponse(httpContext, tenantId, applicationId, result);
        });

        group.MapPost("/tenants/{tenantId:guid}/applications/{applicationId:guid}/credentials/{credentialId:guid}/revoke", async (
            Guid tenantId,
            Guid applicationId,
            Guid credentialId,
            ClaimsPrincipal principal,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            CloudCredentialService service,
            CancellationToken cancellationToken) =>
        {
            if (!await HasValidAntiforgeryTokenAsync(httpContext, antiforgery)) return Results.BadRequest();
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            var result = await service.RevokeAsync(subject, tenantId, applicationId, credentialId, cancellationToken);
            return result switch
            {
                "succeeded" => Results.NoContent(),
                "forbidden" => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Credential administration is not allowed."),
                _ => Results.NotFound(),
            };
        });

        group.MapGet("/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            context.Response.Headers["Cache-Control"] = "no-store";
            return Results.Ok(new { requestToken = tokens.RequestToken });
        });
    }

    private static async Task<bool> HasValidAntiforgeryTokenAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static bool TryGetExpectedRevision(HttpContext context, out int revision)
    {
        revision = 0;
        var value = context.Request.Headers.IfMatch.ToString();
        return value.Length > 2 && value[0] == '"' && value[^1] == '"' &&
            int.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out revision) && revision >= 0;
    }

    private static IResult CapabilitySelectionResponse(HttpContext context, CloudCapabilitySelectionResult result)
    {
        if (result.Status == "succeeded")
        {
            context.Response.Headers.ETag = $"\"{result.Revision}\"";
            return Results.NoContent();
        }

        var (status, title) = result.Status switch
        {
            "not_found" => (StatusCodes.Status404NotFound, "Capability resource was not found."),
            "forbidden" => (StatusCodes.Status403Forbidden, "This membership cannot change service selections."),
            "capability_not_entitled" => (StatusCodes.Status403Forbidden, "Capability is not entitled for this organization."),
            "capability_unavailable" => (StatusCodes.Status503ServiceUnavailable, "Capability is not available on the platform."),
            "dependency_not_enabled" => (StatusCodes.Status409Conflict, "Required capabilities are not enabled."),
            "capability_disabled" => (StatusCodes.Status409Conflict, "Enable the organization capability before selecting it for an application."),
            "conflict" => (StatusCodes.Status409Conflict, "Capability selection changed; reload its current revision."),
            _ => (StatusCodes.Status500InternalServerError, "Capability selection failed."),
        };
        return Results.Problem(statusCode: status, title: title,
            extensions: new Dictionary<string, object?> { ["code"] = result.Status, ["currentRevision"] = result.Revision });
    }

    private static IResult CredentialIssueResponse(
        HttpContext context, Guid tenantId, Guid applicationId, CloudCredentialIssueResult result)
    {
        if (result.Status == "succeeded" && result.Credential is not null && result.Secret is not null)
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["Pragma"] = "no-cache";
            return Results.Created(
                $"/cloud/api/v1/tenants/{tenantId:D}/applications/{applicationId:D}/credentials/{result.Credential.Id:D}",
                new { credential = result.Credential, secret = result.Secret, shownOnce = true });
        }

        var (status, title) = result.Status switch
        {
            "not_found" => (StatusCodes.Status404NotFound, "Application or credential was not found."),
            "forbidden" => (StatusCodes.Status403Forbidden, "Credential administration is not allowed."),
            "invalid_scopes" => (StatusCodes.Status422UnprocessableEntity, "One or more service scopes are invalid."),
            "invalid_expiration" => (StatusCodes.Status422UnprocessableEntity, "Credential expiration must be in the future."),
            "capability_not_entitled" => (StatusCodes.Status403Forbidden, "A requested service is not entitled."),
            "capability_unavailable" => (StatusCodes.Status503ServiceUnavailable, "A requested service is not available."),
            "capability_disabled" => (StatusCodes.Status409Conflict, "A requested service is not enabled for its organization/application."),
            "dependency_not_enabled" => (StatusCodes.Status409Conflict, "A requested service dependency is not enabled."),
            "credential_revoked" => (StatusCodes.Status409Conflict, "The credential has already been revoked."),
            _ => (StatusCodes.Status500InternalServerError, "Credential operation failed."),
        };
        return Results.Problem(statusCode: status, title: title,
            extensions: new Dictionary<string, object?> { ["code"] = result.Status });
    }

    public static bool IsDatabaseFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException or TimeoutException)
            {
                return true;
            }
        }
        return false;
    }
}
