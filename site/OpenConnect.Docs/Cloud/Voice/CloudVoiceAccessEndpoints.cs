namespace OpenConnect.Docs.Cloud;

public static class CloudVoiceAccessEndpoints
{
    public static void MapCloudVoiceAccess(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured) =>
        app.MapGet("/cloud/internal/v1/voice-synthesis/claims/{reservationId:guid}/{claimId:guid}/access",
            (Guid reservationId, Guid claimId, HttpContext context, CancellationToken cancellationToken) =>
                CheckAsync(reservationId, claimId, context, trust, databaseConfigured, cancellationToken));

    internal static async Task<IResult> CheckAsync(Guid reservationId, Guid claimId, HttpContext context,
        CloudGatewayClientTrust trust, bool databaseConfigured, CancellationToken cancellationToken)
    {
        var (identity, error) = await CloudVoiceQuotaEndpoints.AuthorizeAsync(context, trust, databaseConfigured, cancellationToken);
        if (error is not null)
            return error;
        if (reservationId == Guid.Empty || claimId == Guid.Empty)
            return Results.BadRequest();
        try
        {
            var allowed = await context.RequestServices.GetRequiredService<ICloudVoiceAccessStore>()
                .IsClaimAllowedAsync(identity!, reservationId, claimId, cancellationToken);
            return allowed ? Results.Json(new { allowed = true }, statusCode: 200) : Results.StatusCode(503);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Results.StatusCode(503); }
    }
}
