using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudVoiceQuotaPostgresTests
{
    [CloudPostgresFact]
    public async Task CharacterReservationsAreAtomicScopedAndIdempotent()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        var owner = new CloudSubject("https://issuer.example", Guid.NewGuid().ToString("N"));
        var tenantStore = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenantStore.CreateTenantAsync(owner, "Voice quota", CancellationToken.None);
        var app = (await tenantStore.CreateApplicationAsync(owner, tenant.Id, "Voice app", "Development", CancellationToken.None)).Application!;
        var now = DateTimeOffset.UtcNow;
        var bucketId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaBuckets.Add(new CloudQuotaBucket
            {
                Id = bucketId,
                TenantId = tenant.Id,
                ApplicationId = app.Id,
                CapabilityId = "voice.synthesize",
                Unit = "character",
                PeriodStart = now.AddHours(-1),
                PeriodEnd = now.AddHours(1),
                Limit = 20
            });
            await db.SaveChangesAsync();
        }
        var quota = new CloudQuotaService(new EfCloudQuotaStore(factory), TimeProvider.System);
        var expiry = now.AddMinutes(1);
        var requests = Enumerable.Range(0, 20).Select(_ => new CloudQuotaRequest(tenant.Id, app.Id, "voice.synthesize",
            "character", Guid.NewGuid(), "active", new byte[32], 4, expiry)).ToArray();
        var results = await Task.WhenAll(requests.Select(item => quota.ReserveAsync(item, CancellationToken.None)));
        Assert.Equal(5, results.Count(x => x.Status == "reserved"));
        Assert.Equal(15, results.Count(x => x.Status == "quota_exceeded"));
        var index = Array.FindIndex(results, x => x.Status == "reserved");
        Assert.Equal("duplicate", (await quota.ReserveAsync(requests[index], CancellationToken.None)).Status);
        Assert.Equal("idempotency_conflict", (await quota.ReserveAsync(requests[index] with { Amount = 5 }, CancellationToken.None)).Status);
        Assert.Equal("quota_unavailable", (await quota.ReserveAsync(requests[index] with { TenantId = Guid.NewGuid(), RequestId = Guid.NewGuid() }, CancellationToken.None)).Status);
        await using var verify = await factory.CreateDbContextAsync();
        var bucket = await verify.QuotaBuckets.SingleAsync(x => x.Id == bucketId);
        Assert.Equal(20, bucket.Reserved);
        Assert.Equal(0, bucket.Consumed);
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
