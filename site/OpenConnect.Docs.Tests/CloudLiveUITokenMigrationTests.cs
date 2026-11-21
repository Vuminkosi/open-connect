using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudLiveUITokenUpgradeFactAttribute : FactAttribute
{
    public CloudLiveUITokenUpgradeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_UPGRADE_TEST_DATABASE")))
            Skip = "Requires a dedicated empty PostgreSQL database for migration verification.";
    }
}

public sealed class CloudLiveUITokenMigrationTests
{
    [CloudLiveUITokenUpgradeFact]
    public async Task UpgradePreservesLegacyReservationsAndAllowsOldWriters()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_UPGRADE_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
            await db.Database.GetService<IMigrator>().MigrateAsync("20261121133000_AddLiveUIOutputPrivacyEvidence");
        }
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Migration fixture", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var now = DateTimeOffset.UtcNow; var bucketId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = bucketId, TenantId = tenant.Id, ApplicationId = app.Id,
                CapabilityId = "inference.liveui", Unit = "plan", PeriodStart = now.AddMinutes(-1), PeriodEnd = now.AddHours(1), Limit = 5 });
            await db.SaveChangesAsync();
        }
        var legacy = new CloudQuotaService(new EfCloudQuotaStore(factory), TimeProvider.System);
        CloudQuotaRequest Request(Guid id) => new(tenant.Id, app.Id, "inference.liveui", "plan", id, "key", new byte[32], 1, now.AddMinutes(5));
        var before = await legacy.ReserveAsync(Request(Guid.NewGuid()), default);
        Assert.Equal("reserved", before.Status);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var after = await legacy.ReserveAsync(Request(Guid.NewGuid()), default);
        Assert.Equal("reserved", after.Status);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(2, (await verify.QuotaBuckets.SingleAsync(item => item.Id == bucketId)).Reserved);
        Assert.Equal(2, await verify.QuotaReservations.CountAsync(item => item.TenantId == tenant.Id && item.ApplicationId == app.Id));
        Assert.Equal(0, await verify.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.liveui_token_reservations WHERE \"TenantId\" = {tenant.Id}").SingleAsync());
    }

    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
