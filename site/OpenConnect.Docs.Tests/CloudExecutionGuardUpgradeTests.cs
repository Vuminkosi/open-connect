using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudExecutionGuardUpgradeFactAttribute : FactAttribute
{
    public CloudExecutionGuardUpgradeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_GUARD_TEST_DATABASE")))
            Skip = "Set OPENCONNECT_CLOUD_GUARD_TEST_DATABASE to a separate empty disposable PostgreSQL database.";
    }
}

[Collection("Cloud PostgreSQL")]
public sealed class CloudExecutionGuardUpgradeTests
{
    [CloudExecutionGuardUpgradeFact]
    public async Task InconsistentClaimPreventsGuardMigration()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_GUARD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
            await db.GetService<IMigrator>().MigrateAsync("20261120173053_AddCloudExecutionClaims");
        }
        var owner = new CloudSubject("issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Guard upgrade", default);
        var application = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var now = DateTimeOffset.UtcNow;
        var capability = "guard." + Guid.NewGuid().ToString("N");
        var bucket = Guid.NewGuid(); var reservation = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = bucket, TenantId = tenant.Id, ApplicationId = application.Id, CapabilityId = capability, Unit = "request", PeriodStart = now.AddHours(-1), PeriodEnd = now.AddHours(1), Limit = 1 });
            db.QuotaReservations.Add(new CloudQuotaReservation { Id = reservation, BucketId = bucket, TenantId = tenant.Id, ApplicationId = application.Id, RequestId = Guid.NewGuid(), Amount = 1, Actual = 0, FinalizedAt = now, ExpiresAt = now.AddMinutes(5) });
            await db.SaveChangesAsync();
            var claimId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cloud.execution_claims (\"ReservationId\", \"TenantId\", \"ApplicationId\", \"ClaimId\", \"ClaimedAt\") VALUES ({reservation}, {tenant.Id}, {application.Id}, {claimId}, {now})");
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.MigrateAsync());
        }
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.DoesNotContain("20261120173456_GuardClaimedReservationFinalization", await db.Database.GetAppliedMigrationsAsync());
            Assert.True(await db.ExecutionClaims.Where(x => x.ReservationId == reservation).Select(x => x.ReservationId).AnyAsync());
        }
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
