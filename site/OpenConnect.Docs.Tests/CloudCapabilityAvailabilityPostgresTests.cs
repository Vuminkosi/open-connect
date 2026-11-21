using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudCapabilityAvailabilityPostgresTests
{
    [CloudPostgresFact]
    public async Task AvailabilityRequiresDeploymentAndFencesOlderAndConcurrentWriters()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        var capability = "availability." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Test", Description = "Test",
                ContractVersion = "v1", BillingUnit = "request", PlatformAvailability = "not_configured",
                Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            await db.SaveChangesAsync();
        }
        var service = new CloudCapabilityAvailabilityService(new EfCloudCapabilityAvailabilityStore(factory, TimeProvider.System));
        var actor = new CloudSubject("https://operator.example", "operator");
        var trace = Guid.NewGuid().ToString("N");
        Assert.Equal(1, (await service.GetAsync(capability, default))!.Revision);
        Assert.Equal("deployment_required", (await service.PutAsync(actor, trace, capability, "available", 1, default)).Status);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = Guid.NewGuid(), CapabilityId = capability,
                EndpointReference = "test", Transport = "https-mtls", Region = "test", ContractVersion = "v1",
                ImageDigest = "sha256:" + new string('a', 64), Enabled = true, ConcurrencyLimit = 1 });
            await db.SaveChangesAsync();
        }
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => service.PutAsync(actor, trace, capability, "available", 1, default)));
        Assert.Single(concurrent, x => x.Status == "succeeded");
        Assert.Single(concurrent, x => x.Status == "conflict");
        Assert.Equal(2, (await service.GetAsync(capability, default))!.Revision);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Capabilities.Where(x => x.Id == capability).ExecuteUpdateAsync(x => x.SetProperty(y => y.PlatformAvailability, "not_configured"));
        Assert.Equal(3, (await service.GetAsync(capability, default))!.Revision);
        Assert.Equal("conflict", (await service.PutAsync(actor, trace, capability, "available", 2, default)).Status);
        Assert.Equal("invalid_request", (await service.PutAsync(actor, trace, capability, "unknown", 3, default)).Status);
        var unchanged = await service.PutAsync(actor, trace, capability, "not_configured", 3, default);
        Assert.Equal("succeeded", unchanged.Status);
        Assert.Equal(3, unchanged.Availability!.Revision);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var events = await db.AuditEvents.Where(x => x.TraceId == trace && x.Action == "catalog.availability.changed").ToListAsync();
            Assert.Contains(events, x => x.Outcome == "deployment_required");
            Assert.Contains(events, x => x.Outcome == "succeeded");
            Assert.Contains(events, x => x.Outcome == "conflict");
            Assert.Contains(events, x => x.Outcome == "invalid_request");
        }
    }

    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
