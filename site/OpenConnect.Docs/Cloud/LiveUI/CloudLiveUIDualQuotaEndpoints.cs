using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Docs.Cloud;

public static class CloudLiveUIDualQuotaEndpoints
{
    private sealed record Reservation(Guid RequestId, string FingerprintKeyId, string Fingerprint, long TokenAmount, DateTimeOffset ExpiresAt);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 4,
    };

    public static void MapCloudLiveUIDualQuota(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapPost("/cloud/internal/v1/liveui/quota/dual-reservations", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            var authorization = await CloudLiveUIQuotaEndpoints.AuthorizeAsync(context, trust, databaseConfigured, cancellationToken);
            if (authorization.Result is not null)
                return authorization.Result;
            if (!context.Request.HasJsonContentType() || context.Request.Headers.ContainsKey("Content-Encoding") || context.Request.ContentLength > 1024)
                return Results.BadRequest();
            var buffer = new byte[1025];
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
                if (length > 1024)
                    return Results.BadRequest();
                using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return Results.BadRequest();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in document.RootElement.EnumerateObject())
                    if (!names.Add(property.Name))
                        return Results.BadRequest();
                var request = document.RootElement.Deserialize<Reservation>(Options);
                if (request is null || request.RequestId == Guid.Empty || !CloudRequestFingerprint.IsValidKeyId(request.FingerprintKeyId) ||
                    request.Fingerprint is null || request.TokenAmount is < 256 or > 32768)
                    return Results.BadRequest();
                byte[] fingerprint;
                try
                { fingerprint = Convert.FromBase64String(request.Fingerprint); }
                catch (FormatException) { return Results.BadRequest(); }
                if (fingerprint.Length != 32 || Convert.ToBase64String(fingerprint) != request.Fingerprint)
                    return Results.BadRequest();
                var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                if (request.ExpiresAt <= now || request.ExpiresAt > now.AddMinutes(5))
                    return Results.BadRequest();
                var identity = authorization.Identity!;
                var result = await context.RequestServices.GetRequiredService<CloudLiveUIDualQuotaService>().ReserveAsync(
                    new(identity.TenantId, identity.ApplicationId, request.RequestId, request.FingerprintKeyId, fingerprint, request.TokenAmount, request.ExpiresAt), cancellationToken);
                return Results.Json(result, statusCode: result.Status switch
                {
                    "reserved" or "duplicate" => 200,
                    "quota_exceeded" => 429,
                    "idempotency_conflict" => 409,
                    "invalid_request" => 400,
                    _ => 503,
                });
            }
            catch (JsonException) { return Results.BadRequest(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return Results.StatusCode(503); }
            finally { Array.Clear(buffer); }
        });
    }
}
