using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenConnect.Docs.Cloud;

public static class CloudKnowledgeEmbeddingPrivacyEndpoints
{
    public static void MapCloudKnowledgeEmbeddingPrivacy(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapPost("/cloud/internal/v1/knowledge-embed/privacy", (HttpContext context, CancellationToken cancellationToken) =>
            EvaluateAsync(context, trust, databaseConfigured, cancellationToken));
        app.MapPost("/cloud/internal/v1/knowledge-embed/privacy/response", (HttpContext context, CancellationToken cancellationToken) =>
            EvaluateAsync(context, trust, databaseConfigured, cancellationToken, response: true));
    }

    internal static async Task<IResult> EvaluateAsync(HttpContext context, CloudGatewayClientTrust trust, bool databaseConfigured,
        CancellationToken cancellationToken, bool response = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps || !trust.IsTrusted(context.Connection.ClientCertificate, DateTimeOffset.UtcNow))
            return Results.Unauthorized();
        if (!databaseConfigured)
            return Results.StatusCode(503);
        var authorizationHeaders = context.Request.Headers.Authorization;
        if (authorizationHeaders.Count != 1 || !AuthenticationHeaderValue.TryParse(authorizationHeaders.ToString(), out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Parameter) ||
            header.Parameter.Length > 160 || header.Parameter.Any(char.IsWhiteSpace))
            return Results.Unauthorized();
        var authorization = await context.RequestServices.GetRequiredService<CloudMachineAuthorizationService>()
            .AuthorizeAsync(header.Parameter, "knowledge.embed", cancellationToken);
        if (!authorization.Authorized || authorization.Identity is null)
            return Results.StatusCode(authorization.Code switch
            { "unauthorized" => 401, "capability_unavailable" => 503, _ => 403 });
        var ids = context.Request.Headers["X-Cloud-Request-Id"];
        var keys = context.Request.Headers["X-Cloud-Fingerprint-Key-Id"];
        var fingerprints = context.Request.Headers["X-Cloud-Request-Fingerprint"];
        if (ids.Count != 1 || !Guid.TryParseExact(ids.ToString(), "D", out var requestId) || requestId == Guid.Empty ||
            keys.Count != 1 || !CloudRequestFingerprint.IsValidKeyId(keys.ToString()) || fingerprints.Count != 1)
            return Results.BadRequest();
        byte[] fingerprint;
        try
        { fingerprint = Convert.FromBase64String(fingerprints.ToString()); }
        catch (FormatException) { return Results.BadRequest(); }
        var maximum = response ? 8 * 1024 * 1024 : 131072;
        var buffer = new byte[maximum + 1];
        try
        {
            if (fingerprint.Length != 32 || Convert.ToBase64String(fingerprint) != fingerprints.ToString())
                return Results.BadRequest();
            if (!context.Request.HasJsonContentType() || context.Request.Headers.ContainsKey("Content-Encoding"))
                return Results.StatusCode(415);
            if (context.Request.ContentLength > maximum)
                return Results.StatusCode(413);
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await context.Request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (count == 0)
                    break;
                length += count;
            }
            if (length > maximum)
                return Results.StatusCode(413);
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !Unique(root) ||
                !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.String ||
                schema.GetString() != (response ? "openconnect.v1" : "openconnect.cloud.knowledge-embed/v1") ||
                !root.TryGetProperty(response ? "kind" : "capability", out var capability) || capability.ValueKind != JsonValueKind.String || capability.GetString() != "knowledge.embed" ||
                !root.TryGetProperty(response ? "taskId" : "requestId", out var id) || id.ValueKind != JsonValueKind.String || !id.TryGetGuid(out var parsedId) || parsedId != requestId)
                return Results.BadRequest();
            var inputCharacters = response ? null : CountCharacters(root);
            if (!response && inputCharacters is null)
                return Results.BadRequest();
            var assessment = await context.RequestServices.GetRequiredService<CloudJsonPrivacyService>()
                .EvaluateWithEvidenceAsync(authorization.Identity, requestId, root, cancellationToken, embeddingResponse: response);
            var result = assessment.Decision;
            if (result.Code == "invalid_request")
                return Results.BadRequest();
            var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            await context.RequestServices.GetRequiredService<ICloudKnowledgeEmbeddingPrivacyEvidenceStore>()
                .AppendAsync(authorization.Identity, requestId, keys.ToString(), fingerprint, response ? "response" : "request", result.Code, traceId, assessment.Categories, inputCharacters, cancellationToken);
            return Results.Json(result, statusCode: result.Code switch { "allowed" => 200, "privacy_blocked" => 403, _ => 503 });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (JsonException) { return Results.BadRequest(); }
        catch { return Results.StatusCode(503); }
        finally { CryptographicOperations.ZeroMemory(fingerprint); CryptographicOperations.ZeroMemory(buffer); }
    }

    private static int? CountCharacters(JsonElement root)
    {
        if (!root.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty("inputKind", out var kind) || kind.ValueKind != JsonValueKind.String ||
            kind.GetString() is not ("query" or "passage") || !input.TryGetProperty("texts", out var texts) ||
            texts.ValueKind != JsonValueKind.Array || texts.GetArrayLength() is < 1 or > 32)
            return null;
        var total = 0;
        foreach (var item in texts.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return null;
            var value = item.GetString();
            if (string.IsNullOrEmpty(value))
                return null;
            var remaining = value.AsSpan();
            var count = 0;
            while (!remaining.IsEmpty)
            {
                if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done || ++count > 8192)
                    return null;
                remaining = remaining[consumed..];
            }
            total += count;
            if (total > 65536)
                return null;
        }
        return total;
    }

    private static bool Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !Unique(property.Value))
                    return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                if (!Unique(item))
                    return false;
        return true;
    }
}
