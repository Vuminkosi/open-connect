using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudTranslationDeploymentProfileTests
{
    [CloudPostgresFact]
    public async Task ProfileUpdatesAreRevisionFencedAndBoundToDeploymentRevision()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var deploymentId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = deploymentId, CapabilityId = "translation.translate",
                EndpointReference = "nllb-" + Guid.NewGuid().ToString("N"), Transport = "https-mtls", Region = "region-a",
                ContractVersion = "v1", ModelId = "nllb", ModelRevision = "revision-1", ModelDigest = "sha256:" + new string('a', 64),
                ImageDigest = "sha256:" + new string('b', 64), ConcurrencyLimit = 1, Enabled = true, Revision = 1 });
            await db.SaveChangesAsync();
        }
        var service = new CloudTranslationDeploymentProfiles(new EfCloudTranslationDeploymentProfileStore(factory, TimeProvider.System));
        var actor = new CloudSubject("https://issuer.example", Guid.NewGuid().ToString("N"));
        var profile = new CloudTranslationDeploymentProfile(deploymentId, 1, 0, new string('a', 64), [new("eng_Latn", "fra_Latn")]);
        Assert.Equal("invalid_request", await service.PutAsync(actor, "trace", profile with { TokenizerHash = "invalid" }, 0, default));
        Assert.Equal("invalid_request", await service.PutAsync(actor, "trace", profile with { LanguagePairs = [new("eng_Latn", "eng_Latn")] }, 0, default));
        Assert.Equal("succeeded", await service.PutAsync(actor, "trace", profile, 0, default));
        var stored = await service.GetAsync(deploymentId, default);
        Assert.NotNull(stored); Assert.Equal(1, stored.Revision); Assert.Equal(1, stored.DeploymentRevision);
        Assert.Equal(profile.LanguagePairs, stored.LanguagePairs);
        Assert.Equal("conflict", await service.PutAsync(actor, "trace", profile, 0, default));
        var changed = profile with { TokenizerHash = new string('b', 64), LanguagePairs = [new("fra_Latn", "eng_Latn")] };
        var results = await Task.WhenAll(service.PutAsync(actor, "trace", changed, 1, default), service.PutAsync(actor, "trace", changed, 1, default));
        Assert.Single(results, result => result == "succeeded"); Assert.Single(results, result => result == "conflict");
        stored = await service.GetAsync(deploymentId, default);
        Assert.NotNull(stored); Assert.Equal(2, stored.Revision); Assert.Equal(changed.TokenizerHash, stored.TokenizerHash);
        Assert.Equal(changed.LanguagePairs, stored.LanguagePairs);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var deployment = await db.WorkerDeployments.SingleAsync(x => x.Id == deploymentId);
            deployment.Revision = 2;
            await db.SaveChangesAsync();
        }
        Assert.Equal("conflict", await service.PutAsync(actor, "trace", changed, 2, default));
        Assert.Equal("succeeded", await service.PutAsync(actor, "trace", changed with { DeploymentRevision = 2 }, 2, default));
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
