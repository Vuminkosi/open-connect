using System.Net.Http.Headers;

namespace OpenConnect.Docs.Cloud;

public static class CloudInferenceFingerprintKeyEndpoints
{
    public static void MapCloudInferenceFingerprintKeyLookup(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapGet("/cloud/internal/v1/inference/quota/fingerprint-key/{requestId:guid}", async (
            Guid requestId, HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps || !trust.IsTrusted(context.Connection.ClientCertificate, DateTimeOffset.UtcNow)) return Results.Unauthorized();
            if (!databaseConfigured) return Results.StatusCode(503);
            var headers = context.Request.Headers.Authorization;
            if (headers.Count != 1 || !AuthenticationHeaderValue.TryParse(headers.ToString(), out var header) ||
                !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Parameter) ||
                header.Parameter.Length > 160 || header.Parameter.Any(char.IsWhiteSpace)) return Results.Unauthorized();
            var authorization = await context.RequestServices.GetRequiredService<CloudMachineAuthorizationService>()
                .AuthorizeAsync(header.Parameter, "inference.chat", cancellationToken);
            if (!authorization.Authorized || authorization.Identity is null) return Results.StatusCode(authorization.Code switch
            {
                "unauthorized" => 401, "capability_unavailable" => 503, _ => 403,
            });
            if (requestId == Guid.Empty) return Results.BadRequest();
            try
            {
                var result = await context.RequestServices.GetRequiredService<CloudQuotaService>().FindFingerprintKeyAsync(
                    authorization.Identity.TenantId, authorization.Identity.ApplicationId, "inference.chat", requestId, cancellationToken);
                return Results.Json(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return Results.StatusCode(503); }
        });
    }
}
