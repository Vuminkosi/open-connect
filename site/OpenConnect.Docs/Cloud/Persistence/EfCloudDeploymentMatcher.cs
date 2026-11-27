using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

internal sealed class EfCloudDeploymentMatcher(IDbContextFactory<CloudControlPlaneDbContext> contextFactory)
{
    internal async Task<bool> MatchesAsync(Guid tenantId, CloudClassificationDeploymentMatchRequest request, string capabilityId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var policy = await db.RegionPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        var catalog = await db.Capabilities.AsNoTracking().SingleOrDefaultAsync(x => x.Id == capabilityId, cancellationToken);
        if (policy is null || catalog is null || !catalog.AllowedRegionsVerified ||
            !(JsonSerializer.Deserialize<string[]>(policy.AllowedRegions) ?? []).Contains(request.Region, StringComparer.Ordinal) ||
            !(JsonSerializer.Deserialize<string[]>(catalog.AllowedRegions) ?? []).Contains(request.Region, StringComparer.Ordinal))
            return false;
        var matches = await db.WorkerDeployments.AsNoTracking().AnyAsync(x => x.Enabled && x.CapabilityId == capabilityId &&
            x.Transport == "https-mtls" && x.EndpointReference == request.EndpointReference && x.Region == request.Region &&
            x.ContractVersion == request.ContractVersion && x.ModelRevision == request.ModelRevision && x.ModelId != null && x.ModelDigest == request.ModelDigest,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return matches;
    }
}
