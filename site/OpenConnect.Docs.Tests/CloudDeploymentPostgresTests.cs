using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudDeploymentPostgresTests
{
    [CloudPostgresFact]
    public async Task InventorySupportsMultipleDeploymentsAndRevisionConflicts()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var capability = "test." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "unavailable", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            await db.SaveChangesAsync();
        }
        var registry = new CloudDeploymentRegistry(new EfCloudDeploymentStore(factory));
        CloudWorkerDeployment Deployment(string endpoint) => new() { Id = Guid.NewGuid(), CapabilityId = capability,
            EndpointReference = endpoint, Transport = "https-mtls", Region = "region-a", ContractVersion = "v1",
            ImageDigest = "sha256:" + new string('a', 64), ConcurrencyLimit = 10, Enabled = false };
        var first = Deployment("worker-a-" + Guid.NewGuid().ToString("N"));
        var second = Deployment("worker-b-" + Guid.NewGuid().ToString("N"));
        Assert.Equal("succeeded", await registry.PutAsync(first, 0, CancellationToken.None));
        Assert.Equal("succeeded", await registry.PutAsync(second, 0, CancellationToken.None));
        Assert.Equal(2, (await registry.ListAsync(capability, CancellationToken.None)).Count);
        Assert.Equal("conflict", await registry.PutAsync(first, 0, CancellationToken.None));
        first.Enabled = true;
        Assert.Equal("succeeded", await registry.PutAsync(first, 1, CancellationToken.None));
        Assert.Equal("conflict", await registry.PutAsync(first, 1, CancellationToken.None));
        Assert.Equal(2, (await registry.ListAsync(capability, CancellationToken.None)).Single(x => x.Id == first.Id).Revision);
        var duplicate = Deployment(first.EndpointReference);
        Assert.Equal("conflict", await registry.PutAsync(duplicate, 0, CancellationToken.None));
        var invalid = Deployment("https://attacker.example");
        Assert.Equal("invalid_request", await registry.PutAsync(invalid, 0, CancellationToken.None));
        invalid.EndpointReference = "worker-c";
        invalid.ModelId = "model-without-digest";
        Assert.Equal("invalid_request", await registry.PutAsync(invalid, 0, CancellationToken.None));
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
