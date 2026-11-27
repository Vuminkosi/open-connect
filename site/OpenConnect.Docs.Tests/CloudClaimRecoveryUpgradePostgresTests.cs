using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudUpgradePostgresFactAttribute : FactAttribute
{
    public CloudUpgradePostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_UPGRADE_TEST_DATABASE")))
            Skip = "Set OPENCONNECT_CLOUD_UPGRADE_TEST_DATABASE to a fresh disposable PostgreSQL database.";
    }
}

[Collection("Cloud PostgreSQL")]
public sealed class CloudClaimRecoveryUpgradePostgresTests
{
    [CloudUpgradePostgresFact]
    public async Task ExistingConsumedAndUnconsumedClaimsSurviveUpgradeAndAreFenced()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_UPGRADE_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
            await db.GetService<IMigrator>().MigrateAsync("20261120210000_IndexCloudUsageRecords");
        }
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Upgrade test", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var now = DateTimeOffset.UtcNow;
        var bucket = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var consumed = Guid.NewGuid();
        var racing = Guid.NewGuid();
        var pendingClaim = Guid.NewGuid();
        var consumedClaim = Guid.NewGuid();
        var racingClaim = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaBuckets.Add(new CloudQuotaBucket
            {
                Id = bucket,
                TenantId = tenant.Id,
                ApplicationId = app.Id,
                CapabilityId = "classification.classify",
                Unit = "request",
                PeriodStart = now.AddHours(-1),
                PeriodEnd = now.AddHours(1),
                Limit = 10,
                Reserved = 3
            });
            db.WorkerDeployments.Add(new CloudWorkerDeployment
            {
                Id = deployment,
                CapabilityId = "classification.classify",
                EndpointReference = "upgrade-" + Guid.NewGuid().ToString("N"),
                Transport = "https-mtls",
                Region = "region-a",
                ContractVersion = "openconnect.v1",
                ImageDigest = "sha256:" + new string('a', 64),
                Enabled = true,
                ConcurrencyLimit = 10
            });
            await db.SaveChangesAsync();
            foreach (var id in new[] { pending, consumed, racing })
                db.QuotaReservations.Add(new CloudQuotaReservation
                {
                    Id = id,
                    BucketId = bucket,
                    TenantId = tenant.Id,
                    ApplicationId = app.Id,
                    RequestId = Guid.NewGuid(),
                    Amount = 1,
                    ExpiresAt = now.AddMinutes(-10)
                });
            await db.SaveChangesAsync();
            foreach (var pair in new[] { (pending, pendingClaim), (consumed, consumedClaim), (racing, racingClaim) })
                db.ExecutionClaims.Add(new CloudExecutionClaim
                {
                    ReservationId = pair.Item1,
                    ClaimId = pair.Item2,
                    TenantId = tenant.Id,
                    ApplicationId = app.Id,
                    DeploymentId = deployment,
                    Region = "region-a",
                    WorkerPayloadFingerprint = new byte[32],
                    ClaimedAt = now.AddMinutes(-20)
                });
            await db.SaveChangesAsync();
            db.ExecutionConsumptions.Add(new CloudExecutionConsumption { ReservationId = consumed, ClaimId = consumedClaim, DeploymentId = deployment, ConsumedAt = now.AddMinutes(-9) });
            await db.SaveChangesAsync();
        }
        // An older writer is allowed to finish before the migration installs its fence.
        await using (var oldWriter = await factory.CreateDbContextAsync())
        await using (var upgrading = await factory.CreateDbContextAsync())
        await using (var observer = await factory.CreateDbContextAsync())
        {
            await using var transaction = await oldWriter.Database.BeginTransactionAsync();
            oldWriter.ExecutionConsumptions.Add(new CloudExecutionConsumption { ReservationId = racing, ClaimId = racingClaim, DeploymentId = deployment, ConsumedAt = now });
            await oldWriter.SaveChangesAsync();
            var upgrade = upgrading.Database.MigrateAsync();
            var waiting = false;
            for (var attempt = 0; attempt < 100 && !waiting; attempt++)
            {
                waiting = await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%LOCK TABLE cloud.quota_reservations%') AS \"Value\"").SingleAsync();
                if (!waiting)
                    await Task.Delay(20);
            }
            Assert.True(waiting);
            await transaction.CommitAsync();
            await upgrade;
        }
        var recovery = new EfCloudUnconsumedClaimRecoveryStore(factory, TimeProvider.System);
        Assert.Equal(1, await recovery.ReleaseAsync(now.AddMinutes(-5), 100, default));
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.NotNull((await db.QuotaReservations.SingleAsync(x => x.Id == pending)).FinalizedAt);
            Assert.Null((await db.QuotaReservations.SingleAsync(x => x.Id == consumed)).FinalizedAt);
            Assert.True(await db.ExecutionConsumptions.AnyAsync(x => x.ReservationId == consumed));
            Assert.True(await db.ExecutionConsumptions.AnyAsync(x => x.ReservationId == racing));
            Assert.Null((await db.QuotaReservations.SingleAsync(x => x.Id == racing)).FinalizedAt);
            Assert.Equal(2, (await db.QuotaBuckets.SingleAsync(x => x.Id == bucket)).Reserved);
        }
        await using (var oldWriter = await factory.CreateDbContextAsync())
        {
            oldWriter.ExecutionConsumptions.Add(new CloudExecutionConsumption { ReservationId = pending, ClaimId = pendingClaim, DeploymentId = deployment, ConsumedAt = now });
            await Assert.ThrowsAsync<DbUpdateException>(() => oldWriter.SaveChangesAsync());
        }
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
