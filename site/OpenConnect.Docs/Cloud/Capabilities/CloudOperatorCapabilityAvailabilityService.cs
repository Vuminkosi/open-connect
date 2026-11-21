using System.Diagnostics;
using System.Security.Claims;

namespace OpenConnect.Docs.Cloud;

public sealed class CloudOperatorCapabilityAvailabilityService(
    CloudOperatorAccess access, CloudCapabilityAvailabilityService availability, IConfiguration configuration)
{
    public async Task<CloudCapabilityAvailabilityResult> GetAsync(ClaimsPrincipal principal, string capabilityId, CancellationToken cancellationToken)
    {
        if (!access.IsAllowed(principal)) return new("forbidden");
        var value = await availability.GetAsync(capabilityId, cancellationToken);
        return value is null ? new("not_found") : new("succeeded", value);
    }

    public async Task<CloudCapabilityAvailabilityResult> PutAsync(ClaimsPrincipal principal, string capabilityId,
        string state, long revision, CancellationToken cancellationToken)
    {
        var actor = CloudSubjectResolver.FromPrincipal(principal, configuration["OpenConnect:Cloud:Identity:Authority"]);
        if (actor is null) return new("unauthorized");
        var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        if (!access.IsAllowed(principal))
        {
            await availability.RecordAsync(actor, traceId, capabilityId, "forbidden", true, cancellationToken);
            return new("forbidden");
        }
        return await availability.PutAsync(actor, traceId, capabilityId, state, revision, cancellationToken);
    }
}
