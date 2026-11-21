using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudClassificationDeploymentReader(IDbContextFactory<CloudControlPlaneDbContext> contextFactory) : ICloudClassificationDeploymentReader
{
    public Task<bool> MatchesAsync(Guid tenantId, CloudClassificationDeploymentMatchRequest request, CancellationToken cancellationToken) =>
        new EfCloudDeploymentMatcher(contextFactory).MatchesAsync(tenantId, request, "classification.classify", cancellationToken);
}
