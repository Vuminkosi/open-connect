using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudGatewayAuthorizationRequest(string? Credential, string? Capability);
public sealed record CloudGatewayIdentity(Guid CredentialId, Guid TenantId, Guid ApplicationId, string Environment);
public sealed record CloudGatewayAuthorizationResponse(bool Authorized, string Code, CloudGatewayIdentity? Identity = null);

public static class CloudGatewayAuthorizationEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static void MapCloudGatewayAuthorization(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapPost("/cloud/internal/v1/authorize", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps || !trust.IsTrusted(context.Connection.ClientCertificate, DateTimeOffset.UtcNow))
                return Results.Unauthorized();
            if (!databaseConfigured)
                return Results.Problem(statusCode: 503, title: "Cloud authorization is unavailable.");
            if (!context.Request.HasJsonContentType() || context.Request.Headers.ContainsKey("Content-Encoding"))
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            if (context.Request.ContentLength > 4096)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            var buffer = new byte[4097];
            CloudGatewayAuthorizationRequest? request;
            try
            {
                var length = 0;
                while (length < buffer.Length)
                {
                    var count = await context.Request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
                    if (count == 0)
                        break;
                    length += count;
                }
                if (length > 4096)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                request = JsonSerializer.Deserialize<CloudGatewayAuthorizationRequest>(buffer.AsSpan(0, length), JsonOptions);
            }
            catch (JsonException) { return Results.BadRequest(); }
            finally { CryptographicOperations.ZeroMemory(buffer); }
            if (request is null || string.IsNullOrWhiteSpace(request.Credential) || request.Credential.Length > 160 ||
                string.IsNullOrWhiteSpace(request.Capability) || request.Capability.Length > 80)
                return Results.BadRequest();

            var service = context.RequestServices.GetRequiredService<CloudMachineAuthorizationService>();
            var result = await service.AuthorizeAsync(request.Credential, request.Capability, cancellationToken);
            var identity = result.Identity is null ? null : new CloudGatewayIdentity(result.Identity.CredentialId,
                result.Identity.TenantId, result.Identity.ApplicationId, result.Identity.Environment);
            return Results.Json(new CloudGatewayAuthorizationResponse(result.Authorized, result.Code, identity), statusCode: result.Code switch
            {
                "unauthorized" => 401,
                "invalid_request" => 400,
                "capability_unavailable" => 503,
                _ when !result.Authorized => 403,
                _ => 200,
            });
        });
    }
}
