using System.Security.Claims;
using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;

namespace OpenConnect.Docs.Cloud;

public static class CloudControlPlaneEndpoints
{
    public static void MapCloudControlPlaneEndpoints(this WebApplication app, string? identityIssuer, bool databaseConfigured)
    {
        if (!databaseConfigured)
        {
            app.MapGet("/cloud/health/ready", (HttpContext httpContext) => Problem(
                httpContext,
                StatusCodes.Status503ServiceUnavailable,
                "Cloud control plane is not configured.",
                "service_unavailable"));
        }
        else
        {
            app.MapGet("/cloud/health/ready", async (
                HttpContext httpContext,
                CloudReadinessService readiness,
                ILoggerFactory loggerFactory,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    if (!await readiness.IsReadyAsync(cancellationToken))
                    {
                        return Problem(httpContext, StatusCodes.Status503ServiceUnavailable,
                            "Cloud control plane is not ready.", "service_unavailable");
                    }
                    return Results.Ok(new { status = "ready" });
                }
                catch (Exception exception) when (CloudDatabaseFailure.IsDatabaseFailure(exception))
                {
                    loggerFactory.CreateLogger("OpenConnect.Cloud.Readiness")
                        .LogWarning("Cloud control-plane readiness check failed.");
                    return Problem(httpContext, StatusCodes.Status503ServiceUnavailable,
                        "Cloud control plane is not ready.", "service_unavailable");
                }
            });
        }

        var group = app.MapGroup("/cloud/api/v1").RequireAuthorization("CloudApi");
        if (!databaseConfigured)
        {
            group.MapMethods("/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"], (HttpContext httpContext) => Problem(
                httpContext,
                StatusCodes.Status503ServiceUnavailable,
                "Cloud control plane is not configured.",
                "service_unavailable"));
            return;
        }

        group.MapCloudQuotaEndpoints(identityIssuer);
        group.MapCloudUsageEndpoints(identityIssuer);
        group.MapCloudRegionPolicyEndpoints(identityIssuer);
        group.MapCloudInferenceModelPolicyEndpoints(identityIssuer);
        group.MapCloudCatalogRegionsEndpoints();
        group.MapCloudCapabilityAvailabilityEndpoints();
        group.MapCloudQuotaAdministrationEndpoints();
        group.MapCloudEntitlementAdministrationEndpoints();
        group.MapCloudPrivacyObservationEndpoints(identityIssuer);
        group.MapCloudHostPrivacyObservationEndpoints(identityIssuer);
        group.MapCloudSecurityEvidenceQueryEndpoints(identityIssuer);
        group.MapCloudPrivacyStatisticsEndpoints(identityIssuer);
        group.MapCloudDeploymentEndpoints();
        group.MapCloudTranslationDeploymentProfileEndpoints();

        group.MapGet("/me", async (ClaimsPrincipal principal, CloudControlPlaneService service, CancellationToken cancellationToken) =>
        {
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            return subject is null
                ? Results.Unauthorized()
                : Results.Ok(new { tenants = await service.GetTenantsAsync(subject, cancellationToken) });
        });

        group.MapGet("/tenants", async (ClaimsPrincipal principal, CloudControlPlaneService service, CancellationToken cancellationToken) =>
        {
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            return subject is null ? Results.Unauthorized() : Results.Ok(new { tenants = await service.GetTenantsAsync(subject, cancellationToken) });
        });

        group.MapGet("/tenants/{tenantId:guid}/members", async (
            Guid tenantId, ClaimsPrincipal principal, CloudControlPlaneService service, CancellationToken cancellationToken) =>
        {
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            var members = await service.GetMembersAsync(subject, tenantId, cancellationToken);
            return members is null ? Results.NotFound() : Results.Ok(new { members });
        });

        group.MapPost("/tenants/{tenantId:guid}/members/invitations", async (
            Guid tenantId, CreateCloudInvitationRequest request, ClaimsPrincipal principal, HttpContext context,
            IAntiforgery antiforgery, CloudInvitationService service, CancellationToken cancellationToken) =>
        {
            if (!await HasValidAntiforgeryTokenAsync(context, antiforgery)) return InvalidAntiforgeryResponse(context);
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            var result = await service.CreateAsync(subject, tenantId, request, cancellationToken);
            if (result.Status != "succeeded") return InvitationResponse(context, result.Status);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { code = result.Code, expiresAt = result.ExpiresAt });
        });

        group.MapGet("/tenants/{tenantId:guid}/members/invitations", async (
            Guid tenantId, ClaimsPrincipal principal, CloudInvitationService service, CancellationToken cancellationToken) =>
        {
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            var invitations = await service.ListAsync(subject, tenantId, cancellationToken);
            return invitations is null ? Results.NotFound() : Results.Ok(new { invitations });
        });

        group.MapPost("/tenants/{tenantId:guid}/members/invitations/{invitationId:guid}/revoke", async (
            Guid tenantId, Guid invitationId, ClaimsPrincipal principal, HttpContext context,
            IAntiforgery antiforgery, CloudInvitationService service, CancellationToken cancellationToken) =>
        {
            if (!await HasValidAntiforgeryTokenAsync(context, antiforgery)) return InvalidAntiforgeryResponse(context);
            if (!TryGetExpectedRevision(context, out var revision) || revision < 1)
                return Problem(context, StatusCodes.Status428PreconditionRequired, "An invitation revision is required.", "precondition_required");
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            var result = await service.RevokeAsync(subject, tenantId, invitationId, revision, cancellationToken);
            if (result.Status != "succeeded") return InvitationResponse(context, result.Status);
            context.Response.Headers.ETag = $"\"{result.Revision}\"";
            return Results.NoContent();
        });

        group.MapPost("/invitations/accept", async (
            AcceptCloudInvitationRequest request, ClaimsPrincipal principal, HttpContext context,
            IAntiforgery antiforgery, CloudInvitationService service, CancellationToken cancellationToken) =>
        {
            if (!await HasValidAntiforgeryTokenAsync(context, antiforgery)) return InvalidAntiforgeryResponse(context);
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            var result = await service.AcceptAsync(subject, request.Code, cancellationToken);
            return result == "succeeded" ? Results.NoContent() : InvitationResponse(context, result);
        });

        group.MapPut("/tenants/{tenantId:guid}/members/role", async (
            Guid tenantId, CloudMemberChangeRequest request, ClaimsPrincipal principal, HttpContext context,
            IAntiforgery antiforgery, CloudControlPlaneService service, CancellationToken cancellationToken) =>
        {
            if (!await HasValidAntiforgeryTokenAsync(context, antiforgery)) return InvalidAntiforgeryResponse(context);
            if (!TryGetExpectedRevision(context, out var revision) || revision < 1)
                return Problem(context, StatusCodes.Status428PreconditionRequired, "A membership revision is required.", "precondition_required");
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            return MemberChangeResponse(context, await service.ChangeMemberAsync(subject, tenantId, request, revision, false, cancellationToken));
        });

        group.MapPost("/tenants/{tenantId:guid}/members/remove", async (
            Guid tenantId, CloudMemberChangeRequest request, ClaimsPrincipal principal, HttpContext context,
            IAntiforgery antiforgery, CloudControlPlaneService service, CancellationToken cancellationToken) =>
        {
            if (!await HasValidAntiforgeryTokenAsync(context, antiforgery)) return InvalidAntiforgeryResponse(context);
            if (!TryGetExpectedRevision(context, out var revision) || revision < 1)
                return Problem(context, StatusCodes.Status428PreconditionRequired, "A membership revision is required.", "precondition_required");
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            return MemberChangeResponse(context, await service.ChangeMemberAsync(subject, tenantId, request, revision, true, cancellationToken));
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
            if (!await HasValidAntiforgeryTokenAsync(httpContext, antiforgery)) return InvalidAntiforgeryResponse(httpContext);
            if (!TryGetExpectedRevision(httpContext, out var expectedRevision))
            {
                return Problem(httpContext, StatusCodes.Status428PreconditionRequired, "If-Match is required.", "precondition_required");
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
            if (!await HasValidAntiforgeryTokenAsync(httpContext, antiforgery)) return InvalidAntiforgeryResponse(httpContext);
            if (!TryGetExpectedRevision(httpContext, out var expectedRevision))
            {
                return Problem(httpContext, StatusCodes.Status428PreconditionRequired, "If-Match is required.", "precondition_required");
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
                return InvalidAntiforgeryResponse(httpContext);
            }
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
            {
                return ValidationProblem(httpContext, new Dictionary<string, string[]> { ["name"] = ["Name must contain 1 to 200 characters."] });
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
                return InvalidAntiforgeryResponse(httpContext);
            }
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
            {
                return ValidationProblem(httpContext, new Dictionary<string, string[]> { ["name"] = ["Name must contain 1 to 200 characters."] });
            }

            if (request.Environment is not ("Development" or "Staging" or "Production"))
            {
                return ValidationProblem(httpContext, new Dictionary<string, string[]>
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
            if (result.Application is null)
            {
                return result.Forbidden
                    ? Problem(httpContext, StatusCodes.Status403Forbidden, "Application administration is not allowed.", "forbidden")
                    : Problem(httpContext, StatusCodes.Status404NotFound, "Organization was not found.", "not_found");
            }

            return Results.Created($"/cloud/api/v1/tenants/{tenantId:D}/applications/{result.Application.Id:D}", result.Application);
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
            if (!await HasValidAntiforgeryTokenAsync(httpContext, antiforgery)) return InvalidAntiforgeryResponse(httpContext);
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
            if (!await HasValidAntiforgeryTokenAsync(httpContext, antiforgery)) return InvalidAntiforgeryResponse(httpContext);
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
            if (!await HasValidAntiforgeryTokenAsync(httpContext, antiforgery)) return InvalidAntiforgeryResponse(httpContext);
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null) return Results.Unauthorized();
            var result = await service.RevokeAsync(subject, tenantId, applicationId, credentialId, cancellationToken);
            return result switch
            {
                "succeeded" => Results.NoContent(),
                "forbidden" => Problem(httpContext, StatusCodes.Status403Forbidden, "Credential administration is not allowed.", "forbidden"),
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

    private static IResult Problem(
        HttpContext context,
        int statusCode,
        string title,
        string code,
        IDictionary<string, object?>? extraExtensions = null)
    {
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = code,
            ["traceId"] = context.TraceIdentifier,
        };
        if (extraExtensions is not null)
        {
            foreach (var (key, value) in extraExtensions)
            {
                extensions[key] = value;
            }
        }
        return Results.Problem(statusCode: statusCode, title: title, extensions: extensions);
    }

    private static IResult ValidationProblem(HttpContext context, IDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "invalid_request",
                ["traceId"] = context.TraceIdentifier,
            });

    private static IResult InvalidAntiforgeryResponse(HttpContext context) =>
        Problem(context, StatusCodes.Status400BadRequest, "A valid antiforgery token is required.", "invalid_antiforgery");

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
        return Problem(context, status, title, result.Status,
            new Dictionary<string, object?> { ["currentRevision"] = result.Revision });
    }

    private static IResult MemberChangeResponse(HttpContext context, CloudMemberChangeResult result)
    {
        if (result.Status == "succeeded")
        {
            if (result.Revision is not null) context.Response.Headers.ETag = $"\"{result.Revision}\"";
            return Results.NoContent();
        }
        var (status, title) = result.Status switch
        {
            "not_found" => (404, "Membership was not found."),
            "forbidden" => (403, "Only an organization owner can change memberships."),
            "invalid_request" => (422, "Membership change is invalid."),
            "last_owner" => (409, "An organization must retain an owner."),
            "conflict" => (409, "Membership changed; reload its current revision."),
            _ => (500, "Membership change failed."),
        };
        return Problem(context, status, title, result.Status,
            new Dictionary<string, object?> { ["currentRevision"] = result.Revision });
    }

    private static IResult InvitationResponse(HttpContext context, string status)
    {
        var (code, title) = status switch
        {
            "not_found" => (404, "Organization was not found."),
            "forbidden" => (403, "Only an organization owner can issue invitations."),
            "invalid_request" => (422, "Invitation details are invalid."),
            "already_member" => (409, "This identity is already a member."),
            "invitation_closed" => (409, "The invitation is no longer pending."),
            "reinvitation_unavailable" => (409, "A previous membership requires reconciliation before another invitation can be accepted."),
            "conflict" => (409, "Invitation could not be completed. Try again."),
            _ => (400, "Invitation is invalid or unavailable."),
        };
        return Problem(context, code, title, status);
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
            "invalid_expiration" => (StatusCodes.Status422UnprocessableEntity, "A future credential expiration is required."),
            "capability_not_entitled" => (StatusCodes.Status403Forbidden, "A requested service is not entitled."),
            "capability_unavailable" => (StatusCodes.Status503ServiceUnavailable, "A requested service is not available."),
            "capability_disabled" => (StatusCodes.Status409Conflict, "A requested service is not enabled for its organization/application."),
            "dependency_not_enabled" => (StatusCodes.Status409Conflict, "A requested service dependency is not enabled."),
            "credential_revoked" => (StatusCodes.Status409Conflict, "The credential has already been revoked."),
            _ => (StatusCodes.Status500InternalServerError, "Credential operation failed."),
        };
        return Problem(context, status, title, result.Status);
    }

}
