using System.Security.Claims;

namespace OpenConnect.Docs.Cloud;

public static class CloudLiveUIReconciliationEndpoints
{
    public static void MapCloudLiveUIReconciliation(this WebApplication app, bool databaseConfigured)
    {
        app.MapGet("/cloud/api/v1/operator/liveui/unknown-usage", async (DateTimeOffset? after, Guid? afterId, int? limit,
            ClaimsPrincipal principal, HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.RequestServices.GetRequiredService<CloudOperatorAccess>().IsAllowed(principal) is false)
                return Results.Forbid();
            if (!databaseConfigured)
                return Results.StatusCode(503);
            if (after.HasValue != afterId.HasValue)
                return Results.BadRequest();
            try
            {
                var cursor = after is null ? null : new CloudLiveUIReconciliationCursor(after.Value, afterId!.Value);
                var page = await context.RequestServices.GetRequiredService<CloudLiveUIReconciliation>()
                    .ListUnknownAsync(principal, cursor, limit ?? 50, cancellationToken);
                return page.Status == "succeeded" ? Results.Ok(page) : Results.BadRequest();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return Results.StatusCode(503); }
        }).RequireAuthorization("CloudApi");
    }
}
