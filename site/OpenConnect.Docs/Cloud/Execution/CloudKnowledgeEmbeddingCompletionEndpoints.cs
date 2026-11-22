namespace OpenConnect.Docs.Cloud;

public static class CloudKnowledgeEmbeddingCompletionEndpoints
{
    public static void MapCloudKnowledgeEmbeddingCompletion(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured) =>
        app.MapPost("/cloud/internal/v1/knowledge-embed/quota/reservations/{reservationId:guid}/claims/{claimId:guid}/completion",
            (Guid reservationId, Guid claimId, HttpContext context, CancellationToken cancellationToken) =>
                CompleteAsync(reservationId, claimId, context, trust, databaseConfigured, cancellationToken));

    internal static async Task<IResult> CompleteAsync(Guid reservationId, Guid claimId, HttpContext context,
        CloudGatewayClientTrust trust, bool databaseConfigured, CancellationToken cancellationToken)
    {
        var (identity, error) = await CloudKnowledgeEmbeddingQuotaEndpoints.AuthorizeAsync(context, trust, databaseConfigured, cancellationToken);
        if (error is not null) return error;
        if (reservationId == Guid.Empty || claimId == Guid.Empty || context.Request.ContentLength is > 0 ||
            context.Request.Headers.ContainsKey("Transfer-Encoding") || context.Request.Headers.ContainsKey("Content-Encoding")) return Results.BadRequest();
        try
        {
            if (await context.Request.Body.ReadAsync(new byte[1], cancellationToken) != 0) return Results.BadRequest();
            var result = await context.RequestServices.GetRequiredService<ICloudKnowledgeEmbeddingCompletionStore>()
                .CompleteAsync(identity!.TenantId, identity.ApplicationId, reservationId, claimId, cancellationToken);
            return Results.Json(result, statusCode: result.Status switch
            { "completed" or "already_completed" => 200, "conflict" => 409, "invalid_request" => 400, _ => 503 });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Results.StatusCode(503); }
    }
}
