using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Docs.Cloud;

public static class CloudTranslationClaimEndpoints
{
    private sealed record ClaimProof(string FingerprintKeyId, string Fingerprint, string WorkerPayloadFingerprint,
        CloudTranslationDeploymentMatchRequest Deployment);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, MaxDepth = 4 };

    public static void MapCloudTranslationClaim(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured) =>
        app.MapPost("/cloud/internal/v1/translation/quota/reservations/{reservationId:guid}/claim",
            (Guid reservationId, HttpContext context, CancellationToken cancellationToken) => ClaimAsync(reservationId, context, trust, databaseConfigured, cancellationToken));

    internal static async Task<IResult> ClaimAsync(Guid reservationId, HttpContext context, CloudGatewayClientTrust trust,
        bool databaseConfigured, CancellationToken cancellationToken)
    {
        var (identity, error) = await CloudTranslationAuthorization.AuthorizeAsync(context, trust, databaseConfigured, cancellationToken);
        if (error is not null) return error;
        if (reservationId == Guid.Empty) return Results.BadRequest();
        var proof = await ReadAsync(context.Request, cancellationToken);
        if (proof is null || !CloudRequestFingerprint.IsValidKeyId(proof.FingerprintKeyId) || proof.Fingerprint is null ||
            proof.WorkerPayloadFingerprint is null || !CloudTranslationDeploymentMatch.IsValid(proof.Deployment)) return Results.BadRequest();
        byte[]? fingerprint = null, workerFingerprint = null;
        try
        {
            try { fingerprint = Convert.FromBase64String(proof.Fingerprint); workerFingerprint = Convert.FromBase64String(proof.WorkerPayloadFingerprint); }
            catch (FormatException) { return Results.BadRequest(); }
            if (fingerprint.Length != 32 || workerFingerprint.Length != 32 || Convert.ToBase64String(fingerprint) != proof.Fingerprint ||
                Convert.ToBase64String(workerFingerprint) != proof.WorkerPayloadFingerprint) return Results.BadRequest();
            var result = await context.RequestServices.GetRequiredService<CloudTranslationClaim>().ClaimAsync(
                identity!, reservationId, proof.FingerprintKeyId, fingerprint, workerFingerprint, proof.Deployment, cancellationToken);
            return Results.Json(result, statusCode: result.Status switch { "claimed" => 200, "already_claimed" or "idempotency_conflict" => 409, "invalid_request" => 400, _ => 503 });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Results.StatusCode(503); }
        finally
        {
            if (fingerprint is not null) CryptographicOperations.ZeroMemory(fingerprint);
            if (workerFingerprint is not null) CryptographicOperations.ZeroMemory(workerFingerprint);
        }
    }

    private static async Task<ClaimProof?> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType() || request.Headers.ContainsKey("Content-Encoding") || request.ContentLength > 4096) return null;
        var buffer = new byte[4097];
        try
        {
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (count == 0) break;
                length += count;
            }
            if (length > 4096) return null;
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !Unique(document.RootElement)) return null;
            return document.RootElement.Deserialize<ClaimProof>(Options);
        }
        catch (JsonException) { return null; }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static bool Unique(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return true;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject()) if (!names.Add(property.Name) || !Unique(property.Value)) return false;
        return true;
    }
}
