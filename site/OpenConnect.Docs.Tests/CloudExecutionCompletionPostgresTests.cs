using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudExecutionCompletionPostgresTests
{
    [CloudPostgresFact]
    public async Task CompletionRollsBackWhenReservedBalanceIsUnavailable()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Completion test", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var now = DateTimeOffset.UtcNow; var bucketId = Guid.NewGuid(); var reservationId = Guid.NewGuid(); var claimId = Guid.NewGuid(); var deploymentId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = bucketId, TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "classification.classify", Unit = "request", PeriodStart = now.AddMinutes(-1), PeriodEnd = now.AddHours(1), Limit = 1, Reserved = 0 });
            db.QuotaReservations.Add(new CloudQuotaReservation { Id = reservationId, TenantId = tenant.Id, ApplicationId = app.Id, BucketId = bucketId, RequestId = Guid.NewGuid(), Amount = 1, ExpiresAt = now.AddMinutes(5), FingerprintKeyId = "key", RequestFingerprint = new byte[32] });
            db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = deploymentId, CapabilityId = "classification.classify", EndpointReference = "completion-" + Guid.NewGuid().ToString("N"), Transport = "https-mtls", Region = "region-a", ContractVersion = "v1", ImageDigest = "sha256:" + new string('a', 64), ConcurrencyLimit = 1, Enabled = false });
            db.ExecutionClaims.Add(new CloudExecutionClaim { ReservationId = reservationId, TenantId = tenant.Id, ApplicationId = app.Id, ClaimId = claimId, DeploymentId = deploymentId, Region = "region-a", ClaimedAt = now });
            await db.SaveChangesAsync();
        }
        var completions = new CloudExecutionCompletionService(new EfCloudExecutionCompletionStore(factory, TimeProvider.System));
        Assert.Equal("unavailable", (await completions.CompleteClassificationAsync(tenant.Id, app.Id, reservationId, claimId, "completed", default)).Status);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.False(await db.ExecutionCompletions.AnyAsync(x => x.ReservationId == reservationId));
            Assert.False(await db.UsageRecords.AnyAsync(x => x.ReservationId == reservationId));
            Assert.Null((await db.QuotaReservations.SingleAsync(x => x.Id == reservationId)).FinalizedAt);
            await db.QuotaBuckets.Where(x => x.Id == bucketId).ExecuteUpdateAsync(x => x.SetProperty(y => y.Reserved, 1L));
        }
        Assert.Equal("completed", (await completions.CompleteClassificationAsync(tenant.Id, app.Id, reservationId, claimId, "completed", default)).Status);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var bucket = await db.QuotaBuckets.SingleAsync(x => x.Id == bucketId);
            Assert.Equal(0, bucket.Reserved); Assert.Equal(1, bucket.Consumed);
            Assert.Equal(1, (await db.UsageRecords.SingleAsync(x => x.ReservationId == reservationId)).Quantity);
        }
        var legacyReservation = Guid.NewGuid(); var legacyClaim = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.QuotaBuckets.Where(x => x.Id == bucketId).ExecuteUpdateAsync(x => x.SetProperty(y => y.Limit, 2L).SetProperty(y => y.Reserved, 1L));
            db.QuotaReservations.Add(new CloudQuotaReservation { Id = legacyReservation, TenantId = tenant.Id, ApplicationId = app.Id, BucketId = bucketId, RequestId = Guid.NewGuid(), Amount = 1, ExpiresAt = now.AddMinutes(5), FingerprintKeyId = "key", RequestFingerprint = new byte[32] });
            db.ExecutionClaims.Add(new CloudExecutionClaim { ReservationId = legacyReservation, TenantId = tenant.Id, ApplicationId = app.Id, ClaimId = legacyClaim, ClaimedAt = now });
            await db.SaveChangesAsync();
        }
        Assert.Equal("unavailable", (await completions.CompleteClassificationAsync(tenant.Id, app.Id, legacyReservation, legacyClaim, "completed", default)).Status);
        await using (var db = await factory.CreateDbContextAsync())
            Assert.False(await db.UsageRecords.AnyAsync(x => x.ReservationId == legacyReservation));
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
