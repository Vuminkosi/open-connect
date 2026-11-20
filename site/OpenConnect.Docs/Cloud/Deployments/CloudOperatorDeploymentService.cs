using System.Diagnostics;
using System.Security.Claims;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudOperatorDeploymentPage(string Status, IReadOnlyList<CloudWorkerDeployment>? Deployments = null, int? NextOffset = null);

public sealed class CloudOperatorDeploymentService(CloudOperatorAccess access, CloudDeploymentRegistry registry, IConfiguration configuration)
{
    public async Task<CloudOperatorDeploymentPage> ListAsync(ClaimsPrincipal principal, string capability, int offset, int limit, CancellationToken cancellationToken)
    {
        if (!access.IsAllowed(principal)) return new("forbidden");
        if (!CloudDeploymentRegistry.ValidToken(capability, 80) || offset is < 0 or > 1000000 || limit is < 1 or > 100) return new("invalid_request");
        var records = await registry.ListAsync(capability, offset, limit + 1, cancellationToken);
        return new("succeeded", records.Take(limit).ToArray(), records.Count > limit ? offset + limit : null);
    }

    public async Task<string> PutAsync(ClaimsPrincipal principal, CloudWorkerDeployment deployment, long revision, CancellationToken cancellationToken)
    {
        var actor = CloudSubjectResolver.FromPrincipal(principal, configuration["OpenConnect:Cloud:Identity:Authority"]);
        if (actor is null) return "unauthorized";
        var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        if (!access.IsAllowed(principal))
        {
            await registry.RecordDeniedAsync(actor, traceId, deployment.Id, cancellationToken);
            return "forbidden";
        }
        return await registry.PutAsync(actor, traceId, deployment, revision, cancellationToken);
    }
}
