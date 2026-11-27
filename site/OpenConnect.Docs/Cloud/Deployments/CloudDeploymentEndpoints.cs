using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;

namespace OpenConnect.Docs.Cloud;

public static class CloudDeploymentEndpoints
{
    public static void MapCloudDeploymentEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/operator/deployments", async (string? capability, int? offset, int? limit,
            ClaimsPrincipal principal, HttpContext context, CloudOperatorAccess operators,
            CloudDeploymentRegistry registry, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!operators.IsAllowed(principal))
                return Results.Forbid();
            var start = offset ?? 0;
            var size = limit ?? 50;
            if (!CloudDeploymentRegistry.ValidToken(capability, 80) || start is < 0 or > 1000000 || size is < 1 or > 100)
                return Results.BadRequest();
            var records = await registry.ListAsync(capability!, start, size + 1, cancellationToken);
            var hasMore = records.Count > size;
            return Results.Ok(new { deployments = records.Take(size), offset = start, nextOffset = hasMore ? (int?)(start + size) : null });
        });

        group.MapPut("/operator/deployments/{deploymentId:guid}", async (Guid deploymentId, ClaimsPrincipal principal,
            HttpContext context, CloudOperatorAccess operators, CloudDeploymentRegistry registry, IAntiforgery antiforgery,
            IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            var actor = CloudSubjectResolver.FromPrincipal(principal, configuration["OpenConnect:Cloud:Identity:Authority"]);
            if (actor is null)
                return Results.Unauthorized();
            try
            {
                if (!operators.IsAllowed(principal))
                {
                    await registry.RecordDeniedAsync(actor, traceId, deploymentId, cancellationToken);
                    return Results.Forbid();
                }
                if (!await antiforgery.IsRequestValidAsync(context) || !TryRevision(context, out var revision))
                {
                    await registry.RecordInvalidAsync(actor, traceId, deploymentId, cancellationToken);
                    return Results.Problem(statusCode: 400, title: "A valid antiforgery token and revision are required.", extensions: new Dictionary<string, object?> { ["code"] = "invalid_request", ["traceId"] = traceId });
                }
                var request = await CloudDeploymentRequestReader.ReadAsync(context.Request, cancellationToken);
                if (request is null)
                {
                    await registry.RecordInvalidAsync(actor, traceId, deploymentId, cancellationToken);
                    return Results.Problem(statusCode: 400, title: "Deployment request is invalid.", extensions: new Dictionary<string, object?> { ["code"] = "invalid_request", ["traceId"] = traceId });
                }
                var deployment = new CloudWorkerDeployment
                {
                    Id = deploymentId,
                    CapabilityId = request.CapabilityId ?? "",
                    EndpointReference = request.EndpointReference ?? "",
                    Transport = request.Transport ?? "",
                    Region = request.Region ?? "",
                    ContractVersion = request.ContractVersion ?? "",
                    ModelId = request.ModelId,
                    ModelRevision = request.ModelRevision,
                    ModelDigest = request.ModelDigest,
                    ImageDigest = request.ImageDigest ?? "",
                    ConcurrencyLimit = request.ConcurrencyLimit,
                    Enabled = request.Enabled
                };
                var status = await registry.PutAsync(actor, traceId, deployment, revision, cancellationToken);
                if (status == "succeeded")
                { context.Response.Headers.ETag = $"\"{revision + 1}\""; return Results.NoContent(); }
                return Results.Problem(statusCode: status == "conflict" ? 409 : 422, title: "Deployment change could not be applied.",
                    extensions: new Dictionary<string, object?> { ["code"] = status, ["traceId"] = traceId });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                return Results.Problem(statusCode: 503, title: "Deployment administration is unavailable.",
                    extensions: new Dictionary<string, object?> { ["code"] = "capability_unavailable", ["traceId"] = traceId });
            }
        });
    }

    private static bool TryRevision(HttpContext context, out long revision)
    {
        revision = 0;
        var headers = context.Request.Headers.IfMatch;
        if (headers.Count != 1)
            return false;
        var value = headers.ToString();
        return value.Length > 2 && value[0] == '"' && value[^1] == '"' &&
            long.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out revision) &&
            revision >= 0 && revision < long.MaxValue;
    }
}
