using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudClassificationDeploymentReader(IDbContextFactory<CloudControlPlaneDbContext> contextFactory) : ICloudClassificationDeploymentReader
{
    public async Task<bool> MatchesAsync(CloudClassificationDeploymentMatchRequest request, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.WorkerDeployments.AsNoTracking().AnyAsync(x => x.Enabled && x.CapabilityId == "classification.classify" &&
            x.Transport == "https-mtls" && x.EndpointReference == request.EndpointReference && x.Region == request.Region &&
            x.ContractVersion == request.ContractVersion && x.ModelRevision == request.ModelRevision && x.ModelId != null && x.ModelDigest != null,
            cancellationToken);
    }
}
