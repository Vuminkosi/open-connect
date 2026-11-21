using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Docs.Cloud;

public static class CloudVoiceQuotaEndpoints
{
    private sealed record ReserveRequest(Guid RequestId, string FingerprintKeyId, string Fingerprint, long Characters, DateTimeOffset ExpiresAt);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, MaxDepth = 4 };

    public static void MapCloudVoiceQuota(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapGet("/cloud/internal/v1/voice-synthesis/quota/fingerprint-key/{requestId:guid}",
            (Guid requestId, HttpContext context, CancellationToken cancellationToken) => LookupAsync(context, trust, databaseConfigured, requestId, cancellationToken));
        app.MapPost("/cloud/internal/v1/voice-synthesis/quota/reservations",
            (HttpContext context, CancellationToken cancellationToken) => ReserveAsync(context, trust, databaseConfigured, cancellationToken));
    }

    internal static async Task<IResult> LookupAsync(HttpContext context, CloudGatewayClientTrust trust, bool databaseConfigured,
        Guid requestId, CancellationToken cancellationToken)
    {
        var (identity, error) = await AuthorizeAsync(context, trust, databaseConfigured, cancellationToken);
        if (error is not null) return error;
        if (requestId == Guid.Empty) return Results.BadRequest();
        try
        {
            var result = await context.RequestServices.GetRequiredService<CloudQuotaService>()
                .FindFingerprintKeyAsync(identity!.TenantId, identity.ApplicationId, "voice.synthesize", requestId, cancellationToken);
            return Results.Json(result, statusCode: 200);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Results.StatusCode(503); }
    }

    internal static async Task<IResult> ReserveAsync(HttpContext context, CloudGatewayClientTrust trust, bool databaseConfigured,
        CancellationToken cancellationToken)
    {
        var (identity, error) = await AuthorizeAsync(context, trust, databaseConfigured, cancellationToken);
        if (error is not null) return error;
        var request = await ReadAsync(context.Request, cancellationToken);
        if (request is null || request.RequestId == Guid.Empty || request.Characters is < 1 or > 2000 ||
            !CloudRequestFingerprint.IsValidKeyId(request.FingerprintKeyId) || request.Fingerprint is null) return Results.BadRequest();
        byte[] fingerprint;
        try { fingerprint = Convert.FromBase64String(request.Fingerprint); }
        catch (FormatException) { return Results.BadRequest(); }
        try
        {
            if (fingerprint.Length != 32 || Convert.ToBase64String(fingerprint) != request.Fingerprint) return Results.BadRequest();
            var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            if (request.ExpiresAt <= now || request.ExpiresAt > now.AddMinutes(5)) return Results.BadRequest();
            var result = await context.RequestServices.GetRequiredService<CloudQuotaService>().ReserveAsync(new CloudQuotaRequest(
                identity!.TenantId, identity.ApplicationId, "voice.synthesize", "character", request.RequestId,
                request.FingerprintKeyId, fingerprint, request.Characters, request.ExpiresAt), cancellationToken);
            return Results.Json(result, statusCode: result.Status switch
            { "reserved" or "duplicate" => 200, "quota_exceeded" => 429, "idempotency_conflict" => 409, "invalid_request" => 400, _ => 503 });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Results.StatusCode(503); }
        finally { CryptographicOperations.ZeroMemory(fingerprint); }
    }

    internal static async Task<(CloudCredentialIdentity? Identity, IResult? Error)> AuthorizeAsync(HttpContext context,
        CloudGatewayClientTrust trust, bool databaseConfigured, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps || !trust.IsTrusted(context.Connection.ClientCertificate, DateTimeOffset.UtcNow)) return (null, Results.Unauthorized());
        if (!databaseConfigured) return (null, Results.StatusCode(503));
        var headers = context.Request.Headers.Authorization;
        if (headers.Count != 1 || !AuthenticationHeaderValue.TryParse(headers.ToString(), out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Parameter) ||
            header.Parameter.Length > 160 || header.Parameter.Any(char.IsWhiteSpace)) return (null, Results.Unauthorized());
        try
        {
            var authorization = await context.RequestServices.GetRequiredService<CloudMachineAuthorizationService>()
                .AuthorizeAsync(header.Parameter, "voice.synthesize", cancellationToken);
            return authorization.Authorized && authorization.Identity is not null ? (authorization.Identity, null) :
                (null, Results.StatusCode(authorization.Code switch { "unauthorized" => 401, "capability_unavailable" => 503, _ => 403 }));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return (null, Results.StatusCode(503)); }
    }

    private static async Task<ReserveRequest?> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType() || request.Headers.ContainsKey("Content-Encoding") || request.ContentLength > 1024) return null;
        var buffer = new byte[1025];
        try
        {
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (count == 0) break;
                length += count;
            }
            if (length > 1024) return null;
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject()) if (!names.Add(property.Name)) return null;
            return document.RootElement.Deserialize<ReserveRequest>(Options);
        }
        catch (JsonException) { return null; }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
