namespace OpenConnect.Docs.Cloud;

public static class CloudPrivateReadinessEndpoints
{
    public static void MapCloudGatewayReadiness(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapGet("/cloud/internal/v1/gateway/ready", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps || !trust.IsTrusted(context.Connection.ClientCertificate, DateTimeOffset.UtcNow))
                return Results.Unauthorized();
            if (!databaseConfigured)
                return Results.StatusCode(503);
            try
            {
                return await context.RequestServices.GetRequiredService<CloudReadinessService>().IsReadyAsync(cancellationToken)
                    ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return Results.StatusCode(503); }
        });
    }

    public static void MapCloudWorkerReadiness(this WebApplication app, CloudWorkerClientTrust trust, bool databaseConfigured)
    {
        app.MapGet("/cloud/internal/v1/worker/ready", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var deploymentId = context.Request.IsHttps ? trust.ResolveDeployment(context.Connection.ClientCertificate, DateTimeOffset.UtcNow) : null;
            if (deploymentId is null)
                return Results.Unauthorized();
            if (!databaseConfigured)
                return Results.StatusCode(503);
            try
            {
                return await context.RequestServices.GetRequiredService<CloudWorkerReadiness>().IsReadyAsync(deploymentId.Value, cancellationToken)
                    ? Results.Ok(new { status = "ready", deploymentId = deploymentId.Value }) : Results.StatusCode(503);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return Results.StatusCode(503); }
        });
    }
}
