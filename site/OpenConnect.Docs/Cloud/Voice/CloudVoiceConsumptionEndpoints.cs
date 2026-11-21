using System.Security.Cryptography;

namespace OpenConnect.Docs.Cloud;

public static class CloudVoiceConsumptionEndpoints
{
    public static void MapCloudVoiceConsumption(this WebApplication app, CloudWorkerClientTrust trust, bool databaseConfigured) =>
        app.MapPost("/cloud/internal/v1/voice-synthesis/quota/reservations/{reservationId:guid}/claims/{claimId:guid}/consume",
            (Guid reservationId, Guid claimId, HttpContext context, CancellationToken cancellationToken) =>
                ConsumeAsync(reservationId, claimId, context, trust, databaseConfigured, cancellationToken));

    internal static async Task<IResult> ConsumeAsync(Guid reservationId, Guid claimId, HttpContext context,
        CloudWorkerClientTrust trust, bool databaseConfigured, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        var deploymentId = context.Request.IsHttps ? trust.ResolveDeployment(context.Connection.ClientCertificate, DateTimeOffset.UtcNow) : null;
        if (deploymentId is null) return Results.Unauthorized();
        if (!databaseConfigured) return Results.StatusCode(503);
        if (reservationId == Guid.Empty || claimId == Guid.Empty || context.Request.ContentLength is > 0 ||
            context.Request.Headers.ContainsKey("Transfer-Encoding") || context.Request.Headers.ContainsKey("Content-Encoding")) return Results.BadRequest();
        var header = context.Request.Headers["X-OpenConnect-Worker-Payload-Fingerprint"];
        if (header.Count != 1) return Results.BadRequest();
        byte[] fingerprint;
        try { fingerprint = Convert.FromBase64String(header.ToString()); }
        catch (FormatException) { return Results.BadRequest(); }
        try
        {
            if (fingerprint.Length != 32 || Convert.ToBase64String(fingerprint) != header.ToString() ||
                await context.Request.Body.ReadAsync(new byte[1], cancellationToken) != 0) return Results.BadRequest();
            var result = await context.RequestServices.GetRequiredService<CloudVoiceConsumptionService>()
                .ConsumeAsync(reservationId, claimId, deploymentId.Value, fingerprint, cancellationToken);
            return Results.Json(new { status = result }, statusCode: result switch
            { "consumed" => 200, "already_consumed" => 409, "invalid_request" => 400, _ => 503 });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Results.StatusCode(503); }
        finally { CryptographicOperations.ZeroMemory(fingerprint); }
    }
}
