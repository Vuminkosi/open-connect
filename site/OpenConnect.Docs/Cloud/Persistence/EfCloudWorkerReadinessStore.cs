using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudWorkerReadinessStore(IDbContextFactory<CloudControlPlaneDbContext> factory) : ICloudWorkerReadinessStore
{
    public async Task<bool> IsReadyAsync(Guid deploymentId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Database.CanConnectAsync(cancellationToken) &&
            !(await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any() &&
            await db.WorkerDeployments.AsNoTracking().AnyAsync(x => x.Id == deploymentId && x.Enabled &&
                (x.CapabilityId == "classification.classify" || x.CapabilityId == "inference.chat"), cancellationToken);
    }
}
