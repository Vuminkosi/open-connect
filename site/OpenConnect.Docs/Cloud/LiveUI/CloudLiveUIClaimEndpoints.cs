using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Docs.Cloud;

public static class CloudLiveUIClaimEndpoints
{
    private sealed record ClaimProof(string FingerprintKeyId, string Fingerprint, string EndpointReference, string Region,
        string ContractVersion, string ModelRevision, string ModelDigest, string ModelId, string WorkerPayloadFingerprint);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };

    public static void MapCloudLiveUIClaim(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapPost("/cloud/internal/v1/liveui/quota/reservations/{reservationId:guid}/claim", async (
            Guid reservationId, HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps || !trust.IsTrusted(context.Connection.ClientCertificate, DateTimeOffset.UtcNow))
                return Results.Unauthorized();
            if (!databaseConfigured)
                return Results.StatusCode(503);
            var headers = context.Request.Headers.Authorization;
            if (headers.Count != 1 || !AuthenticationHeaderValue.TryParse(headers.ToString(), out var header) ||
                !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Parameter) ||
                header.Parameter.Length > 160 || header.Parameter.Any(char.IsWhiteSpace))
                return Results.Unauthorized();
            var authorization = await context.RequestServices.GetRequiredService<CloudMachineAuthorizationService>()
                .AuthorizeAsync(header.Parameter, "inference.liveui", cancellationToken);
            if (!authorization.Authorized || authorization.Identity is null)
                return Results.StatusCode(authorization.Code switch
                {
                    "unauthorized" => 401,
                    "capability_unavailable" => 503,
                    _ => 403,
                });
            if (reservationId == Guid.Empty)
                return Results.BadRequest();
            var proof = await ReadAsync(context.Request, cancellationToken);
            if (proof is null || !CloudRequestFingerprint.IsValidKeyId(proof.FingerprintKeyId) || proof.Fingerprint is null || proof.WorkerPayloadFingerprint is null)
                return Results.BadRequest();
            var deployment = new CloudInferenceDeploymentMatch(proof.EndpointReference, proof.Region, proof.ContractVersion, proof.ModelRevision, proof.ModelDigest);
            if (!deployment.IsValid() || !CloudInferenceModelPolicyService.ValidModelId(proof.ModelId))
                return Results.BadRequest();
            byte[] fingerprint, workerFingerprint;
            try
            { fingerprint = Convert.FromBase64String(proof.Fingerprint); workerFingerprint = Convert.FromBase64String(proof.WorkerPayloadFingerprint); }
            catch (FormatException) { return Results.BadRequest(); }
            if (fingerprint.Length != 32 || workerFingerprint.Length != 32 || Convert.ToBase64String(fingerprint) != proof.Fingerprint ||
                Convert.ToBase64String(workerFingerprint) != proof.WorkerPayloadFingerprint)
                return Results.BadRequest();
            try
            {
                var result = await context.RequestServices.GetRequiredService<CloudLiveUIClaimService>().ClaimAsync(
                    authorization.Identity, reservationId, proof.FingerprintKeyId, fingerprint, workerFingerprint, proof.ModelId, deployment, cancellationToken);
                return Results.Json(result, statusCode: result.Status switch { "claimed" => 200, "already_claimed" or "idempotency_conflict" => 409, "invalid_request" => 400, _ => 503 });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return Results.StatusCode(503); }
        });
    }

    private static async Task<ClaimProof?> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType() || request.Headers.ContainsKey("Content-Encoding") || request.ContentLength > 2048)
            return null;
        var buffer = new byte[2049];
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
            if (length > 2048)
                return null;
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name))
                    return null;
            return document.RootElement.Deserialize<ClaimProof>(Options);
        }
        catch (JsonException) { return null; }
    }
}
