namespace OpenConnect.Docs.Cloud;

public static class CloudLiveUISettlementEndpoints
{
    public static void MapCloudLiveUISettlement(this WebApplication app, CloudWorkerClientTrust trust, bool databaseConfigured)
    {
        app.MapPost("/cloud/internal/v1/liveui/quota/reservations/{reservationId:guid}/claims/{claimId:guid}/complete", async (
            Guid reservationId, Guid claimId, HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var deploymentId = context.Request.IsHttps ? trust.ResolveDeployment(context.Connection.ClientCertificate, DateTimeOffset.UtcNow) : null;
            if (deploymentId is null)
                return Results.Unauthorized();
            if (!databaseConfigured)
                return Results.StatusCode(503);
            if (reservationId == Guid.Empty || claimId == Guid.Empty || context.Request.ContentLength is > 0 ||
                context.Request.Headers.ContainsKey("Transfer-Encoding") || context.Request.Headers.ContainsKey("Content-Encoding"))
                return Results.BadRequest();
            try
            {
                if (await context.Request.Body.ReadAsync(new byte[1], cancellationToken) != 0)
                    return Results.BadRequest();
                var status = await context.RequestServices.GetRequiredService<CloudLiveUISettlement>().SettleAsync(reservationId, claimId, deploymentId.Value, cancellationToken);
                return Results.Json(new { status }, statusCode: status switch { "completed" or "already_completed" => 200, "conflict" => 409, "invalid_request" => 400, _ => 503 });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return Results.StatusCode(503); }
        });
    }
}
