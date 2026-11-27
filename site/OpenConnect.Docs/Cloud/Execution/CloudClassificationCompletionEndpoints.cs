using System.Net.Http.Headers;

namespace OpenConnect.Docs.Cloud;

public static class CloudClassificationCompletionEndpoints
{
    public static void MapCloudClassificationCompletion(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapPost("/cloud/internal/v1/classification/quota/reservations/{reservationId:guid}/claims/{claimId:guid}/completion", async (
            Guid reservationId, Guid claimId, HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps || !trust.IsTrusted(context.Connection.ClientCertificate, DateTimeOffset.UtcNow))
                return Results.Unauthorized();
            if (!databaseConfigured)
                return Results.StatusCode(503);
            var headers = context.Request.Headers.Authorization;
            if (headers.Count != 1 || !AuthenticationHeaderValue.TryParse(headers.ToString(), out var header) ||
                !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Parameter) ||
                header.Parameter.Length > 160 || header.Parameter.Any(char.IsWhiteSpace))
                return Results.Unauthorized();
            CloudCredentialIdentity? identity;
            try
            { identity = await context.RequestServices.GetRequiredService<CloudCredentialService>().ValidateAsync(header.Parameter, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return Results.StatusCode(503); }
            if (identity is null)
                return Results.Unauthorized();
            if (!identity.Scopes.Contains("classification.classify"))
                return Results.StatusCode(403);
            if (reservationId == Guid.Empty || claimId == Guid.Empty || context.Request.ContentLength is > 0 ||
                context.Request.Headers.ContainsKey("Transfer-Encoding") || context.Request.Headers.ContainsKey("Content-Encoding"))
                return Results.BadRequest();
            try
            {
                var result = await context.RequestServices.GetRequiredService<CloudExecutionCompletionService>().CompleteClassificationAsync(
                    identity.TenantId, identity.ApplicationId, reservationId, claimId, "completed", cancellationToken);
                return Results.Json(result, statusCode: result.Status switch { "completed" or "already_completed" => 200, "conflict" => 409, "invalid_request" => 400, _ => 503 });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return Results.StatusCode(503); }
        });
    }
}
