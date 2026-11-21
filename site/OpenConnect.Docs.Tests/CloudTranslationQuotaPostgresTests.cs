using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudTranslationQuotaPostgresTests
{
    [CloudPostgresFact]
    public async Task ReservationRequiresLatestMatchingPrivacyEvidenceAndRespectsQuotaUnderConcurrency()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("https://issuer.example", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Translation quota", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "Translation app", "Development", default)).Application!;
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), tenant.Id, app.Id, "Development", new HashSet<string> { "translation.translate" });
        var now = DateTimeOffset.UtcNow; var bucketId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = bucketId, TenantId = tenant.Id, ApplicationId = app.Id,
                CapabilityId = "translation.translate", Unit = "character", PeriodStart = now.AddHours(-1), PeriodEnd = now.AddHours(1), Limit = 20 });
            await db.SaveChangesAsync();
        }
        var quota = new CloudTranslationQuota(new EfCloudTranslationQuotaStore(factory, TimeProvider.System), TimeProvider.System);
        var evidence = new EfCloudTranslationPrivacyEvidenceStore(factory, TimeProvider.System);
        var requests = Enumerable.Range(0, 10).Select(_ => new CloudQuotaRequest(tenant.Id, app.Id, "translation.translate", "character",
            Guid.NewGuid(), "active", new byte[32], 4, now.AddMinutes(1))).ToArray();
        Assert.Equal("privacy_unavailable", (await quota.ReserveAsync(requests[0], default)).Status);
        foreach (var request in requests)
            await evidence.AppendAsync(identity, request.RequestId, "active", request.RequestFingerprint, 4, "allowed", new string('a', 32), default);
        Assert.Equal("privacy_unavailable", (await quota.ReserveAsync(requests[0] with { Amount = 5 }, default)).Status);
        Assert.Equal("privacy_unavailable", (await quota.ReserveAsync(requests[0] with { RequestFingerprint = Enumerable.Repeat((byte)1, 32).ToArray() }, default)).Status);
        var results = await Task.WhenAll(requests.Select(request => quota.ReserveAsync(request, default)));
        Assert.Equal(5, results.Count(result => result.Status == "reserved"));
        Assert.Equal(5, results.Count(result => result.Status == "quota_exceeded"));
        var accepted = requests[Array.FindIndex(results, result => result.Status == "reserved")];
        Assert.Equal("duplicate", (await quota.ReserveAsync(accepted, default)).Status);
        await evidence.AppendAsync(identity, accepted.RequestId, "active", accepted.RequestFingerprint, 4, "privacy_blocked", new string('a', 32), default);
        Assert.Equal("privacy_unavailable", (await quota.ReserveAsync(accepted, default)).Status);
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
