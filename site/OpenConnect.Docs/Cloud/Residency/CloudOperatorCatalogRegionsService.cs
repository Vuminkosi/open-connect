using System.Diagnostics;
using System.Security.Claims;

namespace OpenConnect.Docs.Cloud;

public sealed class CloudOperatorCatalogRegionsService(CloudOperatorAccess access, CloudCatalogRegionsService regions, IConfiguration configuration)
{
    public async Task<CloudCatalogRegionsResult> GetAsync(ClaimsPrincipal principal, string capabilityId, CancellationToken cancellationToken)
    {
        if (!access.IsAllowed(principal)) return new("forbidden");
        var value = await regions.GetAsync(capabilityId, cancellationToken);
        return value is null ? new("not_found") : new("succeeded", value);
    }

    public async Task<CloudCatalogRegionsResult> PutAsync(ClaimsPrincipal principal, string capabilityId, string[] regionsList, bool verified, long revision, CancellationToken cancellationToken)
    {
        var actor = CloudSubjectResolver.FromPrincipal(principal, configuration["OpenConnect:Cloud:Identity:Authority"]);
        if (actor is null) return new("unauthorized");
        var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        if (!access.IsAllowed(principal))
        {
            await regions.RecordAsync(actor, traceId, capabilityId, "forbidden", true, cancellationToken);
            return new("forbidden");
        }
        return await regions.PutAsync(actor, traceId, capabilityId, regionsList, verified, revision, cancellationToken);
    }
}
