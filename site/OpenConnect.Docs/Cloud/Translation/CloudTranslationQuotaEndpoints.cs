using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Docs.Cloud;

public static class CloudTranslationQuotaEndpoints
{
    private sealed record ReserveRequest(Guid RequestId, string FingerprintKeyId, string Fingerprint, long Characters, DateTimeOffset ExpiresAt);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, MaxDepth = 4 };

    public static void MapCloudTranslationQuota(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapGet("/cloud/internal/v1/translation/quota/fingerprint-key/{requestId:guid}",
            (Guid requestId, HttpContext context, CancellationToken ct) => LookupAsync(context, trust, databaseConfigured, requestId, ct));
        app.MapPost("/cloud/internal/v1/translation/quota/reservations",
            (HttpContext context, CancellationToken ct) => ReserveAsync(context, trust, databaseConfigured, ct));
    }

    internal static async Task<IResult> LookupAsync(HttpContext context, CloudGatewayClientTrust trust, bool databaseConfigured,
        Guid requestId, CancellationToken cancellationToken)
    {
        var (identity, error) = await CloudTranslationAuthorization.AuthorizeAsync(context, trust, databaseConfigured, cancellationToken);
        if (error is not null)
            return error;
        if (requestId == Guid.Empty)
            return Results.BadRequest();
        try
        {
            var result = await context.RequestServices.GetRequiredService<CloudQuotaService>()
                .FindFingerprintKeyAsync(identity!.TenantId, identity.ApplicationId, "translation.translate", requestId, cancellationToken);
            return Results.Json(result, statusCode: 200);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Results.StatusCode(503); }
    }

    internal static async Task<IResult> ReserveAsync(HttpContext context, CloudGatewayClientTrust trust, bool databaseConfigured,
        CancellationToken cancellationToken)
    {
        var (identity, error) = await CloudTranslationAuthorization.AuthorizeAsync(context, trust, databaseConfigured, cancellationToken);
        if (error is not null)
            return error;
        var request = await ReadAsync(context.Request, cancellationToken);
        if (request is null || request.RequestId == Guid.Empty || request.Characters is < 1 or > 5000 ||
            !CloudRequestFingerprint.IsValidKeyId(request.FingerprintKeyId) || request.Fingerprint is null)
            return Results.BadRequest();
        byte[] fingerprint;
        try
        { fingerprint = Convert.FromBase64String(request.Fingerprint); }
        catch (FormatException) { return Results.BadRequest(); }
        try
        {
            if (fingerprint.Length != 32 || Convert.ToBase64String(fingerprint) != request.Fingerprint)
                return Results.BadRequest();
            var result = await context.RequestServices.GetRequiredService<CloudTranslationQuota>().ReserveAsync(new CloudQuotaRequest(
                identity!.TenantId, identity.ApplicationId, "translation.translate", "character", request.RequestId,
                request.FingerprintKeyId, fingerprint, request.Characters, request.ExpiresAt), cancellationToken);
            return Results.Json(result, statusCode: result.Status switch
            { "reserved" or "duplicate" => 200, "quota_exceeded" => 429, "idempotency_conflict" => 409, "invalid_request" => 400, _ => 503 });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Results.StatusCode(503); }
        finally { CryptographicOperations.ZeroMemory(fingerprint); }
    }

    private static async Task<ReserveRequest?> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType() || request.Headers.ContainsKey("Content-Encoding") || request.ContentLength > 1024)
            return null;
        var buffer = new byte[1025];
        try
        {
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (count == 0)
                    break;
                length += count;
            }
            if (length > 1024)
                return null;
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name))
                    return null;
            return document.RootElement.Deserialize<ReserveRequest>(Options);
        }
        catch (JsonException) { return null; }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
