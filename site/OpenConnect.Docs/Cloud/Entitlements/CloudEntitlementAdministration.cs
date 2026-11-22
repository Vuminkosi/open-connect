using System.Diagnostics;
using System.Security.Claims;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudEntitlementChange(bool PlanGranted, DateTimeOffset? ExpiresAt);
public sealed record CloudEntitlementResult(string Status, CloudTenantEntitlement? Entitlement = null);

public interface ICloudEntitlementAdministrationStore
{
    Task<CloudTenantEntitlement?> GetAsync(Guid tenantId, string capabilityId, CancellationToken cancellationToken);
    Task<CloudEntitlementResult> PutAsync(CloudSubject actor, string traceId, Guid tenantId, string capabilityId, CloudEntitlementChange change, int revision, CancellationToken cancellationToken);
    Task RecordAsync(CloudSubject actor, string traceId, Guid tenantId, string capabilityId, string outcome, bool denied, CancellationToken cancellationToken);
}

public sealed class CloudEntitlementAdministration(ICloudEntitlementAdministrationStore store)
{
    public Task<CloudTenantEntitlement?> GetAsync(Guid tenantId, string capabilityId, CancellationToken cancellationToken) =>
        tenantId == Guid.Empty || !CloudDeploymentRegistry.ValidToken(capabilityId, 80) ? Task.FromResult<CloudTenantEntitlement?>(null) : store.GetAsync(tenantId, capabilityId, cancellationToken);

    public Task RecordAsync(CloudSubject actor, string traceId, Guid tenantId, string capabilityId, string outcome, bool denied, CancellationToken cancellationToken) =>
        store.RecordAsync(actor, traceId, tenantId, capabilityId, outcome, denied, cancellationToken);

    public async Task<CloudEntitlementResult> PutAsync(CloudSubject actor, string traceId, Guid tenantId, string capabilityId, CloudEntitlementChange change, int revision, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || !CloudDeploymentRegistry.ValidToken(capabilityId, 80) || revision < 0 || revision == int.MaxValue ||
            change.ExpiresAt is { } expiry && (expiry.Offset != TimeSpan.Zero || expiry.Ticks % 10 != 0))
        {
            await store.RecordAsync(actor, traceId, tenantId, capabilityId, "invalid_request", false, cancellationToken);
            return new("invalid_request");
        }
        return await store.PutAsync(actor, traceId, tenantId, capabilityId, change, revision, cancellationToken);
    }
}

public sealed class CloudOperatorEntitlementAdministration(CloudOperatorAccess access, CloudEntitlementAdministration entitlements, IConfiguration configuration)
{
    public async Task<CloudEntitlementResult> GetAsync(ClaimsPrincipal principal, Guid tenantId, string capabilityId, CancellationToken cancellationToken)
    {
        if (!access.IsAllowed(principal))
            return new("forbidden");
        var value = await entitlements.GetAsync(tenantId, capabilityId, cancellationToken);
        return value is null ? new("not_found") : new("succeeded", value);
    }

    public async Task<CloudEntitlementResult> PutAsync(ClaimsPrincipal principal, Guid tenantId, string capabilityId, CloudEntitlementChange change, int revision, CancellationToken cancellationToken)
    {
        var actor = CloudSubjectResolver.FromPrincipal(principal, configuration["OpenConnect:Cloud:Identity:Authority"]);
        if (actor is null)
            return new("unauthorized");
        var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        if (!access.IsAllowed(principal))
        {
            await entitlements.RecordAsync(actor, traceId, tenantId, capabilityId, "forbidden", true, cancellationToken);
            return new("forbidden");
        }
        return await entitlements.PutAsync(actor, traceId, tenantId, capabilityId, change, revision, cancellationToken);
    }
}
