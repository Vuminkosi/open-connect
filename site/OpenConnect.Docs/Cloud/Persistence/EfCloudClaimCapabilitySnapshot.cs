using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

internal static class EfCloudClaimCapabilitySnapshot
{
    // Hold read locks through claim commit so an acknowledged disable or entitlement revocation
    // cannot be followed by a claim based on the preceding capability state.
    internal static async Task<CloudCapabilitySnapshot> LockAsync(CloudControlPlaneDbContext db, Guid tenantId, Guid applicationId, CancellationToken cancellationToken)
    {
        var catalog = await db.Capabilities.FromSqlRaw("SELECT * FROM cloud.capabilities ORDER BY \"Id\" FOR SHARE").AsNoTracking().ToListAsync(cancellationToken);
        var entitlements = await db.TenantEntitlements.FromSqlInterpolated($"SELECT * FROM cloud.tenant_entitlements WHERE \"TenantId\" = {tenantId} ORDER BY \"CapabilityId\" FOR SHARE").AsNoTracking().ToListAsync(cancellationToken);
        var tenantSelections = await db.TenantCapabilitySelections.FromSqlInterpolated($"SELECT * FROM cloud.tenant_capability_selections WHERE \"TenantId\" = {tenantId} ORDER BY \"CapabilityId\" FOR SHARE").AsNoTracking().ToListAsync(cancellationToken);
        var appSelections = await db.ApplicationCapabilitySelections.FromSqlInterpolated($"SELECT * FROM cloud.application_capability_selections WHERE \"TenantId\" = {tenantId} AND \"ApplicationId\" = {applicationId} ORDER BY \"CapabilityId\" FOR SHARE").AsNoTracking().ToListAsync(cancellationToken);
        return new(catalog, entitlements.ToDictionary(x => x.CapabilityId, StringComparer.Ordinal),
            tenantSelections.ToDictionary(x => x.CapabilityId, StringComparer.Ordinal), appSelections.ToDictionary(x => x.CapabilityId, StringComparer.Ordinal));
    }
}
