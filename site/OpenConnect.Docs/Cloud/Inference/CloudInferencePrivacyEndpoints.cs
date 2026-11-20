using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Docs.Cloud;

public static class CloudInferencePrivacyEndpoints
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8,
    };

    public static void MapCloudInferencePrivacy(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapPost("/cloud/internal/v1/inference/privacy", async (HttpContext context, CancellationToken cancellationToken) =>
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
            if (context.Request.ContentLength > 131072) return Results.StatusCode(413);
            var buffer = new byte[131073];
            try
            {
                var length = 0;
                while (length < buffer.Length)
                {
                    var count = await context.Request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
                    if (count == 0) break;
                    length += count;
                }
                if (length > 131072) return Results.StatusCode(413);
                using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 8 });
                if (document.RootElement.ValueKind != JsonValueKind.Object || !Unique(document.RootElement)) return Results.BadRequest();
                var input = document.RootElement.Deserialize<CloudInferencePrivacyRequest>(Options);
                if (input?.Messages is null) return Results.BadRequest();
                var result = await context.RequestServices.GetRequiredService<CloudInferencePrivacyService>()
                    .EvaluateAsync(authorization.Identity, requestId, input.Messages, cancellationToken);
                if (result.Code == "invalid_request") return Results.BadRequest();
                var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
                try
                {
                    await context.RequestServices.GetRequiredService<ICloudInferencePrivacyEvidenceStore>()
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

    private static bool Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject()) if (!names.Add(property.Name) || !Unique(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (!Unique(item)) return false;
        return true;
    }
}
