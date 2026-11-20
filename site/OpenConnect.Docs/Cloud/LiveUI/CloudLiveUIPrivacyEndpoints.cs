using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Docs.Cloud;

public static class CloudLiveUIPrivacyEndpoints
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 4,
    };

    public static void MapCloudLiveUIPrivacy(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapPost("/cloud/internal/v1/liveui/privacy", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps || !trust.IsTrusted(context.Connection.ClientCertificate, DateTimeOffset.UtcNow)) return Results.Unauthorized();
            if (!databaseConfigured) return Results.StatusCode(503);
            var headers = context.Request.Headers.Authorization;
            if (headers.Count != 1 || !AuthenticationHeaderValue.TryParse(headers.ToString(), out var header) ||
                !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Parameter) ||
                header.Parameter.Length > 160 || header.Parameter.Any(char.IsWhiteSpace)) return Results.Unauthorized();
            var authorization = await context.RequestServices.GetRequiredService<CloudMachineAuthorizationService>()
                .AuthorizeAsync(header.Parameter, "inference.liveui", cancellationToken);
            if (!authorization.Authorized || authorization.Identity is null) return Results.StatusCode(authorization.Code switch
            {
                "unauthorized" => 401, "capability_unavailable" => 503, _ => 403,
            });
            var ids = context.Request.Headers["X-Cloud-Request-Id"];
            if (ids.Count != 1 || !Guid.TryParseExact(ids.ToString(), "D", out var requestId) || requestId == Guid.Empty) return Results.BadRequest();
            var keys = context.Request.Headers["X-Cloud-Fingerprint-Key-Id"];
            var fingerprints = context.Request.Headers["X-Cloud-Request-Fingerprint"];
            if (keys.Count != 1 || !CloudRequestFingerprint.IsValidKeyId(keys.ToString()) || fingerprints.Count != 1) return Results.BadRequest();
            byte[] fingerprint;
            try { fingerprint = Convert.FromBase64String(fingerprints.ToString()); }
            catch (FormatException) { return Results.BadRequest(); }
            if (fingerprint.Length != 32 || Convert.ToBase64String(fingerprint) != fingerprints.ToString()) return Results.BadRequest();
            if (!context.Request.HasJsonContentType() || context.Request.Headers.ContainsKey("Content-Encoding")) return Results.StatusCode(415);
            const int maxBody = 262144;
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
                using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
                if (document.RootElement.ValueKind != JsonValueKind.Object) return Results.BadRequest();
                var properties = document.RootElement.EnumerateObject().ToArray();
                if (properties.Length != 1 || properties[0].Name != "fields" || properties[0].Value.ValueKind != JsonValueKind.Array) return Results.BadRequest();
                var input = document.RootElement.Deserialize<CloudLiveUIPrivacyRequest>(Options);
                if (input?.Fields is null) return Results.BadRequest();
                var result = await context.RequestServices.GetRequiredService<CloudLiveUIPrivacyService>()
                    .EvaluateAsync(authorization.Identity, requestId, input.Fields, cancellationToken);
                if (result.Code == "invalid_request") return Results.BadRequest();
                var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
                try
                {
                    await context.RequestServices.GetRequiredService<ICloudLiveUIPrivacyEvidenceStore>()
                        .AppendAsync(authorization.Identity, requestId, keys.ToString(), fingerprint, result.Code, traceId, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { return Results.StatusCode(503); }
                return Results.Json(result, statusCode: result.Code switch { "allowed" => 200, "privacy_blocked" => 403, _ => 503 });
            }
            catch (JsonException) { return Results.BadRequest(); }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        });
    }
}
