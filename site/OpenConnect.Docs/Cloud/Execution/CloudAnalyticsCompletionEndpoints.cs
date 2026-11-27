using System.Security.Cryptography;
using System.Text.Json;

namespace OpenConnect.Docs.Cloud;

public static class CloudAnalyticsCompletionEndpoints
{
    public static void MapCloudAnalyticsCompletion(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured) =>
        app.MapPost("/cloud/internal/v1/analytics/quota/reservations/{reservationId:guid}/claims/{claimId:guid}/completion",
            (Guid reservationId, Guid claimId, HttpContext context, CancellationToken cancellationToken) =>
                CompleteAsync(reservationId, claimId, context, trust, databaseConfigured, cancellationToken));

    internal static async Task<IResult> CompleteAsync(Guid reservationId, Guid claimId, HttpContext context,
        CloudGatewayClientTrust trust, bool databaseConfigured, CancellationToken cancellationToken)
    {
        var (identity, error) = await CloudAnalyticsQuotaEndpoints.AuthorizeAsync(context, trust, databaseConfigured, cancellationToken);
        if (error is not null)
            return error;
        if (reservationId == Guid.Empty || claimId == Guid.Empty || !context.Request.HasJsonContentType() ||
            context.Request.ContentLength is > 128 || context.Request.Headers.ContainsKey("Content-Encoding"))
            return Results.BadRequest();
        var buffer = new byte[129];
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
            if (length > 128)
                return Results.BadRequest();
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Results.BadRequest();
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != "computeMilliseconds" || properties[0].Value.ValueKind != JsonValueKind.Number ||
                !properties[0].Value.TryGetInt32(out var milliseconds) || milliseconds is < 1 or > 120000)
                return Results.BadRequest();
            var result = await context.RequestServices.GetRequiredService<ICloudAnalyticsCompletionStore>()
                .CompleteAsync(identity!.TenantId, identity.ApplicationId, reservationId, claimId, milliseconds, cancellationToken);
            return Results.Json(result, statusCode: result.Status switch
            { "completed" or "already_completed" => 200, "conflict" => 409, "invalid_request" => 400, _ => 503 });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (JsonException) { return Results.BadRequest(); }
        catch { return Results.StatusCode(503); }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
