using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

internal static class EfCloudInferenceDeploymentAdmission
{
    internal static async Task<CloudWorkerDeployment?> LockEligibleAsync(CloudControlPlaneDbContext db, Guid tenantId,
        CloudCapabilitySnapshot capabilities, string modelId, CloudInferenceDeploymentMatch request, CancellationToken cancellationToken)
    {
        var policy = await db.RegionPolicies.FromSqlInterpolated($"SELECT * FROM cloud.region_policies WHERE \"TenantId\" = {tenantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var catalog = capabilities.Capabilities.SingleOrDefault(x => x.Id == "inference.chat");
        if (policy is null || catalog is null || !catalog.AllowedRegionsVerified ||
            !(JsonSerializer.Deserialize<string[]>(policy.AllowedRegions) ?? []).Contains(request.Region, StringComparer.Ordinal) ||
            !(JsonSerializer.Deserialize<string[]>(catalog.AllowedRegions) ?? []).Contains(request.Region, StringComparer.Ordinal))
            return null;
        var deployment = await db.WorkerDeployments.FromSqlInterpolated($"SELECT * FROM cloud.worker_deployments WHERE \"EndpointReference\" = {request.EndpointReference} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return deployment is { Enabled: true, CapabilityId: "inference.chat", Transport: "https-mtls" } &&
            deployment.Region == request.Region && deployment.ContractVersion == request.ContractVersion && deployment.ModelId == modelId &&
            deployment.ModelRevision == request.ModelRevision && deployment.ModelDigest == request.ModelDigest ? deployment : null;
    }
}
