using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Docs.Cloud;

public static class CloudClassificationDeploymentEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static void MapCloudClassificationDeployment(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapPost("/cloud/internal/v1/classification/deployment-match", async (HttpContext context, CancellationToken cancellationToken) =>
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
            var authorization = await context.RequestServices.GetRequiredService<CloudMachineAuthorizationService>()
                .AuthorizeAsync(header.Parameter, "classification.classify", cancellationToken);
            if (!authorization.Authorized || authorization.Identity is null)
                return Results.Json(new CloudClassificationDeploymentMatchResponse(false), statusCode: authorization.Code switch { "unauthorized" => 401, "capability_unavailable" => 503, _ => 403 });
            if (!context.Request.HasJsonContentType() || context.Request.Headers.ContainsKey("Content-Encoding"))
                return Results.StatusCode(415);
            if (context.Request.ContentLength > 4096)
                return Results.StatusCode(413);
            var buffer = new byte[4097];
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
                    return Results.StatusCode(413);
                using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return Results.BadRequest();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in document.RootElement.EnumerateObject())
                    if (!names.Add(property.Name))
                        return Results.BadRequest();
                var request = document.RootElement.Deserialize<CloudClassificationDeploymentMatchRequest>(JsonOptions);
                if (request is null)
                    return Results.BadRequest();
                var matched = await context.RequestServices.GetRequiredService<CloudClassificationDeploymentService>().MatchesAsync(authorization.Identity.TenantId, request, cancellationToken);
                return Results.Json(new CloudClassificationDeploymentMatchResponse(matched));
            }
            catch (JsonException) { return Results.BadRequest(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return Results.StatusCode(503); }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        });
    }
}
