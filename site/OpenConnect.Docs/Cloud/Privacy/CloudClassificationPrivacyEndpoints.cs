using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;

namespace OpenConnect.Docs.Cloud;

public static class CloudClassificationPrivacyEndpoints
{
    public static void MapCloudClassificationPrivacy(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapPost("/cloud/internal/v1/classification/privacy", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps || !trust.IsTrusted(context.Connection.ClientCertificate, DateTimeOffset.UtcNow)) return Results.Unauthorized();
            if (!databaseConfigured) return Results.StatusCode(503);
            var authorization = context.Request.Headers.Authorization;
            if (authorization.Count != 1 || !AuthenticationHeaderValue.TryParse(authorization.ToString(), out var header) ||
                !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Parameter) ||
                header.Parameter.Length > 160 || header.Parameter.Any(char.IsWhiteSpace)) return Results.Unauthorized();
            var service = context.RequestServices.GetRequiredService<CloudMachineAuthorizationService>();
            var decision = await service.AuthorizeAsync(header.Parameter, "classification.classify", cancellationToken);
            if (!decision.Authorized || decision.Identity is null)
                return Results.Json(new CloudPrivacyDecision(false, decision.Code), statusCode: decision.Code switch
                {
                    "unauthorized" => 401, "capability_unavailable" => 503, _ => 403,
                });
            var ids = context.Request.Headers["X-Cloud-Request-Id"];
            if (ids.Count != 1 || !Guid.TryParseExact(ids.ToString(), "D", out var requestId) || requestId == Guid.Empty)
                return Results.BadRequest();
            if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) ||
                !string.Equals(contentType.MediaType, "text/plain", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(contentType.CharSet, "utf-8", StringComparison.OrdinalIgnoreCase) ||
                contentType.Parameters.Count != 1 || context.Request.Headers.ContainsKey("Content-Encoding")) return Results.StatusCode(415);
            if (context.Request.ContentLength > 65536) return Results.StatusCode(413);
            var buffer = new byte[65537];
            try
            {
                var length = 0;
                while (length < buffer.Length)
                {
                    var count = await context.Request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
                    if (count == 0) break;
                    length += count;
                }
                if (length > 65536) return Results.StatusCode(413);
                var text = new UTF8Encoding(false, true).GetString(buffer, 0, length);
                if (string.IsNullOrEmpty(text) || text.EnumerateRunes().Count() > 20000) return Results.BadRequest();
                var privacy = context.RequestServices.GetRequiredService<CloudClassificationPrivacyService>();
                var assessment = await privacy.EvaluateWithEvidenceAsync(decision.Identity, requestId, text, cancellationToken);
                var result = assessment.Decision;
                var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
                try
                {
                    var evidence = context.RequestServices.GetRequiredService<ICloudPrivacyEvidenceStore>();
                    await evidence.AppendAsync(decision.Identity, requestId, result.Code, traceId, assessment.Categories, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { return Results.StatusCode(503); }
                return Results.Json(result, statusCode: result.Code switch { "allowed" => 200, "privacy_blocked" => 403, _ => 503 });
            }
            catch (DecoderFallbackException) { return Results.BadRequest(); }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        });
    }
}
