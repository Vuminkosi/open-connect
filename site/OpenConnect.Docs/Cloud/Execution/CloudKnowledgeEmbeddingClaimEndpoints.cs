using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Docs.Cloud;

public static class CloudKnowledgeEmbeddingClaimEndpoints
{
    private sealed record Proof(string FingerprintKeyId, string Fingerprint, string WorkerPayloadFingerprint,
        string EndpointReference, string Region, string ContractVersion, string ModelRevision, string ModelDigest);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };

    public static void MapCloudKnowledgeEmbeddingClaim(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured) =>
        app.MapPost("/cloud/internal/v1/knowledge-embed/quota/reservations/{reservationId:guid}/claim",
            (Guid reservationId, HttpContext context, CancellationToken cancellationToken) =>
                ClaimAsync(reservationId, context, trust, databaseConfigured, cancellationToken));

    internal static async Task<IResult> ClaimAsync(Guid reservationId, HttpContext context, CloudGatewayClientTrust trust,
        bool databaseConfigured, CancellationToken cancellationToken)
    {
        var (identity, error) = await CloudKnowledgeEmbeddingQuotaEndpoints.AuthorizeAsync(context, trust, databaseConfigured, cancellationToken);
        if (error is not null)
            return error;
        if (reservationId == Guid.Empty)
            return Results.BadRequest();
        var proof = await ReadAsync(context.Request, cancellationToken);
        if (proof is null || !CloudRequestFingerprint.IsValidKeyId(proof.FingerprintKeyId) ||
            proof.Fingerprint is null || proof.WorkerPayloadFingerprint is null)
            return Results.BadRequest();
        var deployment = new CloudClassificationDeploymentMatchRequest(proof.EndpointReference, proof.Region, proof.ContractVersion,
            proof.ModelRevision, proof.ModelDigest);
        if (!CloudClassificationDeploymentService.IsValid(deployment))
            return Results.BadRequest();
        byte[]? fingerprint = null;
        byte[]? workerFingerprint = null;
        try
        {
            fingerprint = Convert.FromBase64String(proof.Fingerprint);
            workerFingerprint = Convert.FromBase64String(proof.WorkerPayloadFingerprint);
            if (fingerprint.Length != 32 || workerFingerprint.Length != 32 ||
                Convert.ToBase64String(fingerprint) != proof.Fingerprint ||
                Convert.ToBase64String(workerFingerprint) != proof.WorkerPayloadFingerprint)
                return Results.BadRequest();
            var result = await context.RequestServices.GetRequiredService<CloudExecutionClaimService>().ClaimAsync(
                identity!.CredentialId, identity.TenantId, identity.ApplicationId, reservationId, "knowledge.embed", proof.FingerprintKeyId,
                fingerprint, deployment, cancellationToken, workerFingerprint);
            return Results.Json(result, statusCode: result.Status switch
            { "claimed" => 200, "already_claimed" or "idempotency_conflict" => 409, "invalid_request" => 400, _ => 503 });
        }
        catch (FormatException) { return Results.BadRequest(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Results.StatusCode(503); }
        finally
        {
            if (fingerprint is not null)
                CryptographicOperations.ZeroMemory(fingerprint);
            if (workerFingerprint is not null)
                CryptographicOperations.ZeroMemory(workerFingerprint);
        }
    }

    private static async Task<Proof?> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
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
            return document.RootElement.Deserialize<Proof>(Options);
        }
        catch (JsonException) { return null; }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
