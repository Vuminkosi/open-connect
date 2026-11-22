using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Docs.Cloud;

public static class CloudTranslationPrivacyEndpoints
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, MaxDepth = 4 };

    public static void MapCloudTranslationPrivacy(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured) =>
        app.MapPost("/cloud/internal/v1/translation/privacy", (HttpContext context, CancellationToken cancellationToken) =>
            EvaluateAsync(context, trust, databaseConfigured, cancellationToken));

    internal static async Task<IResult> EvaluateAsync(HttpContext context, CloudGatewayClientTrust trust, bool databaseConfigured,
        CancellationToken cancellationToken)
    {
        var (identity, error) = await CloudTranslationAuthorization.AuthorizeAsync(context, trust, databaseConfigured, cancellationToken);
        if (error is not null)
            return error;
        try
        {
            return await InspectAsync(context, identity!, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Results.StatusCode(503); }
    }

    private static async Task<IResult> InspectAsync(HttpContext context, CloudCredentialIdentity identity, CancellationToken cancellationToken)
    {
        var ids = context.Request.Headers["X-Cloud-Request-Id"];
        var keys = context.Request.Headers["X-Cloud-Fingerprint-Key-Id"];
        var hashes = context.Request.Headers["X-Cloud-Request-Fingerprint"];
        if (ids.Count != 1 || !Guid.TryParseExact(ids.ToString(), "D", out var requestId) || requestId == Guid.Empty ||
            keys.Count != 1 || !CloudRequestFingerprint.IsValidKeyId(keys.ToString()) || hashes.Count != 1)
            return Results.BadRequest();
        byte[] fingerprint;
        try
        { fingerprint = Convert.FromBase64String(hashes.ToString()); }
        catch (FormatException) { return Results.BadRequest(); }
        try
        {
            if (fingerprint.Length != 32 || Convert.ToBase64String(fingerprint) != hashes.ToString())
                return Results.BadRequest();
            if (!context.Request.HasJsonContentType() || context.Request.Headers.ContainsKey("Content-Encoding"))
                return Results.StatusCode(415);
            const int maxBody = 65536;
            if (context.Request.ContentLength > maxBody)
                return Results.StatusCode(413);
            var buffer = new byte[maxBody + 1];
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
                if (length > maxBody)
                    return Results.StatusCode(413);
                using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return Results.BadRequest();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in document.RootElement.EnumerateObject())
                    if (!names.Add(property.Name))
                        return Results.BadRequest();
                var input = document.RootElement.Deserialize<CloudTranslationPrivacyInput>(Options);
                if (input is null)
                    return Results.BadRequest();
                var result = await context.RequestServices.GetRequiredService<CloudTranslationPrivacyService>()
                    .InspectInputAsync(identity, requestId, input, cancellationToken);
                if (result.Code == "invalid_request")
                    return Results.BadRequest();
                var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
                await context.RequestServices.GetRequiredService<ICloudTranslationPrivacyEvidenceStore>()
                    .AppendAsync(identity, requestId, keys.ToString(), fingerprint, result.Characters, result.Code, traceId, cancellationToken);
                return Results.Json(result, statusCode: result.Code switch { "allowed" => 200, "privacy_blocked" => 403, _ => 503 });
            }
            catch (JsonException) { return Results.BadRequest(); }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
        finally { CryptographicOperations.ZeroMemory(fingerprint); }
    }
}
