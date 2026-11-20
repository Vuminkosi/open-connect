using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudDeploymentStore(IDbContextFactory<CloudControlPlaneDbContext> contextFactory) : ICloudDeploymentStore
{
    public async Task<IReadOnlyList<CloudWorkerDeployment>> ListAsync(string capabilityId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.WorkerDeployments.AsNoTracking().Where(x => x.CapabilityId == capabilityId).OrderBy(x => x.Id).ToListAsync(cancellationToken);
    }

    public async Task<string> PutAsync(CloudWorkerDeployment deployment, long expectedRevision, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var current = await db.WorkerDeployments.SingleOrDefaultAsync(x => x.Id == deployment.Id, cancellationToken);
        if (expectedRevision == 0)
        {
            if (current is not null) return "conflict";
            deployment.Revision = 1;
            db.WorkerDeployments.Add(deployment);
        }
        else
        {
            if (current is null || current.Revision != expectedRevision) return "conflict";
            db.Entry(current).CurrentValues.SetValues(deployment);
            current.Revision = expectedRevision + 1;
        }
        try { await db.SaveChangesAsync(cancellationToken); return "succeeded"; }
        catch (DbUpdateConcurrencyException) { return "conflict"; }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: "23505" }) { return "conflict"; }
    }
}
