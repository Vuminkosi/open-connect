using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Text.Json;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudClassificationDeploymentReader(IDbContextFactory<CloudControlPlaneDbContext> contextFactory) : ICloudClassificationDeploymentReader
{
    public async Task<bool> MatchesAsync(Guid tenantId, CloudClassificationDeploymentMatchRequest request, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var policy = await db.RegionPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        var catalog = await db.Capabilities.AsNoTracking().SingleOrDefaultAsync(x => x.Id == "classification.classify", cancellationToken);
        if (policy is null || catalog is null || !catalog.AllowedRegionsVerified ||
            !(JsonSerializer.Deserialize<string[]>(policy.AllowedRegions) ?? []).Contains(request.Region, StringComparer.Ordinal) ||
            !(JsonSerializer.Deserialize<string[]>(catalog.AllowedRegions) ?? []).Contains(request.Region, StringComparer.Ordinal)) return false;
        var matches = await db.WorkerDeployments.AsNoTracking().AnyAsync(x => x.Enabled && x.CapabilityId == "classification.classify" &&
            x.Transport == "https-mtls" && x.EndpointReference == request.EndpointReference && x.Region == request.Region &&
            x.ContractVersion == request.ContractVersion && x.ModelRevision == request.ModelRevision && x.ModelId != null && x.ModelDigest != null,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return matches;
    }
}
