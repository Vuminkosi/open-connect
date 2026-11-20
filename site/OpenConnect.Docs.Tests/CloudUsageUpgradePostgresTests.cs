using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudUsageUpgradeFactAttribute : FactAttribute
{
    public CloudUsageUpgradeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_UPGRADE_TEST_DATABASE")))
            Skip = "Set OPENCONNECT_CLOUD_UPGRADE_TEST_DATABASE to a separate empty disposable PostgreSQL database.";
    }
}

[Collection("Cloud PostgreSQL")]
public sealed class CloudUsageUpgradePostgresTests
{
    [CloudUsageUpgradeFact]
    public async Task ExistingFinalizationIsBackfilledAndOldWritersProduceUsage()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_UPGRADE_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
            await db.GetService<IMigrator>().MigrateAsync("20261120135737_AddQuotaFingerprintKeyVersion");
        }
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Upgrade test", CancellationToken.None);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", CancellationToken.None)).Application!;
        var capability = "test." + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var bucketId = Guid.NewGuid();
        var oldId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = bucketId, TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = capability,
                Unit = "request", PeriodStart = now.AddHours(-1), PeriodEnd = now.AddHours(1), Limit = 10, Consumed = 2, Reserved = 1 });
            db.QuotaReservations.AddRange(
                new CloudQuotaReservation { Id = oldId, BucketId = bucketId, TenantId = tenant.Id, ApplicationId = app.Id, RequestId = Guid.NewGuid(), Amount = 2, Actual = 2, FinalizedAt = now, ExpiresAt = now.AddMinutes(5) },
                new CloudQuotaReservation { Id = pendingId, BucketId = bucketId, TenantId = tenant.Id, ApplicationId = app.Id, RequestId = Guid.NewGuid(), Amount = 1, ExpiresAt = now.AddMinutes(5) });
            await db.SaveChangesAsync();
            await db.Database.MigrateAsync();
        }
        await using (var db = await factory.CreateDbContextAsync())
        {
            var legacy = await db.UsageRecords.AsNoTracking().SingleAsync(x => x.ReservationId == oldId);
            Assert.Equal("legacy_reservation", legacy.Provenance);
            Assert.Equal(2, legacy.Quantity);
            Assert.Null(legacy.CapabilityId);
            Assert.Null(legacy.Unit);
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.QuotaReservations.Where(x => x.Id == pendingId).ExecuteUpdateAsync(x => x.SetProperty(y => y.Actual, 1L).SetProperty(y => y.FinalizedAt, DateTimeOffset.UtcNow));
            await db.QuotaBuckets.Where(x => x.Id == bucketId).ExecuteUpdateAsync(x => x.SetProperty(y => y.Reserved, y => y.Reserved - 1).SetProperty(y => y.Consumed, y => y.Consumed + 1));
            await transaction.CommitAsync();
            var current = await db.UsageRecords.AsNoTracking().SingleAsync(x => x.ReservationId == pendingId);
            Assert.Equal("reservation", current.Provenance);
            Assert.Equal(1, current.Quantity);
            Assert.Equal(capability, current.CapabilityId);
            Assert.Equal("request", current.Unit);
        }
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
