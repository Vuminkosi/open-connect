using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudWorkerExpansionReadinessPostgresTests
{
    [CloudPostgresFact]
    public async Task EnabledExpansionDeploymentsAreReadyAndDisabledOnesAreNot()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            foreach (var (id, capability) in ids.Zip(new[] { "analytics.analyze", "knowledge.embed" }))
                db.WorkerDeployments.Add(new CloudWorkerDeployment
                {
                    Id = id,
                    CapabilityId = capability,
                    EndpointReference = "readiness-" + id.ToString("N"),
                    Transport = "https-mtls",
                    Region = "region-a",
                    ContractVersion = "openconnect.v1",
                    ImageDigest = "sha256:" + new string('a', 64),
                    Enabled = true,
                    ConcurrencyLimit = 1
                });
            await db.SaveChangesAsync();
        }
        var readiness = new EfCloudWorkerReadinessStore(factory);
        foreach (var id in ids)
            Assert.True(await readiness.IsReadyAsync(id, CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
            await db.WorkerDeployments.Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, false));
        foreach (var id in ids)
            Assert.False(await readiness.IsReadyAsync(id, CancellationToken.None));
    }

    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
