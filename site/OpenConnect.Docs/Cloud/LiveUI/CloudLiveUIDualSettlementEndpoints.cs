using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Docs.Cloud;

public static class CloudLiveUIDualSettlementEndpoints
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    public static void MapCloudLiveUIDualSettlement(this WebApplication app, CloudWorkerClientTrust trust, bool databaseConfigured)
    {
        app.MapPost("/cloud/internal/v1/liveui/quota/reservations/{reservationId:guid}/claims/{claimId:guid}/complete-metered",
            (Guid reservationId, Guid claimId, HttpContext context, CancellationToken cancellationToken) =>
                HandleAsync(reservationId, claimId, context, trust, databaseConfigured, "complete", cancellationToken));
        app.MapPost("/cloud/internal/v1/liveui/quota/reservations/{reservationId:guid}/claims/{claimId:guid}/fail-metered",
            (Guid reservationId, Guid claimId, HttpContext context, CancellationToken cancellationToken) =>
                HandleAsync(reservationId, claimId, context, trust, databaseConfigured, "fail", cancellationToken));
        app.MapPost("/cloud/internal/v1/liveui/quota/reservations/{reservationId:guid}/claims/{claimId:guid}/observe-metered",
            (Guid reservationId, Guid claimId, HttpContext context, CancellationToken cancellationToken) =>
                HandleAsync(reservationId, claimId, context, trust, databaseConfigured, "observe", cancellationToken));
    }

    private static async Task<IResult> HandleAsync(Guid reservationId, Guid claimId, HttpContext context,
        CloudWorkerClientTrust trust, bool databaseConfigured, string operation, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        var deploymentId = context.Request.IsHttps ? trust.ResolveDeployment(context.Connection.ClientCertificate, DateTimeOffset.UtcNow) : null;
        if (deploymentId is null)
            return Results.Unauthorized();
        if (!databaseConfigured)
            return Results.StatusCode(503);
        if (reservationId == Guid.Empty || claimId == Guid.Empty || !context.Request.HasJsonContentType() ||
            context.Request.Headers.ContainsKey("Content-Encoding") || context.Request.ContentLength > 1024)
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
            var usage = document.RootElement.Deserialize<CloudLiveUITokenUsage>(Options);
            if (usage is null)
                return Results.BadRequest();
            var settlement = context.RequestServices.GetRequiredService<CloudLiveUIDualSettlement>();
            var status = operation switch
            {
                "fail" => await settlement.FailAsync(reservationId, claimId, deploymentId.Value, usage, cancellationToken),
                "observe" => await settlement.ObserveAsync(reservationId, claimId, deploymentId.Value, usage, cancellationToken),
                _ => await settlement.SettleAsync(reservationId, claimId, deploymentId.Value, usage, cancellationToken),
            };
            return Results.Json(new { status }, statusCode: status switch
            {
                "completed" or "already_completed" or "failed" or "already_failed" or "observed" or "already_observed" => 200,
                "conflict" or "overage_requires_reconciliation" => 409,
                "invalid_request" => 400,
                _ => 503,
            });
        }
        catch (JsonException) { return Results.BadRequest(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Results.StatusCode(503); }
        finally { Array.Clear(buffer); }
    }
}
