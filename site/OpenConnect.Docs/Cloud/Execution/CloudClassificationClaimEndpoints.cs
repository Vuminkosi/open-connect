using System.Net.Http.Headers;

namespace OpenConnect.Docs.Cloud;

public static class CloudClassificationClaimEndpoints
{
    public static void MapCloudClassificationClaim(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapPost("/cloud/internal/v1/classification/quota/reservations/{reservationId:guid}/claim", async (
            Guid reservationId, HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps || !trust.IsTrusted(context.Connection.ClientCertificate, DateTimeOffset.UtcNow)) return Results.Unauthorized();
            if (!databaseConfigured) return Results.StatusCode(503);
            var headers = context.Request.Headers.Authorization;
            if (headers.Count != 1 || !AuthenticationHeaderValue.TryParse(headers.ToString(), out var header) ||
                !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Parameter) ||
                header.Parameter.Length > 160 || header.Parameter.Any(char.IsWhiteSpace)) return Results.Unauthorized();
            var authorization = await context.RequestServices.GetRequiredService<CloudMachineAuthorizationService>()
                .AuthorizeAsync(header.Parameter, "classification.classify", cancellationToken);
            if (!authorization.Authorized || authorization.Identity is null) return Results.StatusCode(authorization.Code switch
            {
                "unauthorized" => 401, "capability_unavailable" => 503, _ => 403,
            });
            if (reservationId == Guid.Empty || context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding") ||
                context.Request.Headers.ContainsKey("Content-Encoding")) return Results.BadRequest();
            try
            {
                var result = await context.RequestServices.GetRequiredService<CloudExecutionClaimService>().ClaimAsync(
                    authorization.Identity.TenantId, authorization.Identity.ApplicationId, reservationId, "classification.classify", cancellationToken);
                return Results.Json(result, statusCode: result.Status switch { "claimed" => 200, "already_claimed" => 409, "invalid_request" => 400, _ => 503 });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return Results.StatusCode(503); }
        });
    }
}
