using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenConnect.Docs.Cloud;

public static class CloudInferenceOutputPrivacyEndpoints
{
    public static void MapCloudInferenceOutputPrivacy(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapPost("/cloud/internal/v1/inference/output-privacy", async (HttpContext context, CancellationToken cancellationToken) =>
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
            if (!ReadId(context, "X-Cloud-Request-Id", out var requestId) || !ReadId(context, "X-Cloud-Reservation-Id", out var reservationId) ||
                !ReadId(context, "X-Cloud-Claim-Id", out var claimId)) return Results.BadRequest();
            if (!context.Request.HasJsonContentType() || context.Request.Headers.ContainsKey("Content-Encoding")) return Results.StatusCode(415);
            const int maxBody = 786500;
            if (context.Request.ContentLength > maxBody) return Results.StatusCode(413);
            var buffer = new byte[maxBody + 1];
            try
            {
                var length = 0;
                while (length < buffer.Length)
                {
                    var count = await context.Request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
                    if (count == 0) break;
                    length += count;
                }
                if (length > maxBody) return Results.StatusCode(413);
                using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 3 });
                if (document.RootElement.ValueKind != JsonValueKind.Object) return Results.BadRequest();
                var properties = document.RootElement.EnumerateObject().ToArray();
                if (properties.Length != 1 || properties[0].Name != "output" || properties[0].Value.ValueKind != JsonValueKind.String)
                    return Results.BadRequest();
                var output = properties[0].Value.GetString();
                if (output is null) return Results.BadRequest();
                var result = await context.RequestServices.GetRequiredService<CloudInferenceOutputPrivacyService>()
                    .EvaluateAsync(authorization.Identity, requestId, output, cancellationToken);
                if (result.Code == "invalid_request") return Results.BadRequest();
                var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
                try
                {
                    var recorded = await context.RequestServices.GetRequiredService<ICloudInferenceOutputEvidenceStore>()
                        .AppendForCompletedAsync(authorization.Identity, requestId, reservationId, claimId, result.Code, traceId, cancellationToken);
                    if (!recorded) return Results.StatusCode(503);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { return Results.StatusCode(503); }
                return Results.Json(result, statusCode: result.Code switch { "allowed" => 200, "privacy_blocked" => 403, _ => 503 });
            }
            catch (JsonException) { return Results.BadRequest(); }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        });
    }

    private static bool ReadId(HttpContext context, string name, out Guid id)
    {
        var values = context.Request.Headers[name];
        id = Guid.Empty;
        return values.Count == 1 && Guid.TryParseExact(values.ToString(), "D", out id) && id != Guid.Empty;
    }
}
