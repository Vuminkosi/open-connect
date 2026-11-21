using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudKnowledgeDeploymentPostgresTests
{
    [CloudPostgresFact]
    public async Task MatchingRequiresCorrectCapabilityTenantRegionAndEnabledDeployment()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var tenant = await new EfCloudTenantStore(factory, TimeProvider.System)
            .CreateTenantAsync(new CloudSubject("test-issuer", Guid.NewGuid().ToString("N")), "Deployment", default);
        var reference = "embed-" + Guid.NewGuid().ToString("N");
        var digest = "sha256:" + new string('a', 64);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var capability = await db.Capabilities.SingleAsync(x => x.Id == "knowledge.embed");
            capability.AllowedRegions = "[\"region-a\"]"; capability.AllowedRegionsVerified = true;
            db.RegionPolicies.Add(new CloudRegionPolicy { TenantId = tenant.Id, AllowedRegions = "[\"region-a\"]", Revision = 1 });
            db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = Guid.NewGuid(), CapabilityId = "knowledge.embed",
                EndpointReference = reference, Transport = "https-mtls", Region = "region-a", ContractVersion = "openconnect.v1",
                ModelId = "model", ModelRevision = "revision", ModelDigest = digest, ImageDigest = digest, ConcurrencyLimit = 1, Enabled = true });
            await db.SaveChangesAsync();
        }
        var request = new CloudClassificationDeploymentMatchRequest(reference, "region-a", "openconnect.v1", "revision", digest);
        var knowledge = new EfCloudKnowledgeEmbeddingDeploymentReader(factory);
        var classification = new EfCloudClassificationDeploymentReader(factory);
        Assert.True(await knowledge.MatchesAsync(tenant.Id, request, default));
        Assert.False(await knowledge.MatchesAsync(Guid.NewGuid(), request, default));
        Assert.False(await classification.MatchesAsync(tenant.Id, request, default));
        Assert.False(await knowledge.MatchesAsync(tenant.Id, request with { Region = "region-b" }, default));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var deployment = await db.WorkerDeployments.SingleAsync(x => x.EndpointReference == reference);
            deployment.Enabled = false; await db.SaveChangesAsync();
        }
        Assert.False(await knowledge.MatchesAsync(tenant.Id, request, default));
    }
    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
